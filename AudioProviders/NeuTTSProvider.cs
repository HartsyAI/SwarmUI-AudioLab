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
        .WithPythonEngine("tts_neutts", "NeuTTSEngine")
        .WithModelPrefix("NeuTTS")
        .WithModelClass("neutts_tts", "NeuTTS Air")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("neutts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "neutts", InstallName = "neutts", ImportName = "neutts", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "onnxruntime>=1.17.0", InstallName = "onnxruntime>=1.17.0", ImportName = "onnxruntime", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "air", Name = "NeuTTS Air", Description = "On-device TTS with instant voice cloning, 0.5B params", SourceUrl = "https://huggingface.co/neuphonic/neutts-air", License = "Apache 2.0", EstimatedSize = "~1GB", EstimatedVram = "~2GB (or CPU)", EngineConfig = new() { ["model_name"] = "neuphonic/neutts-air" } }
    ];

    #endregion
}
