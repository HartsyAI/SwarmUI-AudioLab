using System;
using System.Collections.Generic;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Codecs.Mimi;
using HartsyInference.Audio.Models.Kyutai;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Kyutai TTS (kyutai/tts-1.6b-en_fr) — a Moshi delayed-streams model: the Helium temporal backbone
/// steps once per 12.5 Hz frame over the summed text + previous-audio embeddings while the depformer emits the
/// 32 Mimi codebooks, which the Mimi codec decodes to 24 kHz. Provider id <c>kyutaitts_tts</c>. Voice is a
/// pre-embedded speaker (<see cref="MoshiConditioner"/> cross-attention), selected from the
/// <c>kyutai/tts-voices</c> repo (default <c>expresso/ex03-...</c>); a user-uploaded reference clip is not used
/// — this checkpoint conditions on its own voice embeddings, not raw wavs.
///
/// <para>Synthesis mirrors moshi's <c>script_to_entries</c> exactly: one entry per word, SentencePiece-tokenized,
/// with per-word articulation padding (<c>padding_between=1</c>) and the <c>Main</c> speaker token prepended to
/// the first word; the CFG-distilled guidance coefficient (2.0) is a conditioning input, not a second forward.
/// This is the in-engine path verified word-correct end-to-end (<c>KyutaiTtsEndToEndTests</c>).</para></summary>
public static class KyutaiTtsModel
{
    private const string Repo = "kyutai/tts-1.6b-en_fr";
    private const string VoicesRepo = "kyutai/tts-voices";
    // Checkpoint-pinned asset names for this repo revision (the DSM release uses hashed filenames, not the
    // HF-transformers model.safetensors layout). The '1e68beda@240' tag pins the voice-embedding version to the
    // backbone it was trained against — voices from tts-voices carry the same suffix.
    private const string BackboneFile = "dsm_tts_1e68beda@240.safetensors";
    private const string MimiFile = "tokenizer-e351c8d8-checkpoint125.safetensors";
    private const string SpmFile = "tokenizer_spm_8k_en_fr_audio.model";
    private const string VoiceSuffix = ".1e68beda@240.safetensors";
    private const string DefaultVoice = "expresso/ex03-ex01_happy_001_channel1_334s.wav";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string dsmPath = await AudioModelCache.GetAsync(Repo, BackboneFile, ct: ct).ConfigureAwait(false);
            string mimiPath = await AudioModelCache.GetAsync(Repo, MimiFile, ct: ct).ConfigureAwait(false);
            string spmPath = await AudioModelCache.GetAsync(Repo, SpmFile, ct: ct).ConfigureAwait(false);
            await EnsureVoiceAsync(DefaultVoice, ct).ConfigureAwait(false);

            Session session = Session.Load(dsmPath, mimiPath, spmPath);
            Logs.Info("[AudioLab][Kyutai-TTS] Loaded kyutai/tts-1.6b-en_fr (Helium backbone + Mimi DSM, 24 kHz).");
            return new TtsRunner(session.SampleRate, session.Synthesize, session);
        },
    };

    /// <summary>Ensures a tts-voices speaker embedding is present, mapping the UI voice name (a path relative to
    /// the repo root, e.g. <c>expresso/ex03-...</c>) to its versioned safetensors filename.</summary>
    private static async Task<string> EnsureVoiceAsync(string voiceName, CancellationToken ct)
    {
        string file = voiceName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? voiceName : voiceName + VoiceSuffix;
        return await AudioModelCache.GetAsync(VoicesRepo, file, ct: ct).ConfigureAwait(false);
    }

    /// <summary>A loaded Kyutai TTS model: the generator + Mimi codec + SentencePiece tokenizer, plus a cache of
    /// transposed voice embeddings. Owns the weight loaders (the F32 tensors reference their mmap) and disposes
    /// everything together.</summary>
    private sealed unsafe class Session : IDisposable
    {
        private readonly MoshiTtsGenerator _gen;
        private readonly Mimi _codec;
        private readonly KyutaiSttTokenizer _tok;
        private readonly IDisposable[] _loaders;
        private readonly Dictionary<string, Tensor> _voices = new(StringComparer.Ordinal);
        private readonly object _voiceLock = new();
        private int _disposed;

        private Session(MoshiTtsGenerator gen, Mimi codec, KyutaiSttTokenizer tok, IDisposable[] loaders)
        {
            _gen = gen;
            _codec = codec;
            _tok = tok;
            _loaders = loaders;
        }

        public int SampleRate => 24_000;

        public static Session Load(string dsmPath, string mimiPath, string spmPath)
        {
            SafeTensorsLoader dsm = new();
            dsm.Load(dsmPath);
            SafeTensorsLoader mimi = new();
            mimi.Load(mimiPath);

            MoshiTtsGenerator gen = new();
            gen.LoadWeights(dsm.GetAllTensors());
            gen.SetZeroToken(-1);   // moshi ScaledEmbedding zero token → zero contribution (matches the e2e test)
            Mimi codec = new(MimiConfig.Mimi24kHzDsm);
            codec.LoadWeights(mimi.GetAllTensors());
            KyutaiSttTokenizer tok = new(spmPath);
            return new Session(gen, codec, tok, [dsm, mimi]);
        }

        public float[] Synthesize(IBackend backend, TtsRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                return Array.Empty<float>();
            }
            Tensor voice = GetVoice(string.IsNullOrEmpty(req.Voice) ? DefaultVoice : req.Voice);

            // moshi script_to_entries with padding_between=1: one entry per word, prepend the Main speaker token
            // to the first word, and force per-word articulation padding of max(0, padding_between + len - 1) so
            // the model paces words as the 4.4 s reference did (without it, it pads maximally and drifts).
            const int paddingBetween = 1;
            List<KyutaiTextScheduler.Entry> entries = new();
            bool first = true;
            foreach (string word in req.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                List<int> ids = new(_tok.Encode(word));
                if (ids.Count == 0)
                {
                    continue;
                }
                if (first)
                {
                    ids.Insert(0, KyutaiTextScheduler.Main);
                    first = false;
                }
                int padding = Math.Max(0, paddingBetween + ids.Count - 1);
                entries.Add(new KyutaiTextScheduler.Entry(ids, word, padding));
            }
            if (entries.Count == 0)
            {
                return Array.Empty<float>();
            }

            // Frame budget: the 16-frame text lead + Σ(word tokens + padding) + tail, capped at the 500-position
            // budget. The scheduler ends the sequence once all words are consumed, so this is only an upper bound.
            int maxFrames = 16 + 8;
            foreach (KyutaiTextScheduler.Entry e in entries)
            {
                maxFrames += e.Tokens.Count + e.Padding + 2;
            }
            maxFrames = Math.Min(499, maxFrames);

            using Tensor cross = _gen.Conditioner.ComputeCross(backend, voice);
            // CFG-distilled model: guidance is a conditioning input (the LUT-embedded coefficient), and moshi's
            // TTS default is 2.0. A coefficient of 1.0 means no guidance → the text isn't enforced.
            using Tensor sumCond = _gen.Conditioner.ComputeSum(backend, MoshiConditioner.CfgBin(2.0f));

            KyutaiTextScheduler scheduler = new(secondStreamAhead: 2, maxPadding: 8, initialPadding: 2);
            int[,] codes = _gen.Generate(backend, scheduler, entries, cross, sumCond,
                maxFrames: maxFrames, audioTemp: 0.6f, seed: req.Seed);
            int n = codes.GetLength(1);
            if (n == 0)
            {
                Logs.Warning("[AudioLab][Kyutai-TTS] Generation produced no audio frames.");
                return Array.Empty<float>();
            }

            Tensor codeT = new(new TensorShape(1, MoshiTtsGenerator.NumCodebooks, n), DType.I32);
            int* cp = (int*)codeT.DataPointer;
            for (int k = 0; k < MoshiTtsGenerator.NumCodebooks; k++)
            {
                for (int f = 0; f < n; f++)
                {
                    cp[(long)k * n + f] = codes[k, f];
                }
            }
            using Tensor audioT = _codec.Decode(backend, codeT, batch: 1, tFrames: n);
            codeT.Dispose();

            int samples = (int)audioT.Shape[audioT.Shape.Rank - 1];
            float[] audio = new float[samples];
            fixed (float* dst = audio)
            {
                Buffer.MemoryCopy((void*)audioT.DataPointer, dst, (long)samples * 4, (long)samples * 4);
            }
            Logs.Info($"[AudioLab][Kyutai-TTS] {entries.Count} words → {n} frames → {samples / (double)SampleRate:0.0}s.");
            return audio;
        }

        /// <summary>Loads (and caches) a voice embedding transposed to the conditioner's [1,T,512] layout. The
        /// tts-voices files ship <c>speaker_wavs</c> as channels-first [1,512,T].</summary>
        private Tensor GetVoice(string voiceName)
        {
            lock (_voiceLock)
            {
                if (_voices.TryGetValue(voiceName, out Tensor cached))
                {
                    return cached;
                }
                string path = EnsureVoiceAsync(voiceName, CancellationToken.None).GetAwaiter().GetResult();
                using SafeTensorsLoader vw = new();
                vw.Load(path);
                Tensor sw = vw.GetAllTensors()["speaker_wavs"];   // [1, 512, T]
                int spkDim = (int)sw.Shape[1], tv = (int)sw.Shape[2];
                Tensor voice = new(new TensorShape(1, tv, spkDim), DType.F32);
                float* dp = (float*)voice.DataPointer;
                float* srp = (float*)sw.DataPointer;
                for (int c = 0; c < spkDim; c++)
                {
                    for (int ti = 0; ti < tv; ti++)
                    {
                        dp[(long)ti * spkDim + c] = srp[(long)c * tv + ti];
                    }
                }
                _voices[voiceName] = voice;
                return voice;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _gen.Dispose();
            _tok.Dispose();
            lock (_voiceLock)
            {
                foreach (Tensor v in _voices.Values)
                {
                    v.Dispose();
                }
                _voices.Clear();
            }
            foreach (IDisposable loader in _loaders)
            {
                loader?.Dispose();
            }
        }
    }
}
