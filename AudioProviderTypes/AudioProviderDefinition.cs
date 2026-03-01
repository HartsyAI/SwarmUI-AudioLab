using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Type-safe definition for an audio provider. Built via <see cref="AudioProviderDefinitionBuilder"/>.</summary>
public sealed class AudioProviderDefinition
{
    /// <summary>Unique provider identifier (e.g. "chatterbox_tts", "whisper_stt").</summary>
    public required string Id { get; init; }

    /// <summary>Display name of the provider (e.g. "Chatterbox TTS").</summary>
    public required string Name { get; init; }

    /// <summary>Audio processing category this provider belongs to.</summary>
    public required AudioCategory Category { get; init; }

    /// <summary>Python module name in the engines/ directory (e.g. "tts_chatterbox").</summary>
    public required string PythonModule { get; init; }

    /// <summary>Python engine class name within the module (e.g. "ChatterboxEngine").</summary>
    public required string PythonEngineClass { get; init; }

    /// <summary>Prefix used in model names for routing (e.g. "Chatterbox/").</summary>
    public required string ModelPrefix { get; init; }

    /// <summary>Model class ID for SwarmUI categorization.</summary>
    public required string ModelClassId { get; init; }

    /// <summary>Model class display name.</summary>
    public required string ModelClassName { get; init; }

    /// <summary>Feature flags this provider supports (used for parameter visibility).</summary>
    public required IReadOnlyList<string> FeatureFlags { get; init; }

    /// <summary>Python package dependencies required by this provider.</summary>
    public required IReadOnlyList<PackageDefinition> Dependencies { get; init; }

    /// <summary>Available models for this provider.</summary>
    public required IReadOnlyList<AudioModelDefinition> Models { get; init; }

    /// <summary>Engine group for venv/Docker isolation (e.g. "core", "transformers", "audiocraft", "linux_docker").</summary>
    public string EngineGroup { get; init; } = "default";

    /// <summary>Whether this provider requires Docker to run (Linux-only engines).</summary>
    public bool RequiresDocker { get; init; } = false;

    /// <summary>Creates the full model name with the Audio Models prefix for SwarmUI routing.</summary>
    public string GetFullModelName(string modelId) => $"Audio Models/{ModelPrefix}/{modelId}";
}
