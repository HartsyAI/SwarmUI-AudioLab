using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>GPT-SoVITS provider — few-shot voice cloning (1 min reference), strong multilingual/CJK support.</summary>
public sealed class GPTSoVITSProvider : IAudioProviderSource
{
    public static GPTSoVITSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("gptsovits_clone")
        .WithName("GPT-SoVITS")
        .WithCategory(AudioCategory.VoiceClone)
        .WithPythonEngine("clone_gptsovits", "GPTSoVITSEngine")
        .WithModelPrefix("GPTSoVITS")
        .WithModelClass("gptsovits_clone", "GPT-SoVITS")
        .AddFeatureFlag("gptsovits_clone_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "voice_clone" },
        new() { Name = "librosa>=0.10.0", InstallName = "librosa>=0.10.0", ImportName = "librosa", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "GPT-SoVITS Default", Description = "Few-shot voice cloning from 1 min reference, CJK + English (~4GB VRAM)" }
    ];
}
