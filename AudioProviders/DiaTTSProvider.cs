using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Dia TTS provider — ultra-realistic dialogue generation with nonverbal sounds.</summary>
public sealed class DiaTTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Dia TTS provider.</summary>
    public static DiaTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Dia TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("dia_tts")
        .WithName("Dia TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Dia")
        .WithModelClass("dia_tts", "Dia TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("dia_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_cfg")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.6b", Name = "Dia 1.6B", Description = "Ultra-realistic dialogue, 2 speakers in one pass, nonverbal sounds", SourceUrl = "https://huggingface.co/nari-labs/Dia-1.6B-0626", License = "Apache 2.0", EstimatedSize = "~6.4GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "nari-labs/Dia-1.6B-0626" } }
    ];

    #endregion
}
