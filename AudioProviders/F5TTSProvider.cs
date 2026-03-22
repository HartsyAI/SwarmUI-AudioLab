using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>F5-TTS provider — zero-shot voice cloning via flow matching from short reference audio.</summary>
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
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("f5_tts_params")
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
        new() { Name = "f5-tts>=1.1.0", InstallName = "f5-tts>=1.1.0", ImportName = "f5_tts", Category = "tts", EstimatedInstallTimeMinutes = 8, CustomInstallArgs = "--no-deps" },
        // Explicit f5-tts runtime dependencies (installed with --no-deps)
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torchdiffeq", InstallName = "torchdiffeq", ImportName = "torchdiffeq", Category = "tts" },
        new() { Name = "x-transformers>=1.31.14", InstallName = "x-transformers>=1.31.14", ImportName = "x_transformers", Category = "tts" },
        new() { Name = "vocos", InstallName = "vocos", ImportName = "vocos", Category = "tts" },
        new() { Name = "librosa", InstallName = "librosa", ImportName = "librosa", Category = "tts" },
        new() { Name = "pydub", InstallName = "pydub", ImportName = "pydub", Category = "tts" },
        new() { Name = "safetensors", InstallName = "safetensors", ImportName = "safetensors", Category = "tts" },
        new() { Name = "pypinyin", InstallName = "pypinyin", ImportName = "pypinyin", Category = "tts" },
        new() { Name = "rjieba", InstallName = "rjieba", ImportName = "rjieba", Category = "tts" },
        new() { Name = "cached_path", InstallName = "cached_path", ImportName = "cached_path", Category = "tts" },
        new() { Name = "tomli", InstallName = "tomli", ImportName = "tomli", Category = "tts" },
        new() { Name = "unidecode", InstallName = "unidecode", ImportName = "unidecode", Category = "tts" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "tts" },
        new() { Name = "omegaconf", InstallName = "omegaconf", ImportName = "omegaconf", Category = "tts" },
        new() { Name = "hydra-core>=1.3.0", InstallName = "hydra-core>=1.3.0", ImportName = "hydra", Category = "tts" },
        new() { Name = "matplotlib", InstallName = "matplotlib", ImportName = "matplotlib", Category = "tts" },
        new() { Name = "tqdm", InstallName = "tqdm", ImportName = "tqdm", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v1-base", Name = "F5-TTS v1 Base", Description = "Zero-shot voice cloning from ~10s reference audio, flow matching DiT", SourceUrl = "https://huggingface.co/SWivid/F5-TTS", License = "CC-BY-NC-4.0", EstimatedSize = "~1.3GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "SWivid/F5-TTS" } }
    ];

    #endregion
}
