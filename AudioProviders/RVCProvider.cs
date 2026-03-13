using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>RVC provider — converts the voice in existing audio to a different voice using a trained model. Audio in, audio out (no text generation).</summary>
public sealed class RVCProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the RVC provider.</summary>
    public static RVCProvider Instance { get; } = new();

    /// <summary>Builds and returns the RVC voice conversion provider definition. Takes existing audio + a voice model, outputs the same speech in the target voice.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("rvc_clone")
        .WithName("RVC Voice Conversion")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithPythonEngine("clone_rvc", "RVCEngine")
        .WithModelPrefix("RVC")
        .WithModelClass("rvc_clone", "RVC Voice Conversion")
        .AddFeatureFlag("rvc_clone_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .WithRequiresDocker()
        .Build();

    #region Dependencies

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

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "RVC V2", Description = "Voice conversion: re-voices existing audio using a trained .pth voice model. Does not generate new speech — takes audio in, outputs the same speech in a different voice.", SourceUrl = "https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_version"] = "v2" } }
    ];

    #endregion
}
