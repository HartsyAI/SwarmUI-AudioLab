using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>NeuTTS Air provider -- on-device TTS with instant voice cloning by Neuphonic.</summary>
public sealed class NeuTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the NeuTTS provider.</summary>
    public static NeuTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the NeuTTS provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("neutts_tts")
        .WithName("NeuTTS Air")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("NeuTTS")
        .WithModelClass("neutts_tts", "NeuTTS Air")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("neutts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "air", Name = "NeuTTS Air", Description = "On-device TTS with instant voice cloning, 0.5B params", SourceUrl = "https://huggingface.co/neuphonic/neutts-air", License = "Apache 2.0", EstimatedSize = "~1GB", EstimatedVram = "~2GB (or CPU)", EngineConfig = new() { ["model_name"] = "neuphonic/neutts-air" } }
    ];

    #endregion
}
