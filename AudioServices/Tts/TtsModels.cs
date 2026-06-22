using System.IO;
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
        ResolveRepo = _ => "vibevoice/VibeVoice-1.5B",
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

    /// <summary>Kokoro-82M — fast CPU-capable TTS at 24 kHz. Auto-downloads weights + voice packs; uses the
    /// engine's English <see cref="EnglishG2P"/> (text→IPA). Needs a CMU dictionary (<c>cmudict.dict</c>) in
    /// the audio model root. Built-in voice (default <c>af_heart</c>).</summary>
    public static readonly TtsModelDescriptor Kokoro = new()
    {
        ResolveRepo = _ => "hexgrad/Kokoro-82M",
        LoadAsync = async (_, ct) =>
        {
            string cmudict = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), "cmudict.dict");
            if (!File.Exists(cmudict))
            {
                throw new FileNotFoundException(
                    $"Kokoro needs an English G2P dictionary — place the public-domain CMU Pronouncing Dictionary "
                    + $"('cmudict.dict') at '{cmudict}'.", cmudict);
            }
            EnglishG2P g2p = new(cmudict);
            KokoroPipeline p = await KokoroPipeline.LoadAsync(ct).ConfigureAwait(false);
            return new TtsRunner(24_000, (backend, req) => p.Synthesize(backend, g2p.ToIpa(req.Text), voiceName: "af_heart"), p);
        },
    };

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

            // Loader kept alive: the F32 stage weights reference its tensors. Seed not plumbed (default 0).
            return new TtsRunner(cfg.SampleRate,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.BarkText(bert, req.Text, cfg.TextEncodingOffset)),
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
            string dacPath = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), "dac_44khz.safetensors");
            if (!File.Exists(dacPath))
            {
                throw new FileNotFoundException(
                    $"Dia needs the Descript DAC 44 kHz codec — place 'dac_44khz.safetensors' (converted from the descript-audio-codec 44 kHz weights) at '{dacPath}'.", dacPath);
            }
            SafeTensorsLoader modelLoader = new();
            modelLoader.Load(modelPath);
            SafeTensorsLoader dacLoader = new();
            dacLoader.Load(dacPath);

            DiaPipeline pipeline = new(DiaConfig.Dia1_6B);
            pipeline.LoadWeights(modelLoader.GetAllTensors(), dacLoader.GetAllTensors());
            Logs.Info("[AudioLab][Dia] Loaded nari-labs/Dia-1.6B (byte-level dialogue TTS, 44.1 kHz).");

            return new TtsRunner(44_100,
                (backend, req) => pipeline.Generate(backend, AudioTextFrontend.DiaBytes(req.Text)),
                pipeline, modelLoader, dacLoader);
        },
    };

    /// <summary>Orpheus TTS (canopylabs/orpheus-3b-0.1-ft) — Llama-3.2-3B LM + SNAC 24 kHz. Llama BPE of
    /// <c>"{voice}: {text}"</c> via <see cref="AudioTextFrontend.OrpheusText"/> (default voice <c>tara</c>).
    /// TODO(llama-asset): OrpheusText uses the engine's Llama-3 tokenizer — it throws a clear message until the
    /// llama3 vocab/merges asset is embedded; wired here as if present.</summary>
    public static readonly TtsModelDescriptor Orpheus = new()
    {
        ResolveRepo = _ => "canopylabs/orpheus-3b-0.1-ft",
        LoadAsync = async (_, ct) =>
        {
            (IReadOnlyDictionary<string, Tensor> backbone, IDisposable[] bbLoaders) = await LoadCheckpointAsync("canopylabs/orpheus-3b-0.1-ft", ct).ConfigureAwait(false);
            (IReadOnlyDictionary<string, Tensor> snac, IDisposable[] snacLoaders) = await LoadCheckpointAsync("hubertsiuzdak/snac_24khz", ct).ConfigureAwait(false);
            OrpheusPipeline pipeline = new(OrpheusConfig.Orpheus3B);
            pipeline.LoadWeights(backbone, snac);
            Logs.Info("[AudioLab][Orpheus] Loaded canopylabs/orpheus-3b-0.1-ft (Llama-3.2-3B + SNAC 24 kHz).");
            IDisposable[] keep = [pipeline, .. bbLoaders, .. snacLoaders];
            return new TtsRunner(pipeline.SampleRate,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.OrpheusText(req.Text)), keep);
        },
    };

    /// <summary>Sesame CSM-1B (sesame/csm-1b) — dual-transformer conversational TTS + Mimi 24 kHz. Plain
    /// Llama-3 BPE of the text via <see cref="AudioTextFrontend.CsmText"/>.
    /// TODO(llama-asset): CsmText uses the engine's Llama-3 tokenizer — throws a clear message until the
    /// llama3 asset is embedded; wired here as if present.</summary>
    public static readonly TtsModelDescriptor Csm = new()
    {
        ResolveRepo = _ => "sesame/csm-1b",
        LoadAsync = async (_, ct) =>
        {
            (IReadOnlyDictionary<string, Tensor> modelDict, IDisposable[] mLoaders) = await LoadCheckpointAsync("sesame/csm-1b", ct).ConfigureAwait(false);
            (IReadOnlyDictionary<string, Tensor> mimiDict, IDisposable[] miLoaders) = await LoadCheckpointAsync("kyutai/mimi", ct).ConfigureAwait(false);
            CsmModel model = new(CsmConfig.V1B);
            model.LoadWeights(modelDict);
            Mimi mimi = new(MimiConfig.Mimi24kHz);
            mimi.LoadWeights(mimiDict);
            CsmPipeline pipeline = new(CsmConfig.V1B, model, mimi);
            Logs.Info("[AudioLab][CSM] Loaded sesame/csm-1b (dual-transformer + Mimi 24 kHz).");
            IDisposable[] keep = [pipeline, .. mLoaders, .. miLoaders];
            return new TtsRunner(24_000,
                (backend, req) => pipeline.Synthesize(backend, AudioTextFrontend.CsmText(req.Text)), keep);
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
