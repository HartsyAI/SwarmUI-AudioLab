using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenVoice v2 provider — zero-shot voice cloning with granular tone/style control.</summary>
public sealed class OpenVoiceProvider : IAudioProviderSource
{
    public static OpenVoiceProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("openvoice_clone")
        .WithName("OpenVoice V2")
        .WithCategory(AudioCategory.VoiceClone)
        .WithPythonEngine("clone_openvoice", "OpenVoiceEngine")
        .WithModelPrefix("OpenVoice")
        .WithModelClass("openvoice_clone", "OpenVoice V2")
        .AddFeatureFlag("openvoice_clone_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "openvoice", InstallName = "git+https://github.com/myshell-ai/OpenVoice.git", ImportName = "openvoice", Category = "voice_clone", IsGitPackage = true, EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "OpenVoice V2", Description = "Zero-shot voice cloning with tone/style transfer (~2GB VRAM)", EngineConfig = new() { ["model_version"] = "v2" } }
    ];
}
