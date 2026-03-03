using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Zonos TTS provider — multilingual TTS trained on 200k+ hours with zero-shot cloning.</summary>
public sealed class ZonosProvider : IAudioProviderSource
{
    public static ZonosProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("zonos_tts")
        .WithName("Zonos TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_zonos", "ZonosEngine")
        .WithModelPrefix("Zonos")
        .WithModelClass("zonos_tts", "Zonos TTS")
        .AddFeatureFlag("zonos_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_cfg")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "zonos", InstallName = "git+https://github.com/Zyphra/Zonos.git", ImportName = "zonos", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "transformer", Name = "Zonos Transformer", Description = "Transformer-based, multilingual (EN/JP/CN/FR/DE)", SourceUrl = "https://huggingface.co/Zyphra/Zonos-v0.1-transformer", License = "Apache 2.0", EstimatedSize = "~2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "Zyphra/Zonos-v0.1-transformer" } },
        new() { Id = "hybrid", Name = "Zonos Hybrid", Description = "Hybrid architecture, best quality with zero-shot cloning", SourceUrl = "https://huggingface.co/Zyphra/Zonos-v0.1-hybrid", License = "Apache 2.0", EstimatedSize = "~2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "Zyphra/Zonos-v0.1-hybrid" } }
    ];
}
