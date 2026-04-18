using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Cartesia Sonic TTS provider -- ultra-low latency text-to-speech.</summary>
public sealed class CartesiaTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static CartesiaTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("cartesia_tts")
        .WithName("Cartesia Sonic TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("CartesiaSonic")
        .WithModelClass("cartesia_tts", "Cartesia Sonic TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("cartesia_tts_params")
        .WithApiProvider("cartesia_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Cartesia Sonic 2", Description = "Ultra-low latency (<100ms) multilingual TTS", SourceUrl = "https://cartesia.ai", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
