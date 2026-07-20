using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ZipVoice provider — k2-fsa's zero-shot voice cloning via a Zipformer flow-matching backbone.</summary>
public sealed class ZipVoiceProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the ZipVoice provider.</summary>
    public static ZipVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the ZipVoice provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("zipvoice_tts")
        .WithName("ZipVoice")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("ZipVoice")
        .WithModelClass("zipvoice_tts", "ZipVoice")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("zipvoice_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "base", Name = "ZipVoice", Description = "Zero-shot voice cloning via flow matching over a Zipformer backbone, English only", SourceUrl = "https://huggingface.co/k2-fsa/ZipVoice", License = "Apache 2.0", EstimatedSize = "~500MB", EstimatedVram = "~2GB", EngineConfig = new() { ["model_name"] = "k2-fsa/ZipVoice" } }
    ];

    #endregion
}
