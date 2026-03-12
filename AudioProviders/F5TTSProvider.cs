using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>F5-TTS provider — zero-shot voice cloning from 15-second reference audio.</summary>
public sealed class F5TTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the F5-TTS provider.</summary>
    public static F5TTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the F5-TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("f5_tts")
        .WithName("F5-TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_f5", "F5TTSEngine")
        .WithModelPrefix("F5TTS")
        .WithModelClass("f5_tts", "F5-TTS")
        .AddFeatureFlag("f5_tts_params")
        .AddFeatureFlag("tts_cfg")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "f5-tts", InstallName = "git+https://github.com/SWivid/F5-TTS.git", ImportName = "f5_tts", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 10 },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "F5-TTS Default", Description = "Zero-shot voice cloning from 15s reference audio", SourceUrl = "https://huggingface.co/SWivid/F5-TTS", License = "CC-BY-NC-4.0", EstimatedSize = "~2.5GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "SWivid/F5-TTS" } }
    ];

    #endregion
}
