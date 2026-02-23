using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Orpheus TTS provider — LLM-based emotional speech synthesis with emotion tags.</summary>
public sealed class OrpheusTTSProvider : IAudioProviderSource
{
    public static OrpheusTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("orpheus_tts")
        .WithName("Orpheus TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_orpheus", "OrpheusEngine")
        .WithModelPrefix("Orpheus")
        .WithModelClass("orpheus_tts", "Orpheus TTS")
        .AddFeatureFlag("orpheus_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "orpheus-speech", InstallName = "orpheus-speech", ImportName = "orpheus_speech", Category = "tts", EstimatedInstallTimeMinutes = 10 },
        new() { Name = "vllm", InstallName = "vllm", ImportName = "vllm", Category = "tts", EstimatedInstallTimeMinutes = 15 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "3b", Name = "Orpheus 3B", Description = "Full-size model, best quality with emotion tags (~16GB VRAM)", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-3b-0.1-ft", ["model_size"] = "3b" } },
        new() { Id = "1b", Name = "Orpheus 1B", Description = "Mid-size model, good quality with lower VRAM (~8GB VRAM)", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-1b-0.1-ft", ["model_size"] = "1b" } },
        new() { Id = "400m", Name = "Orpheus 400M", Description = "Compact model, fast inference (~4GB VRAM)", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-400m-0.1-ft", ["model_size"] = "400m" } },
        new() { Id = "150m", Name = "Orpheus 150M", Description = "Tiny model, fastest inference (~2GB VRAM)", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-150m-0.1-ft", ["model_size"] = "150m" } }
    ];
}
