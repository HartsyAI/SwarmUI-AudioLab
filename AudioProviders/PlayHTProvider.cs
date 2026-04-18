using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Play.ht TTS provider -- high-quality TTS with voice cloning and emotion control.</summary>
public sealed class PlayHTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static PlayHTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("playht_tts")
        .WithName("Play.ht TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("PlayHT")
        .WithModelClass("playht_tts", "Play.ht TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("playht_tts_params")
        .WithApiProvider("playht_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Play.ht", Description = "High-quality TTS with voice cloning and emotion control (PlayHT 2.0/3.0)", SourceUrl = "https://play.ht", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
