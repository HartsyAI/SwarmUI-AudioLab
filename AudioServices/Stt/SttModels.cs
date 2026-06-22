using System.IO;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using HartsyInference.Audio.Models.Kyutai;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>Per-model specifics for the generic <see cref="SttHandler"/>: how to turn an AudioLab model id
/// into a HuggingFace repo, and how to load that repo into an <see cref="ISttRunner"/>.</summary>
public sealed class SttModelDescriptor
{
    /// <summary>Maps an AudioLab model id (the <c>__model_id</c> variant hint) to a HuggingFace repo id.</summary>
    public required Func<string, string> ResolveRepo { get; init; }

    /// <summary>Loads the repo (downloading on first use) into a uniform runner.</summary>
    public required Func<string, CancellationToken, Task<ISttRunner>> LoadAsync { get; init; }

    /// <summary>Sample rate the input audio is decoded to before transcription (Whisper/Moonshine 16 kHz; Kyutai 24 kHz).</summary>
    public int InputSampleRate { get; init; } = 16_000;
}

/// <summary>STT model registry — each entry wires a pipeline to the generic handler.</summary>
public static class SttModels
{
    /// <summary>OpenAI Whisper + distil-whisper (same <see cref="WhisperPipeline"/>, different repos).</summary>
    public static readonly SttModelDescriptor Whisper = new()
    {
        ResolveRepo = ResolveWhisperRepo,
        LoadAsync = async (repo, ct) =>
        {
            WhisperPipeline p = await WhisperPipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner((backend, audio) => p.TranscribeAudio(backend, audio, 16_000), p);
        },
    };

    /// <summary>Kyutai delayed-streams STT (kyutai/stt-2.6b-en). Helium LM + Mimi codec → text token ids,
    /// decoded by the SentencePiece <see cref="KyutaiSttTokenizer"/>. Input audio is 24 kHz.</summary>
    public static readonly SttModelDescriptor Kyutai = new()
    {
        InputSampleRate = 24_000,
        // The engine loader expects the HF Transformers ("-trfs") key layout (model.embed_tokens.embed_tokens.weight).
        ResolveRepo = modelId =>
        {
            string id = (modelId ?? "").Trim();
            if (id.Contains('/'))
            {
                return id;
            }
            return id.ToLowerInvariant().Contains("1b") ? "kyutai/stt-1b-en_fr-trfs" : "kyutai/stt-2.6b-en-trfs";
        },
        LoadAsync = async (repo, ct) =>
        {
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> dict, System.IDisposable[] loaders)
                = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);
            string spm = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), "kyutai_stt.model");
            if (!File.Exists(spm))
            {
                throw new FileNotFoundException(
                    $"Kyutai STT needs its SentencePiece text model — place 'kyutai_stt.model' (from the {repo} repo) at '{spm}'.", spm);
            }
            // 1B (en+fr, vocab 8001, 16 layers) vs 2.6B (en, vocab 4001, 48 layers) — configs differ, pick by repo.
            KyutaiSttConfig cfg = repo.ToLowerInvariant().Contains("1b") ? KyutaiSttConfig.Stt1B : KyutaiSttConfig.Stt2_6B;
            // The Kyutai checkpoint bundles the Helium backbone + the Mimi codec; LoadWeights extracts each by prefix.
            KyutaiSttPipeline pipeline = new(cfg);
            pipeline.LoadWeights(dict, dict);
            KyutaiSttTokenizer tokenizer = new(spm);
            System.IDisposable[] keep = [pipeline, .. loaders];
            return new SttRunner((backend, audio) => tokenizer.Decode(pipeline.Transcribe(backend, audio)), keep);
        },
    };

    /// <summary>Useful Sensors Moonshine (tiny/base).</summary>
    public static readonly SttModelDescriptor Moonshine = new()
    {
        ResolveRepo = ResolveMoonshineRepo,
        LoadAsync = async (repo, ct) =>
        {
            MoonshinePipeline p = await MoonshinePipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner((backend, audio) => p.TranscribeAudio(backend, audio, 16_000), p);
        },
    };

    /// <summary>Whisper / distil-whisper model id → HF repo. Full repo ids (with '/') pass through; otherwise
    /// a size/variant token is matched; otherwise a sensible per-family default.</summary>
    private static string ResolveWhisperRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        if (lower.Contains("distil"))
        {
            if (lower.Contains("v3.5")) return "distil-whisper/distil-large-v3.5"; // before v3 (v3.5 contains "v3")
            if (lower.Contains("v3")) return "distil-whisper/distil-large-v3";
            if (lower.Contains("v2")) return "distil-whisper/distil-large-v2";
            if (lower.Contains("medium")) return "distil-whisper/distil-medium.en";
            if (lower.Contains("small")) return "distil-whisper/distil-small.en";
            return "distil-whisper/distil-large-v3.5";
        }
        if (lower.Contains("large") && lower.Contains("turbo")) return "openai/whisper-large-v3-turbo";
        if (lower.Contains("large")) return lower.Contains("v2") ? "openai/whisper-large-v2" : "openai/whisper-large-v3";
        if (lower.Contains("medium")) return "openai/whisper-medium";
        if (lower.Contains("small")) return "openai/whisper-small";
        if (lower.Contains("tiny")) return "openai/whisper-tiny";
        if (lower.Contains("base")) return "openai/whisper-base";
        return "openai/whisper-base";
    }

    /// <summary>Moonshine model id → HF repo (only tiny/base exist upstream).</summary>
    private static string ResolveMoonshineRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        return id.ToLowerInvariant().Contains("tiny")
            ? "UsefulSensors/moonshine-tiny"
            : "UsefulSensors/moonshine-base";
    }
}
