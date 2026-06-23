using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Piper TTS provider -- CPU-only ONNX runtime TTS with dozens of pre-trained voices.</summary>
public sealed class PiperProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Piper provider.</summary>
    public static PiperProvider Instance { get; } = new();

    /// <summary>Builds and returns the Piper provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("piper_tts")
        .WithName("Piper TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Piper")
        .WithModelClass("piper_tts", "Piper TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("piper_tts_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Piper TTS", Description = "CPU-only ONNX runtime TTS with dozens of pre-trained voices", SourceUrl = "https://github.com/rhasspy/piper", License = "MIT", EstimatedSize = "~100MB", EstimatedVram = "CPU only" }
    ];

    #endregion
}
