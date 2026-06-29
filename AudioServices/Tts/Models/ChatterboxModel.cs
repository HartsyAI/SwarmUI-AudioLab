using System;
using System.Collections.Generic;
using System.IO;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Chatterbox;
using HartsyInference.Audio.Models.CosyVoice;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Chatterbox (ResembleAI/chatterbox) — T3 LM → CosyVoice2 S3Gen flow → HiFTNet, 24 kHz. Provider id
/// <c>chatterbox_tts</c>. Merges <c>t3_cfg.safetensors</c> (under <c>t3.</c>) + <c>s3gen.safetensors</c> (under
/// <c>s3gen.</c>), loads the shipped <c>ChatterboxEnTokenizer</c>, and drives the default voice from the
/// precomputed conditionals in <c>conds.pt</c> (a torch pickle, loaded via <see cref="PytorchPickleLoader"/> as a
/// nested <c>{t3:{…}, gen:{…}}</c> dict → dotted keys): the T3 256-d voice-encoder embedding
/// (<c>t3.speaker_emb</c>), the 192-d CAM++ flow embedding (<c>gen.embedding</c>), and the T3 cond-prompt speech
/// tokens (<c>t3.cond_prompt_speech_tokens</c>). Reference-voice cloning is gated until a PCM→40-bin-mel front-end
/// for the voice encoder lands; the default voice synthesizes end-to-end.</summary>
public static class ChatterboxModel
{
    private const string Repo = "ResembleAI/chatterbox";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string t3Path = await AudioModelCache.GetAsync(Repo, "t3_cfg.safetensors", ct: ct).ConfigureAwait(false);
            string s3Path = await AudioModelCache.GetAsync(Repo, "s3gen.safetensors", ct: ct).ConfigureAwait(false);
            string tokPath = await AudioModelCache.GetAsync(Repo, "tokenizer.json", ct: ct).ConfigureAwait(false);
            string condsPath = await AudioModelCache.GetAsync(Repo, "conds.pt", ct: ct).ConfigureAwait(false);

            // Merge the two checkpoints under the prefixes ChatterboxPipeline.LoadWeights validates.
            Dictionary<string, Tensor> merged = new();
            SafeTensorsLoader t3Loader = new(); t3Loader.Load(t3Path);
            foreach (KeyValuePair<string, Tensor> kv in t3Loader.GetAllTensors()) { merged["t3." + kv.Key] = kv.Value; }
            SafeTensorsLoader s3Loader = new(); s3Loader.Load(s3Path);
            foreach (KeyValuePair<string, Tensor> kv in s3Loader.GetAllTensors()) { merged["s3gen." + kv.Key] = kv.Value; }

            ChatterboxConfig cfg = ChatterboxConfig.Default;
            CosyVoiceConfig cosy = CosyVoiceConfig.V2_0_5B;   // Chatterbox S3Gen == CosyVoice2-0.5B
            ChatterboxT3 t3 = new(cfg);
            CosyVoiceFlow flow = new(cosy);
            HiFTNetVocoder vocoder = new(cosy.Hift);
            CamPlusSpeakerEncoder spkEnc = new(cosy.Flow.SpeakerEmbedDim);
            ChatterboxPipeline pipeline = new(cfg, t3, flow, vocoder, spkEnc);
            pipeline.LoadWeights(merged); // key-validation gate: throws on any missing/renamed key.

            // Precomputed default-voice conditionals from conds.pt. PytorchPickleLoader materializes into owned
            // memory, so the three tensors stay valid as long as the loader is kept alive (it is, in `keep`).
            PytorchPickleLoader condsLoader = new();
            condsLoader.Load(condsPath, recursiveFlatten: true);
            IReadOnlyDictionary<string, Tensor> conds = condsLoader.GetAllTensors();
            Tensor refSpk = Flatten(conds["t3.speaker_emb"], cfg.SpeakerEmbedDim);            // [256]
            Tensor flowSpk = conds["gen.embedding"];                                          // [1,192]
            int[] t3Prompt = ToInts(conds["t3.cond_prompt_speech_tokens"]);                   // [150] int64→int

            using Stream tokStream = File.OpenRead(tokPath);
            ChatterboxEnTokenizer tok = new(tokStream);
            Logs.Info("[AudioLab][Chatterbox] Loaded ResembleAI/chatterbox (T3 + S3Gen + HiFTNet, 24 kHz, default voice).");

            IDisposable[] keep = [pipeline, t3, flow, vocoder, spkEnc, t3Loader, s3Loader, condsLoader, refSpk];
            return new TtsRunner(cfg.SampleRate, (backend, req) =>
            {
                if (req.ReferenceMono24k is not null)
                {
                    throw new NotSupportedException(
                        "[AudioLab][Chatterbox] Reference-voice cloning isn't wired yet — it needs a PCM→40-bin-mel "
                        + "front-end for the voice encoder. Clear the voice reference to use the built-in default voice.");
                }
                int[] textTokens = tok.EncodeWithStartStop(req.Text);
                float exaggeration = req.Exaggeration.HasValue ? (float)req.Exaggeration.Value : cfg.Exaggeration;
                return pipeline.Synthesize(backend, textTokens, refSpk, exaggeration, req.Seed,
                    flowSpeakerEmbed: flowSpk, t3PromptSpeechTokens: t3Prompt);
            }, keep);
        },
    };

    /// <summary>Copies the first <paramref name="n"/> floats of a tensor into a fresh owned <c>[n]</c> tensor
    /// (the T3 voice-encoder embedding is shipped as <c>[1, 256]</c>; the pipeline takes the flat <c>[256]</c>).</summary>
    private static Tensor Flatten(Tensor src, int n)
    {
        Tensor t = new(new TensorShape(n), DType.F32);
        src.AsSpan<float>().Slice(0, n).CopyTo(t.AsSpan<float>());
        return t;
    }

    /// <summary>Reads an int64 token tensor (conds <c>cond_prompt_speech_tokens</c>) as the <c>int[]</c> the T3 takes.</summary>
    private static int[] ToInts(Tensor src)
    {
        ReadOnlySpan<long> s = src.AsSpan<long>();
        int[] r = new int[s.Length];
        for (int i = 0; i < s.Length; i++) { r[i] = (int)s[i]; }
        return r;
    }
}
