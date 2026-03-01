using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Demucs provider — Meta's audio source separation (vocals, drums, bass, other).</summary>
public sealed class DemucsProvider : IAudioProviderSource
{
    public static DemucsProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("demucs_fx")
        .WithName("Demucs Separation")
        .WithCategory(AudioCategory.AudioFX)
        .WithPythonEngine("fx_demucs", "DemucsEngine")
        .WithModelPrefix("Demucs")
        .WithModelClass("demucs_fx", "Demucs Separation")
        .AddFeatureFlag("demucs_fx_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("fx")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "demucs", InstallName = "demucs", ImportName = "demucs", Category = "audio_fx", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "htdemucs", Name = "HTDemucs", Description = "Hybrid Transformer Demucs — best quality 4-stem separation (~2GB VRAM)", EngineConfig = new() { ["model_name"] = "htdemucs" } },
        new() { Id = "htdemucs_ft", Name = "HTDemucs Fine-tuned", Description = "Fine-tuned variant, highest quality separation (~2GB VRAM)", EngineConfig = new() { ["model_name"] = "htdemucs_ft" } },
        new() { Id = "htdemucs_6s", Name = "HTDemucs 6-Stem", Description = "6-stem separation (vocals, drums, bass, guitar, piano, other)", EngineConfig = new() { ["model_name"] = "htdemucs_6s" } }
    ];
}
