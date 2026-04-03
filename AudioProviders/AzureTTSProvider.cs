using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Azure Neural TTS provider -- 500+ voices with SSML and emotion/style control.</summary>
public sealed class AzureTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static AzureTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("azure_tts")
        .WithName("Azure Neural TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("AzureTTS")
        .WithModelClass("azure_tts", "Azure Neural TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("azure_tts_params")
        .WithApiProvider("azure_speech_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Azure Neural TTS", Description = "500+ neural voices with SSML, emotion, and style control", SourceUrl = "https://azure.microsoft.com/en-us/products/ai-services/text-to-speech", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
