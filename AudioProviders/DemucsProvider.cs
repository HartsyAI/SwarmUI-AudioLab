using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Demucs provider — Meta's audio source separation (vocals, drums, bass, other).</summary>
public sealed class DemucsProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Demucs provider.</summary>
    public static DemucsProvider Instance { get; } = new();

    /// <summary>Builds and returns the Demucs separation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("demucs_fx")
        .WithName("Demucs Separation")
        .WithCategory(AudioCategory.AudioProcessing)
        .WithModelPrefix("Demucs")
        .WithModelClass("demucs_fx", "Demucs Separation")
        .AddFeatureFlag("audiolab_audioproc")
        .AddFeatureFlag("demucs_fx_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "htdemucs", Name = "HTDemucs", Description = "Hybrid Transformer Demucs — best quality 4-stem separation", SourceUrl = "https://github.com/facebookresearch/demucs", License = "MIT", EstimatedSize = "~80MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "htdemucs" } },
        new() { Id = "htdemucs_ft", Name = "HTDemucs Fine-tuned", Description = "Fine-tuned variant, highest quality separation", SourceUrl = "https://github.com/facebookresearch/demucs", License = "MIT", EstimatedSize = "~80MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "htdemucs_ft" } },
        new() { Id = "htdemucs_6s", Name = "HTDemucs 6-Stem", Description = "6-stem separation (vocals, drums, bass, guitar, piano, other)", SourceUrl = "https://github.com/facebookresearch/demucs", License = "MIT", EstimatedSize = "~80MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "htdemucs_6s" } }
    ];

    #endregion
}
