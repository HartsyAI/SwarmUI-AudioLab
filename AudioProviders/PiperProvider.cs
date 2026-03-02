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
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "piper-tts", InstallName = "piper-tts", ImportName = "piper", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "onnxruntime>=1.15.0", InstallName = "onnxruntime>=1.15.0", ImportName = "onnxruntime", Category = "core", EstimatedInstallTimeMinutes = 3 }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Piper TTS", Description = "CPU-only ONNX runtime TTS with dozens of pre-trained voices" }
    ];
}
