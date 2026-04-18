using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ElevenLabs Voice Changer provider -- speech-to-speech voice conversion via ElevenLabs API.</summary>
public sealed class ElevenLabsVoiceChangerProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static ElevenLabsVoiceChangerProvider Instance { get; } = new();

    /// <summary>Builds and returns the ElevenLabs Voice Changer provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("elevenlabs_voice_changer")
        .WithName("ElevenLabs Voice Changer")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithModelPrefix("ElevenLabsVC")
        .WithModelClass("elevenlabs_vc", "ElevenLabs Voice Changer")
        .AddFeatureFlag("audiolab_clone")
        .AddFeatureFlag("elevenlabs_vc_params")
        .WithApiProvider("elevenlabs_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "ElevenLabs Voice Changer", Description = "Speech-to-speech voice conversion with emotion preservation", SourceUrl = "https://elevenlabs.io", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
