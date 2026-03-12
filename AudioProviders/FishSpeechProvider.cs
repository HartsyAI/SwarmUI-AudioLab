using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Fish Speech TTS provider -- dual-autoregressive TTS with inline prosody control supporting 80+ languages and voice cloning.</summary>
public sealed class FishSpeechProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Fish Speech provider.</summary>
    public static FishSpeechProvider Instance { get; } = new();

    /// <summary>Builds and returns the Fish Speech provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fishspeech_tts")
        .WithName("Fish Speech TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_fishspeech", "FishSpeechEngine")
        .WithModelPrefix("FishSpeech")
        .WithModelClass("fishspeech_tts", "Fish Speech TTS")
        .AddFeatureFlag("fishspeech_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // Installed with --no-deps to avoid pulling pinned versions of pydantic, datasets, gradio, etc.
        new() { Name = "fish-speech", InstallName = "fish-speech", ImportName = "fish_speech", Category = "tts", CustomInstallArgs = "--no-deps", EstimatedInstallTimeMinutes = 5 },
        // Explicit runtime dependencies needed by fish-speech inference
        new() { Name = "transformers>=4.45.2", InstallName = "transformers>=4.45.2", ImportName = "transformers", Category = "tts" },
        new() { Name = "einops>=0.7.0", InstallName = "einops>=0.7.0", ImportName = "einops", Category = "tts" },
        new() { Name = "loguru>=0.6.0", InstallName = "loguru>=0.6.0", ImportName = "loguru", Category = "tts" },
        new() { Name = "tiktoken>=0.8.0", InstallName = "tiktoken>=0.8.0", ImportName = "tiktoken", Category = "tts" },
        new() { Name = "descript-audio-codec", InstallName = "descript-audio-codec", ImportName = "dac", Category = "tts" },
        new() { Name = "descript-audiotools", InstallName = "descript-audiotools", ImportName = "audiotools", Category = "tts" },
        new() { Name = "safetensors", InstallName = "safetensors", ImportName = "safetensors", Category = "tts" },
        new() { Name = "pydantic>=2.0.0", InstallName = "pydantic>=2.0.0", ImportName = "pydantic", Category = "tts" },
        new() { Name = "silero-vad", InstallName = "silero-vad", ImportName = "silero_vad", Category = "tts" },
        new() { Name = "ormsgpack", InstallName = "ormsgpack", ImportName = "ormsgpack", Category = "tts" },
        new() { Name = "zstandard>=0.22.0", InstallName = "zstandard>=0.22.0", ImportName = "zstandard", Category = "tts" },
        new() { Name = "einx>=0.2.2", InstallName = "einx>=0.2.2", ImportName = "einx", Category = "tts" },
        new() { Name = "resampy>=0.4.3", InstallName = "resampy>=0.4.3", ImportName = "resampy", Category = "tts" },
        new() { Name = "librosa>=0.10.1", InstallName = "librosa>=0.10.1", ImportName = "librosa", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "hydra-core>=1.3.2", InstallName = "hydra-core>=1.3.2", ImportName = "hydra", Category = "tts" },
        new() { Name = "natsort>=8.4.0", InstallName = "natsort>=8.4.0", ImportName = "natsort", Category = "tts" },
        new() { Name = "rich>=13.5.3", InstallName = "rich>=13.5.3", ImportName = "rich", Category = "tts" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "tts" },
        new() { Name = "lightning", InstallName = "lightning", ImportName = "lightning", Category = "tts" },
        new() { Name = "loralib>=0.1.2", InstallName = "loralib>=0.1.2", ImportName = "loralib", Category = "tts" },
        new() { Name = "cachetools", InstallName = "cachetools", ImportName = "cachetools", Category = "tts" },
        new() { Name = "pyrootutils>=1.0.4", InstallName = "pyrootutils>=1.0.4", ImportName = "pyrootutils", Category = "tts" },
        new() { Name = "pydub", InstallName = "pydub", ImportName = "pydub", Category = "tts" },
        new() { Name = "opencc-python-reimplemented==0.1.7", InstallName = "opencc-python-reimplemented==0.1.7", ImportName = "opencc", Category = "tts" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "s2-pro",
            Name = "Fish Speech S2-Pro",
            Description = "5B parameter flagship model. 80+ languages, inline prosody control with [tag] syntax, voice cloning. Requires ~24GB VRAM.",
            SourceUrl = "https://huggingface.co/fishaudio/s2-pro",
            License = "Fish Audio Research License",
            EstimatedSize = "~10GB",
            EstimatedVram = "~24GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/s2-pro" }
        },
        new()
        {
            Id = "s1-mini",
            Name = "Fish Speech S1-Mini",
            Description = "Lightweight variant for resource-constrained deployment. Requires ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/fishaudio/s1-mini",
            License = "Fish Audio Research License",
            EstimatedSize = "~1GB",
            EstimatedVram = "~4GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/s1-mini" }
        }
    ];

    #endregion
}
