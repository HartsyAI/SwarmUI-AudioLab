namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Metadata for a specific model within an audio provider.</summary>
public sealed class AudioModelDefinition
{
    /// <summary>Unique model identifier within the provider (e.g. "default", "large-v3").</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the UI (e.g. "Chatterbox Default Voice").</summary>
    public required string Name { get; init; }

    /// <summary>Description of the model's capabilities.</summary>
    public string Description { get; init; } = "";

    /// <summary>URL where the model can be downloaded from (e.g. HuggingFace repo URL).</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>License type for the model (e.g. "Apache 2.0", "MIT", "CC-BY-NC-4.0").</summary>
    public string License { get; init; } = "";

    /// <summary>Estimated download size (e.g. "~200MB", "~3GB").</summary>
    public string EstimatedSize { get; init; } = "";

    /// <summary>Estimated VRAM requirement (e.g. "~1GB (or CPU)", "~16GB").</summary>
    public string EstimatedVram { get; init; } = "";

    /// <summary>Engine-specific configuration passed to the Python engine at runtime.</summary>
    public Dictionary<string, object> EngineConfig { get; init; } = [];

    /// <summary>Optional model class ID override. When set, this model uses a different model class
    /// than the provider default, enabling per-model feature flag visibility.</summary>
    public string ModelClassId { get; init; }

    /// <summary>Optional model class display name override. Used with <see cref="ModelClassId"/>.</summary>
    public string ModelClassName { get; init; }

    /// <summary>When true, the model is downloaded by its Python library at runtime rather than
    /// pre-downloaded from HuggingFace during installation (e.g. Whisper, Moonshine, Demucs).</summary>
    public bool SelfManaged { get; init; }

    /// <summary>"{providerId}/{modelId}" of the artifact that backs this row, when it is not this row's own.
    /// Set it where several catalog rows share one file — a streaming variant riding on the base weights, or
    /// a quantization produced on-device from them — so the row is admitted when that artifact is present
    /// instead of waiting for a file it will never have.</summary>
    public string BackedByArtifact { get; init; }
}
