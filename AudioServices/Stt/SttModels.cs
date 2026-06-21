using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>Per-model specifics for the generic <see cref="SttHandler"/>: how to turn an AudioLab model id
/// into a HuggingFace repo, and how to load that repo into an <see cref="ISttRunner"/>.</summary>
public sealed class SttModelDescriptor
{
    /// <summary>Maps an AudioLab model id (the <c>__model_id</c> variant hint) to a HuggingFace repo id.</summary>
    public required Func<string, string> ResolveRepo { get; init; }

    /// <summary>Loads the repo (downloading on first use) into a uniform runner.</summary>
    public required Func<string, CancellationToken, Task<ISttRunner>> LoadAsync { get; init; }
}

/// <summary>The STT model registry. Each entry is a few lines wiring an engine pipeline to the generic
/// handler — no per-model handler/runner classes. Whisper and distil-whisper share one descriptor (same
/// pipeline, different repos resolved per model id).</summary>
public static class SttModels
{
    /// <summary>OpenAI Whisper + distil-whisper (same <see cref="WhisperPipeline"/>, different repos).</summary>
    public static readonly SttModelDescriptor Whisper = new()
    {
        ResolveRepo = ResolveWhisperRepo,
        LoadAsync = async (repo, ct) =>
        {
            WhisperPipeline p = await WhisperPipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner(p, (backend, audio) => p.TranscribeAudio(backend, audio, 16_000));
        },
    };

    /// <summary>Useful Sensors Moonshine (tiny/base).</summary>
    public static readonly SttModelDescriptor Moonshine = new()
    {
        ResolveRepo = ResolveMoonshineRepo,
        LoadAsync = async (repo, ct) =>
        {
            MoonshinePipeline p = await MoonshinePipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner(p, (backend, audio) => p.TranscribeAudio(backend, audio, 16_000));
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
            if (lower.Contains("v3")) return "distil-whisper/distil-large-v3";
            if (lower.Contains("v2")) return "distil-whisper/distil-large-v2";
            if (lower.Contains("medium")) return "distil-whisper/distil-medium.en";
            if (lower.Contains("small")) return "distil-whisper/distil-small.en";
            return "distil-whisper/distil-large-v3";
        }
        if (lower.Contains("large") && lower.Contains("turbo")) return "openai/whisper-large-v3-turbo";
        if (lower.Contains("large")) return "openai/whisper-large-v3";
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
