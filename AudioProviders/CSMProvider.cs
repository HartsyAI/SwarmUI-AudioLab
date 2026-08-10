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
        // Real incremental generation via the shared Mimi-codec streaming state (same mechanism as Kyutai TTS,
        // verified against real weights: streamed output matches monolithic synthesis to ~3e-4 maxAbs) — see
        // AudioEngineBridge.SupportsNativeStreaming, the single source of truth this flag also drives.
        .AddFeatureFlag("tts_streaming")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        // EstimatedSize corrected from ~2GB: the actual unsloth/csm-1b model.safetensors alone is ~3.96GB.
        new() { Id = "1b", Name = "CSM 1B", Description = "Conversational speech, multi-turn dialogue", SourceUrl = "https://huggingface.co/sesame/csm-1b", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~4.5GB", EngineConfig = new() { ["model_name"] = "sesame/csm-1b" } }
    ];

    #endregion
}
