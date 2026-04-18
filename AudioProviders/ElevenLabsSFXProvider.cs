using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ElevenLabs Sound Effects provider -- text-to-SFX via ElevenLabs API.</summary>
public sealed class ElevenLabsSFXProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static ElevenLabsSFXProvider Instance { get; } = new();

    /// <summary>Builds and returns the ElevenLabs SFX provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("elevenlabs_sfx")
        .WithName("ElevenLabs Sound Effects")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("ElevenLabsSFX")
        .WithModelClass("elevenlabs_sfx", "ElevenLabs SFX")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("elevenlabs_sfx_params")
        .WithApiProvider("elevenlabs_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "ElevenLabs Sound Effects", Description = "Text-to-sound-effect generation", SourceUrl = "https://elevenlabs.io", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
