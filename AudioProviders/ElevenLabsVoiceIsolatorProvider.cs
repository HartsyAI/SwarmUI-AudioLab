using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ElevenLabs Voice Isolator provider -- vocal isolation via ElevenLabs API.</summary>
public sealed class ElevenLabsVoiceIsolatorProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static ElevenLabsVoiceIsolatorProvider Instance { get; } = new();

    /// <summary>Builds and returns the ElevenLabs Voice Isolator provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("elevenlabs_voice_isolator")
        .WithName("ElevenLabs Voice Isolator")
        .WithCategory(AudioCategory.AudioProcessing)
        .WithModelPrefix("ElevenLabsIsolator")
        .WithModelClass("elevenlabs_isolator", "ElevenLabs Voice Isolator")
        .AddFeatureFlag("audiolab_audioproc")
        .AddFeatureFlag("elevenlabs_isolator_params")
        .WithApiProvider("elevenlabs_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "ElevenLabs Voice Isolator", Description = "Clean vocal isolation from mixed audio", SourceUrl = "https://elevenlabs.io", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
