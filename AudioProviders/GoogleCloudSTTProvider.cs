using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Google Cloud STT provider -- Chirp 3 and other speech recognition models.</summary>
public sealed class GoogleCloudSTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static GoogleCloudSTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("google_cloud_stt")
        .WithName("Google Cloud STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("GoogleSTT")
        .WithModelClass("google_stt", "Google Cloud STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("google_stt_params")
        .WithApiProvider("google_cloud_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Google Cloud STT", Description = "Speech-to-Text v1: 125+ languages with automatic punctuation (Chirp 2/3 require the v2 API)", SourceUrl = "https://cloud.google.com/speech-to-text", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
