using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Udio Music provider -- high-quality AI music generation.</summary>
public sealed class UdioMusicProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static UdioMusicProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("udio_music")
        .WithName("Udio Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("Udio")
        .WithModelClass("udio_music", "Udio Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("udio_music_params")
        .WithApiProvider("udio_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Udio v1.5", Description = "High-quality AI music generation with style control", SourceUrl = "https://www.udio.com", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
