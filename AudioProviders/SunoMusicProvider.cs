using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Suno Music provider -- AI-generated full songs with vocals and lyrics.</summary>
public sealed class SunoMusicProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static SunoMusicProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("suno_music")
        .WithName("Suno Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("Suno")
        .WithModelClass("suno_music", "Suno Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("suno_music_params")
        .WithApiProvider("suno_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Suno v4", Description = "Full AI-generated songs with vocals, lyrics, and instrumentals", SourceUrl = "https://suno.com", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
