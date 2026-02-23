using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>CosyVoice provider — Alibaba's streaming multilingual TTS with ultra-low latency.</summary>
public sealed class CosyVoiceProvider : IAudioProviderSource
{
    public static CosyVoiceProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("cosyvoice_tts")
        .WithName("CosyVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_cosyvoice", "CosyVoiceEngine")
        .WithModelPrefix("CosyVoice")
        .WithModelClass("cosyvoice_tts", "CosyVoice TTS")
        .AddFeatureFlag("cosyvoice_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "2-0.5b", Name = "CosyVoice2 0.5B", Description = "Streaming TTS with ultra-low latency, multilingual (~8GB VRAM)", EngineConfig = new() { ["model_name"] = "FunAudioLLM/CosyVoice2-0.5B" } }
    ];
}
