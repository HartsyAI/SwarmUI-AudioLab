using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Distil-Whisper STT provider — 6x faster than Whisper large-v3, within 1% WER.</summary>
public sealed class DistilWhisperProvider : IAudioProviderSource
{
    public static DistilWhisperProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("distilwhisper_stt")
        .WithName("Distil-Whisper STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_distilwhisper", "DistilWhisperEngine")
        .WithModelPrefix("DistilWhisper")
        .WithModelClass("distilwhisper_stt", "Distil-Whisper STT")
        .AddFeatureFlag("distilwhisper_stt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "stt" },
        new() { Name = "accelerate>=0.25.0", InstallName = "accelerate>=0.25.0", ImportName = "accelerate", Category = "stt" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "large-v3", Name = "Distil-Whisper Large V3", Description = "6x faster than Whisper large-v3, within 1% WER (~2GB VRAM)", EngineConfig = new() { ["model_name"] = "distil-whisper/distil-large-v3" } },
        new() { Id = "large-v3.5", Name = "Distil-Whisper Large V3.5", Description = "Latest, trained on 98k hours, 1.5x faster than Turbo (~2GB VRAM)", EngineConfig = new() { ["model_name"] = "distil-whisper/distil-large-v3.5" } }
    ];
}
