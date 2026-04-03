using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Google Cloud TTS provider -- WaveNet, Neural2, Studio, and Chirp3 voices.</summary>
public sealed class GoogleCloudTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static GoogleCloudTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("google_cloud_tts")
        .WithName("Google Cloud TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("GoogleTTS")
        .WithModelClass("google_tts", "Google Cloud TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("google_tts_params")
        .WithApiProvider("google_cloud_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Google Cloud TTS", Description = "700+ voices across WaveNet, Neural2, Studio, and Standard engines", SourceUrl = "https://cloud.google.com/text-to-speech", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
