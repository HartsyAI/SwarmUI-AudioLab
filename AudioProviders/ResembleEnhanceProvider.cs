using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Resemble Enhance provider — AI-powered speech denoising and super-resolution to 44.1kHz.</summary>
public sealed class ResembleEnhanceProvider : IAudioProviderSource
{
    public static ResembleEnhanceProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("resemble_enhance_fx")
        .WithName("Resemble Enhance")
        .WithCategory(AudioCategory.AudioFX)
        .WithPythonEngine("fx_resemble_enhance", "ResembleEnhanceEngine")
        .WithModelPrefix("ResembleEnhance")
        .WithModelClass("resemble_enhance_fx", "Resemble Enhance")
        .AddFeatureFlag("resemble_enhance_fx_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .WithRequiresDocker()
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "resemble-enhance", InstallName = "resemble-enhance", ImportName = "resemble_enhance", Category = "audio_fx", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "denoise", Name = "Resemble Denoise", Description = "Speech denoising — removes background noise from audio (~2GB VRAM)", EngineConfig = new() { ["mode"] = "denoise" } },
        new() { Id = "enhance", Name = "Resemble Enhance", Description = "Full enhancement — denoise + super-resolution to 44.1kHz (~2GB VRAM)", EngineConfig = new() { ["mode"] = "enhance" } }
    ];
}
