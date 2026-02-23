using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ACE-Step provider — SOTA music generation foundation model with lyrics alignment.</summary>
public sealed class AceStepProvider : IAudioProviderSource
{
    public static AceStepProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("acestep_music")
        .WithName("ACE-Step Music")
        .WithCategory(AudioCategory.MusicGen)
        .WithPythonEngine("music_acestep", "AceStepEngine")
        .WithModelPrefix("AceStep")
        .WithModelClass("acestep_music", "ACE-Step Music")
        .AddFeatureFlag("acestep_music_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "ace-step", InstallName = "git+https://github.com/ace-step/ACE-Step.git", ImportName = "ace_step", Category = "music", IsGitPackage = true, EstimatedInstallTimeMinutes = 15 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v1.5", Name = "ACE-Step v1.5", Description = "SOTA music generation, up to 4 min in 20s, lyrics alignment (~8GB VRAM)", EngineConfig = new() { ["model_version"] = "1.5" } },
        new() { Id = "v1", Name = "ACE-Step v1", Description = "Original music generation model (~6GB VRAM)", EngineConfig = new() { ["model_version"] = "1.0" } }
    ];
}
