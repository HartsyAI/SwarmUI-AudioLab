using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Resemble Enhance provider — AI-powered speech denoising and super-resolution to 44.1kHz.</summary>
public sealed class ResembleEnhanceProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Resemble Enhance provider.</summary>
    public static ResembleEnhanceProvider Instance { get; } = new();

    /// <summary>Builds and returns the Resemble Enhance provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("resemble_enhance_fx")
        .WithName("Resemble Enhance")
        .WithCategory(AudioCategory.AudioProcessing)
        .WithModelPrefix("ResembleEnhance")
        .WithModelClass("resemble_enhance_fx", "Resemble Enhance")
        .AddFeatureFlag("audiolab_audioproc")
        .AddFeatureFlag("resemble_enhance_fx_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "denoise", Name = "Resemble Denoise", Description = "Speech denoising — removes background noise from audio. Currently runs the same full enhance pipeline as 'Resemble Enhance' below (no denoise-only fast path exists yet); pick this variant for the intent/labeling, not for a speed difference.", SourceUrl = "https://github.com/resemble-ai/resemble-enhance", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", EngineConfig = new() { ["mode"] = "denoise" } },
        new() { Id = "enhance", Name = "Resemble Enhance", Description = "Full enhancement — denoise + super-resolution to 44.1kHz", SourceUrl = "https://github.com/resemble-ai/resemble-enhance", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", EngineConfig = new() { ["mode"] = "enhance" } }
    ];

    #endregion
}
