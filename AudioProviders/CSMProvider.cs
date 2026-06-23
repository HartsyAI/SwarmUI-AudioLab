using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Sesame CSM provider -- conversational speech generation model (Llama backbone).</summary>
public sealed class CSMProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the CSM provider.</summary>
    public static CSMProvider Instance { get; } = new();

    /// <summary>Builds and returns the CSM provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("csm_tts")
        .WithName("CSM Conversational")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("CSM")
        .WithModelClass("csm_tts", "CSM Conversational")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("csm_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1b", Name = "CSM 1B", Description = "Conversational speech, multi-turn dialogue", SourceUrl = "https://huggingface.co/sesame/csm-1b", License = "CC-BY-NC-4.0", EstimatedSize = "~2GB", EstimatedVram = "~4.5GB", EngineConfig = new() { ["model_name"] = "sesame/csm-1b" } }
    ];

    #endregion
}
