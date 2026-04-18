using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Deepgram Aura TTS provider -- fast, affordable text-to-speech.</summary>
public sealed class DeepgramTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static DeepgramTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("deepgram_tts")
        .WithName("Deepgram Aura TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("DeepgramTTS")
        .WithModelClass("deepgram_tts", "Deepgram Aura TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("deepgram_tts_params")
        .WithApiProvider("deepgram_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Deepgram Aura", Description = "Fast, affordable TTS with 12 voices", SourceUrl = "https://deepgram.com", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
