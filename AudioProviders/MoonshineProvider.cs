using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Moonshine STT provider — ultra-fast speech recognition, 5x faster than Whisper.</summary>
public sealed class MoonshineProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Moonshine provider.</summary>
    public static MoonshineProvider Instance { get; } = new();

    /// <summary>Builds and returns the Moonshine STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("moonshine_stt")
        .WithName("Moonshine STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_moonshine", "MoonshineEngine")
        .WithModelPrefix("Moonshine")
        .WithModelClass("moonshine_stt", "Moonshine STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("moonshine_stt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "useful-moonshine-onnx", InstallName = "useful-moonshine-onnx", ImportName = "moonshine_onnx", Category = "stt", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "base", Name = "Moonshine Base", Description = "Fast transcription, good accuracy", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-base", License = "MIT", EstimatedSize = "~400MB", EstimatedVram = "~1GB (or CPU)", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshine/base" } },
        new() { Id = "tiny", Name = "Moonshine Tiny", Description = "Fastest transcription, lighter accuracy, CPU-capable", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-tiny", License = "MIT", EstimatedSize = "~200MB", EstimatedVram = "CPU only", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshine/tiny" } }
    ];

    #endregion
}
