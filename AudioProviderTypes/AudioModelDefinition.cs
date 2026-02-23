namespace Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;

/// <summary>Metadata for a specific model within an audio provider.</summary>
public sealed class AudioModelDefinition
{
    /// <summary>Unique model identifier within the provider (e.g. "default", "large-v3").</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the UI (e.g. "Chatterbox Default Voice").</summary>
    public required string Name { get; init; }

    /// <summary>Description of the model's capabilities.</summary>
    public string Description { get; init; } = "";

    /// <summary>Engine-specific configuration passed to the Python engine at runtime.</summary>
    public Dictionary<string, object> EngineConfig { get; init; } = [];
}
