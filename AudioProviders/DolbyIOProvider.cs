using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Dolby.io Media provider -- professional audio enhancement, noise reduction, and mastering.</summary>
public sealed class DolbyIOProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static DolbyIOProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("dolby_audioproc")
        .WithName("Dolby.io Audio Processing")
        .WithCategory(AudioCategory.AudioProcessing)
        .WithModelPrefix("DolbyIO")
        .WithModelClass("dolby_audioproc", "Dolby.io Audio Processing")
        .AddFeatureFlag("audiolab_audioproc")
        .AddFeatureFlag("dolby_audioproc_params")
        .WithApiProvider("dolby_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Dolby.io Enhance", Description = "Professional audio enhancement, noise reduction, and loudness mastering", SourceUrl = "https://dolby.io", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
