using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Piper TTS provider — CPU-only ONNX runtime TTS with dozens of pre-trained voices.</summary>
public sealed class PiperProvider : IAudioProviderSource
{
    public static PiperProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("piper_tts")
        .WithName("Piper TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_piper", "PiperEngine")
        .WithModelPrefix("Piper")
        .WithModelClass("piper_tts", "Piper TTS")
        .AddFeatureFlag("piper_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "piper-tts", InstallName = "piper-tts", ImportName = "piper", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "onnxruntime>=1.15.0", InstallName = "onnxruntime>=1.15.0", ImportName = "onnxruntime", Category = "core", EstimatedInstallTimeMinutes = 3 }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "en-us-amy", Name = "Piper Amy (US)", Description = "English US female voice, medium quality (CPU only)", EngineConfig = new() { ["voice"] = "en_US-amy-medium" } },
        new() { Id = "en-us-danny", Name = "Piper Danny (US)", Description = "English US male voice, medium quality (CPU only)", EngineConfig = new() { ["voice"] = "en_US-danny-low" } },
        new() { Id = "en-gb-alba", Name = "Piper Alba (GB)", Description = "English GB female voice, medium quality (CPU only)", EngineConfig = new() { ["voice"] = "en_GB-alba-medium" } }
    ];
}
