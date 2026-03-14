using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>GPT-SoVITS provider — generates new speech from text in a cloned voice (TTS with voice cloning). Requires ~1 min reference audio. Strong multilingual/CJK support.</summary>
public sealed class GPTSoVITSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the GPT-SoVITS provider.</summary>
    public static GPTSoVITSProvider Instance { get; } = new();

    /// <summary>Builds and returns the GPT-SoVITS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("gptsovits_clone")
        .WithName("GPT-SoVITS")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithPythonEngine("clone_gptsovits", "GPTSoVITSEngine")
        .WithModelPrefix("GPTSoVITS")
        .WithModelClass("gptsovits_clone", "GPT-SoVITS")
        .AddFeatureFlag("audiolab_clone")
        .AddFeatureFlag("gptsovits_clone_params")
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
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "voice_clone" },
        new() { Name = "librosa>=0.10.0", InstallName = "librosa>=0.10.0", ImportName = "librosa", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "GPT-SoVITS Default", Description = "Text-to-speech with voice cloning: generates new speech from text using a ~1 min reference audio clip. Unlike RVC/OpenVoice, this creates speech from text rather than converting existing audio. CJK + English.", SourceUrl = "https://github.com/RVC-Boss/GPT-SoVITS", License = "MIT", EstimatedSize = "~2GB", EstimatedVram = "~4GB" }
    ];

    #endregion
}
