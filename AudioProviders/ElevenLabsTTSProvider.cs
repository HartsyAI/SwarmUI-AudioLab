using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ElevenLabs TTS provider -- high-quality text-to-speech via ElevenLabs API.</summary>
public sealed class ElevenLabsTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static ElevenLabsTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the ElevenLabs TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("elevenlabs_tts")
        .WithName("ElevenLabs TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("ElevenLabs")
        .WithModelClass("elevenlabs_tts", "ElevenLabs TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("elevenlabs_tts_params")
        .WithApiProvider("elevenlabs_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "ElevenLabs Multilingual v2", Description = "High-quality multilingual TTS with 30+ languages and voice cloning", SourceUrl = "https://elevenlabs.io", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
