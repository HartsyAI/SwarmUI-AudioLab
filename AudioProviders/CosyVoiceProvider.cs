using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>CosyVoice provider -- Alibaba's streaming multilingual TTS with ultra-low latency.</summary>
public sealed class CosyVoiceProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the CosyVoice provider.</summary>
    public static CosyVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the CosyVoice provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("cosyvoice_tts")
        .WithName("CosyVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("CosyVoice")
        .WithModelClass("cosyvoice_tts", "CosyVoice TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("cosyvoice_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .WithRequiresDocker()
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "2-0.5b", Name = "CosyVoice2 0.5B", Description = "Streaming TTS with ultra-low latency, multilingual", SourceUrl = "https://huggingface.co/FunAudioLLM/CosyVoice2-0.5B", License = "Apache 2.0", EstimatedSize = "~2GB", EstimatedVram = "~8GB", EngineConfig = new() { ["model_name"] = "FunAudioLLM/CosyVoice2-0.5B" } }
    ];

    #endregion
}
