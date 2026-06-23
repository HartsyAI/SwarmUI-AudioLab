using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>F5-TTS provider — zero-shot voice cloning via flow matching from short reference audio.</summary>
public sealed class F5TTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the F5-TTS provider.</summary>
    public static F5TTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the F5-TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("f5_tts")
        .WithName("F5-TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("F5TTS")
        .WithModelClass("f5_tts", "F5-TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("f5_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v1-base", Name = "F5-TTS v1 Base", Description = "Zero-shot voice cloning from ~10s reference audio, flow matching DiT", SourceUrl = "https://huggingface.co/SWivid/F5-TTS", License = "CC-BY-NC-4.0", EstimatedSize = "~1.3GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "SWivid/F5-TTS" } }
    ];

    #endregion
}
