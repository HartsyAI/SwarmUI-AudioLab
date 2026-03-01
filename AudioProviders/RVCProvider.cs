using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>RVC provider — retrieval-based voice conversion, industry standard for voice transformation.</summary>
public sealed class RVCProvider : IAudioProviderSource
{
    public static RVCProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("rvc_clone")
        .WithName("RVC Voice Conversion")
        .WithCategory(AudioCategory.VoiceClone)
        .WithPythonEngine("clone_rvc", "RVCEngine")
        .WithModelPrefix("RVC")
        .WithModelClass("rvc_clone", "RVC Voice Conversion")
        .AddFeatureFlag("rvc_clone_params")
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
        new() { Name = "faiss-cpu", InstallName = "faiss-cpu", ImportName = "faiss", Category = "voice_clone", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "librosa>=0.10.0", InstallName = "librosa>=0.10.0", ImportName = "librosa", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "scipy>=1.10.0", InstallName = "scipy>=1.10.0", ImportName = "scipy", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "RVC V2", Description = "Retrieval-based voice conversion with pre-trained index models (~4GB VRAM)", EngineConfig = new() { ["model_version"] = "v2" } }
    ];
}
