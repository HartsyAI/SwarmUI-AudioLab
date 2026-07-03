using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Frontends;
using HartsyInference.Audio.Models.Bark;
using HartsyInference.Audio.Models.Codecs.EnCodec;
using HartsyInference.Audio.Models.Codecs.Mimi;
using HartsyInference.Audio.Models.Csm;
using HartsyInference.Audio.Models.Dia;
using HartsyInference.Audio.Models.Orpheus;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.CheckpointConverters;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Per-model specifics for the generic <see cref="TtsHandler"/>: how to turn an AudioLab model id
/// into a repo (empty when the pipeline hardcodes it), and how to load it into an <see cref="ITtsRunner"/>.</summary>
public sealed class TtsModelDescriptor
{
    /// <summary>Maps an AudioLab model id (the <c>__model_id</c> variant hint) to a HuggingFace repo, or a
    /// constant/empty string when the pipeline resolves its own weights.</summary>
    public required Func<string, string> ResolveRepo { get; init; }

    /// <summary>Loads the model (downloading on first use) into a uniform runner.</summary>
    public required Func<string, CancellationToken, Task<ITtsRunner>> LoadAsync { get; init; }
}

/// <summary>TTS model registry — each entry wires a pipeline to the generic handler.</summary>
public static class TtsModels
{
    /// <summary>VibeVoice 1.5B — long-form multi-speaker synthesis. Built-in tokenizer (raw text in);
    /// requires a 24 kHz voice reference. The pipeline hardcodes its HF repo, so the model id is ignored.</summary>
    public static readonly TtsModelDescriptor VibeVoice = new()
    {
        ResolveRepo = _ => "microsoft/VibeVoice-1.5B",
        LoadAsync = async (_, ct) =>
        {
            VibeVoicePipeline p = await VibeVoicePipeline.LoadAsync(ct).ConfigureAwait(false);
            return new TtsRunner(24_000, (backend, req) =>
            {
                if (req.ReferenceWavPath is null)
                {
                    throw new InvalidOperationException(
                        "VibeVoice needs a voice reference — upload a short WAV clip in the voice reference field.");
                }
                return p.Synthesize(backend, new[] { req.Text }, new[] { req.ReferenceWavPath }, maxNewTokens: 1024);
            }, p);
        },
    };

    /// <summary>Public-domain CMU Pronouncing Dictionary — the English G2P dictionary, auto-downloaded on first use.</summary>
    private const string CmudictUrl = "https://raw.githubusercontent.com/cmusphinx/cmudict/master/cmudict.dict";

    /// <summary>Kokoro-82M — fast CPU-capable TTS at 24 kHz. Auto-downloads weights + voice packs; uses the
    /// engine's English <see cref="EnglishG2P"/> (text→IPA), backed by the CMU dictionary (<c>cmudict.dict</c>,
    /// auto-downloaded to the audio model root). Built-in voice (default <c>af_heart</c>).</summary>
    public static readonly TtsModelDescriptor Kokoro = new()
    {
        ResolveRepo = _ => "hexgrad/Kokoro-82M",
        LoadAsync = async (_, ct) =>
        {
            string cmudict = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), "cmudict.dict");
            if (!File.Exists(cmudict))
            {
                Logs.Info("[AudioLab][Kokoro] Downloading the public-domain CMU Pronouncing Dictionary (cmudict.dict)...");
                Directory.CreateDirectory(Path.GetDirectoryName(cmudict));
                await Utilities.DownloadFile(CmudictUrl, cmudict, (_, _, _) => { }).ConfigureAwait(false);
                Logs.Info("[AudioLab][Kokoro] CMU dictionary ready.");
            }
            EnglishG2P g2p = new(cmudict);
            KokoroPipeline p = await KokoroPipeline.LoadAsync(ct).ConfigureAwait(false);
            await EnsureKokoroVoiceAsync("af_heart", ct).ConfigureAwait(false);
            return new TtsRunner(24_000, (backend, req) =>
            {
                string voice = string.IsNullOrEmpty(req.Voice) ? "af_heart" : req.Voice;
                // Fetch the chosen voice pack on first use (af_heart is already ensured at load).
                EnsureKokoroVoiceAsync(voice, CancellationToken.None).GetAwaiter().GetResult();
                float speed = req.Speed.HasValue ? (float)req.Speed.Value : 1f;
                return p.Synthesize(backend, g2p.ToIpa(req.Text), voiceName: voice, speed: speed);
            }, p);
        },
    };

    /// <summary>Ensures a Kokoro voice pack exists as the raw-float32 <c>.bin</c> the engine reads. The HF repo
    /// ships each voice as a torch-saved <c>.pt</c> (a zip whose single contiguous f32 tensor storage at
    /// <c>*/data/0</c> is exactly that raw payload), so we fetch it and extract that entry.</summary>
    private static async Task EnsureKokoroVoiceAsync(string voiceName, CancellationToken ct)
    {
        string repoDir = AudioModelCache.GetRepoDirectory("hexgrad/Kokoro-82M");
        string binPath = Path.Combine(repoDir, "voices", $"{voiceName}.bin");
        if (File.Exists(binPath))
        {
            return;
        }
        Logs.Info($"[AudioLab][Kokoro] Fetching voice pack '{voiceName}'...");
        string ptPath = await AudioModelCache.GetAsync("hexgrad/Kokoro-82M", $"voices/{voiceName}.pt", ct: ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(binPath));
        using ZipArchive zip = ZipFile.OpenRead(ptPath);
        ZipArchiveEntry storage = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("/data/0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unexpected Kokoro voice format in '{ptPath}' — no tensor storage entry.");
        string tmp = binPath + ".tmp";
        using (Stream src = storage.Open())
        using (FileStream dst = File.Create(tmp))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(tmp, binPath, overwrite: true);
        Logs.Info($"[AudioLab][Kokoro] Voice pack '{voiceName}' ready.");
    }

    /// <summary>Bark (suno/bark) — 3-stage GPT cascade + EnCodec 24 kHz. No converter needed: the engine's
    /// Bark stages consume the HF-transformers key naming directly; the bundled codec maps via
    /// <see cref="MusicGenCheckpointConverter.ConvertEnCodec"/>. Text via the BERT WordPiece tokenizer
    /// (bert-base-multilingual-cased, auto-downloaded) + Bark's <c>TextEncodingOffset</c>.</summary>
    public static readonly TtsModelDescriptor Bark = new()
    {
        ResolveRepo = _ => "suno/bark",
        LoadAsync = async (_, ct) =>
        {
            string vocabPath = await AudioModelCache.GetAsync("google-bert/bert-base-multilingual-cased", "vocab.txt", ct: ct).ConfigureAwait(false);
            (IReadOnlyDictionary<string, Tensor> dict, IDisposable loader) = await LoadBarkWeightsAsync(ct).ConfigureAwait(false);

            BarkConfig cfg = BarkConfig.Full;
            BarkCausalStage semantic = new(cfg.Stage, cfg.SemanticInputVocab, cfg.SemanticOutputVocab);
            semantic.LoadWeights(dict, "semantic");
            BarkCausalStage coarse = new(cfg.Stage, cfg.CoarseVocab, cfg.CoarseVocab);
            coarse.LoadWeights(dict, "coarse_acoustics");
            BarkFineModel fine = new(cfg);
            fine.LoadWeights(dict, "fine_acoustics");

            // EnCodec 24 kHz from the bundled codec_model.* (HF EncodecModel naming → Meta via ConvertEnCodec).
            Dictionary<string, Tensor> codecRaw = new();
            foreach (KeyValuePair<string, Tensor> kv in dict)
            {
                if (kv.Key.StartsWith("codec_model.", StringComparison.Ordinal))
                {
                    codecRaw[kv.Key["codec_model.".Length..]] = kv.Value;
                }
            }
            EnCodec encodec = new(EnCodecConfig.EnCodec24kHz);
            encodec.LoadWeights(MusicGenCheckpointConverter.ConvertEnCodec(codecRaw));

            BarkPipeline pipeline = new(cfg, semantic, coarse, fine, encodec);
            BertWordPieceTokenizer bert = new(vocabPath, lowerCase: false);
            Logs.Info("[AudioLab][Bark] Loaded suno/bark (3-stage GPT + EnCodec 24 kHz).");

            // Loader kept alive: the F32 stage weights reference its tensors.
            return new TtsRunner(cfg.SampleRate,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.BarkText(bert, req.Text, cfg.TextEncodingOffset), req.Seed),
                pipeline, loader);
        },
    };

    /// <summary>Dia-1.6B (Nari Labs) — byte-level two-speaker dialogue TTS at 44.1 kHz. No converter: the
    /// encoder/decoder consume HF naming (<c>model.encoder.*</c>/<c>model.decoder.*</c>) directly; text is raw
    /// UTF-8 bytes via <see cref="AudioTextFrontend.DiaBytes"/> (speaker tags <c>[S1]</c>/<c>[S2]</c> inline).
    /// Auto-downloads the model; the separate Descript DAC 44 kHz codec is user-placed.</summary>
    public static readonly TtsModelDescriptor Dia = new()
    {
        ResolveRepo = _ => "nari-labs/Dia-1.6B",
        LoadAsync = async (_, ct) =>
        {
            string modelPath = await AudioModelCache.GetAsync("nari-labs/Dia-1.6B", "model.safetensors", ct: ct).ConfigureAwait(false);
            // The Descript DAC 44 kHz codec auto-downloads — the canonical descript .pth has the layout the engine
            // expects (the HF safetensors mirrors are MLX/HF-reshaped and would not load).
            string dacPath = await AudioModelCache.GetAsync("descript/descript-audio-codec", "weights.pth", ct: ct).ConfigureAwait(false);
            SafeTensorsLoader modelLoader = new();
            modelLoader.Load(modelPath);
            PytorchPickleLoader dacLoader = new();
            dacLoader.Load(dacPath);

            DiaPipeline pipeline = new(DiaConfig.Dia1_6B);
            pipeline.LoadWeights(modelLoader.GetAllTensors(), dacLoader.GetAllTensors());
            Logs.Info("[AudioLab][Dia] Loaded nari-labs/Dia-1.6B (byte-level dialogue TTS, 44.1 kHz).");

            return new TtsRunner(44_100,
                (backend, req) =>
                {
                    // Dia was trained on [S1]/[S2]-tagged dialogue; untagged text degenerates into repetition loops.
                    string text = req.Text.Contains("[S", StringComparison.Ordinal) ? req.Text : $"[S1] {req.Text}";
                    // The checkpoint itself degenerates to silence on short prompts (verified against upstream
                    // PyTorch, which does the same; nari-labs recommends text worth ~5-20s of speech).
                    if (text.Length < 120)
                    {
                        Logs.Warning($"[AudioLab][Dia] Prompt is very short ({text.Length} chars) — Dia-1.6B tends to "
                            + "produce silence below ~2 sentences (upstream behaves the same). Use longer dialogue-style text.");
                    }
                    return pipeline.Generate(backend, AudioTextFrontend.DiaBytes(text), seed: req.Seed);
                },
                pipeline, modelLoader, dacLoader);
        },
    };

    /// <summary>Orpheus TTS — Llama-3.2-3B LM + SNAC 24 kHz. Llama BPE of
    /// <c>"{voice}: {text}"</c> via <see cref="AudioTextFrontend.OrpheusText"/> (default voice <c>tara</c>).
    /// Weights from <c>unsloth/orpheus-3b-0.1-ft</c> — a non-gated mirror of the license-gated
    /// <c>canopylabs/orpheus-3b-0.1-ft</c> (same finetune, verified standard Llama-3.2 key layout), so no
    /// HF_TOKEN is needed. TODO(llama-asset): OrpheusText uses the engine's Llama-3 tokenizer — it throws a
    /// clear message until the llama3 vocab/merges asset is embedded; wired here as if present.</summary>
    public static readonly TtsModelDescriptor Orpheus = new()
    {
        ResolveRepo = _ => "unsloth/orpheus-3b-0.1-ft",
        LoadAsync = async (_, ct) =>
        {
            (IReadOnlyDictionary<string, Tensor> backbone, IDisposable[] bbLoaders) = await LoadCheckpointAsync("unsloth/orpheus-3b-0.1-ft", ct).ConfigureAwait(false);
            (IReadOnlyDictionary<string, Tensor> snac, IDisposable[] snacLoaders) = await LoadCheckpointAsync("hubertsiuzdak/snac_24khz", ct).ConfigureAwait(false);
            OrpheusPipeline pipeline = new(OrpheusConfig.Orpheus3B);
            pipeline.LoadWeights(backbone, snac);
            Logs.Info("[AudioLab][Orpheus] Loaded unsloth/orpheus-3b-0.1-ft (Llama-3.2-3B + SNAC 24 kHz).");
            IDisposable[] keep = [pipeline, .. bbLoaders, .. snacLoaders];
            return new TtsRunner(pipeline.SampleRate,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.OrpheusText(req.Text), seed: req.Seed), keep);
        },
    };

    /// <summary>Sesame CSM-1B — dual-transformer conversational TTS + Mimi 24 kHz. Plain
    /// Llama-3 BPE of the text via <see cref="AudioTextFrontend.CsmText"/>. Weights from <c>nielsr/csm-1b</c>
    /// — a non-gated mirror of the license-gated <c>sesame/csm-1b</c> (verified byte-identical original-format
    /// layout: <c>backbone.*</c>/<c>decoder.*</c>/<c>text_embeddings.weight</c>, 187 tensors), so no HF_TOKEN is
    /// needed. NOT the transformers <c>CSMModel</c> re-export, whose keys the engine loader wouldn't match.
    /// TODO(llama-asset): CsmText uses the engine's Llama-3 tokenizer — throws a clear message until the
    /// llama3 asset is embedded; wired here as if present.</summary>
    public static readonly TtsModelDescriptor Csm = new()
    {
        ResolveRepo = _ => "nielsr/csm-1b",
        LoadAsync = async (_, ct) =>
        {
            (IReadOnlyDictionary<string, Tensor> modelDict, IDisposable[] mLoaders) = await LoadCheckpointAsync("nielsr/csm-1b", ct).ConfigureAwait(false);
            (IReadOnlyDictionary<string, Tensor> mimiDict, IDisposable[] miLoaders) = await LoadCheckpointAsync("kyutai/mimi", ct).ConfigureAwait(false);
            CsmModel model = new(CsmConfig.V1B);
            model.LoadWeights(modelDict);
            Mimi mimi = new(MimiConfig.Mimi24kHz);
            mimi.LoadWeights(mimiDict);
            CsmPipeline pipeline = new(CsmConfig.V1B, model, mimi);
            Logs.Info("[AudioLab][CSM] Loaded nielsr/csm-1b (dual-transformer + Mimi 24 kHz).");
            IDisposable[] keep = [pipeline, .. mLoaders, .. miLoaders];
            return new TtsRunner(24_000,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.CsmText(req.Text), seed: req.Seed), keep);
        },
    };

    /// <summary>Loads a (possibly sharded) HF safetensors checkpoint, or a PyTorch pickle <c>.bin</c>, into one
    /// merged dict + the loaders to keep alive (the model tensors may reference them).</summary>
    internal static async Task<(IReadOnlyDictionary<string, Tensor> Dict, IDisposable[] Loaders)> LoadCheckpointAsync(string repo, CancellationToken ct)
    {
        try
        {
            string p = await AudioModelCache.GetAsync(repo, "model.safetensors", ct: ct).ConfigureAwait(false);
            SafeTensorsLoader loader = new();
            loader.Load(p);
            return (loader.GetAllTensors(), [loader]);
        }
        catch (FileNotFoundException) { }

        try
        {
            string indexPath = await AudioModelCache.GetAsync(repo, "model.safetensors.index.json", ct: ct).ConfigureAwait(false);
            JObject index = JObject.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));
            HashSet<string> shards = new(((JObject)index["weight_map"]).Properties().Select(pr => pr.Value!.ToString()), StringComparer.Ordinal);
            Dictionary<string, Tensor> merged = new();
            List<IDisposable> loaders = new();
            foreach (string shard in shards)
            {
                string sp = await AudioModelCache.GetAsync(repo, shard, ct: ct).ConfigureAwait(false);
                SafeTensorsLoader l = new();
                l.Load(sp);
                loaders.Add(l);
                foreach (KeyValuePair<string, Tensor> kv in l.GetAllTensors())
                {
                    merged[kv.Key] = kv.Value;
                }
            }
            return (merged, loaders.ToArray());
        }
        catch (FileNotFoundException) { }

        string binPath = await AudioModelCache.GetAsync(repo, "pytorch_model.bin", ct: ct).ConfigureAwait(false);
        PytorchPickleLoader pickle = new();
        pickle.Load(binPath);
        return (pickle.GetAllTensors(), [pickle]);
    }

    /// <summary>Loads the Bark transformers checkpoint — safetensors preferred, pickle <c>.bin</c> fallback.</summary>
    private static async Task<(IReadOnlyDictionary<string, Tensor> Dict, IDisposable Loader)> LoadBarkWeightsAsync(CancellationToken ct)
    {
        try
        {
            string path = await AudioModelCache.GetAsync("suno/bark", "model.safetensors", ct: ct).ConfigureAwait(false);
            SafeTensorsLoader loader = new();
            loader.Load(path);
            return (loader.GetAllTensors(), loader);
        }
        catch (FileNotFoundException)
        {
            string path = await AudioModelCache.GetAsync("suno/bark", "pytorch_model.bin", ct: ct).ConfigureAwait(false);
            PytorchPickleLoader loader = new();
            loader.Load(path);
            return (loader.GetAllTensors(), loader);
        }
    }
}
