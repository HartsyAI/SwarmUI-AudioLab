using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Chatterbox TTS provider — high-quality voice synthesis with expressive controls.</summary>
public sealed class ChatterboxProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Chatterbox TTS provider.</summary>
    public static ChatterboxProvider Instance { get; } = new();

    /// <summary>Builds and returns the Chatterbox TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("chatterbox_tts")
        .WithName("Chatterbox TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_chatterbox", "ChatterboxEngine")
        .WithModelPrefix("Chatterbox")
        .WithModelClass("chatterbox_tts", "Chatterbox TTS")
        .AddFeatureFlag("chatterbox_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("chatterbox")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "librosa==0.11.0", InstallName = "librosa==0.11.0", ImportName = "librosa", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchvision==0.21.0+cu126", InstallName = "torchvision==0.21.0+cu126", ImportName = "torchvision", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // chatterbox-tts from PyPI with --no-deps to skip dev/training deps (gradio, fastapi, tensorboard, etc.)
        new() { Name = "chatterbox-tts", InstallName = "chatterbox-tts", ImportName = "chatterbox", Category = "tts", EstimatedInstallTimeMinutes = 2, CustomInstallArgs = "--no-deps" },
        // Explicit chatterbox runtime dependencies (inference only)
        new() { Name = "s3tokenizer", InstallName = "s3tokenizer", ImportName = "s3tokenizer", Category = "tts" },
        new() { Name = "transformers==4.46.3", InstallName = "transformers==4.46.3", ImportName = "transformers", Category = "tts" },
        new() { Name = "diffusers==0.29.0", InstallName = "diffusers==0.29.0", ImportName = "diffusers", Category = "tts" },
        new() { Name = "resemble-perth==1.0.1", InstallName = "resemble-perth==1.0.1", ImportName = "resemble_perth", Category = "tts" },
        // perth needs pkg_resources which was removed from setuptools 78+ on Python 3.13
        new() { Name = "setuptools<78", InstallName = "setuptools<78", ImportName = "pkg_resources", Category = "tts" },
        new() { Name = "conformer==0.3.2", InstallName = "conformer==0.3.2", ImportName = "conformer", Category = "tts" },
        new() { Name = "safetensors==0.5.3", InstallName = "safetensors==0.5.3", ImportName = "safetensors", Category = "tts" },
        new() { Name = "omegaconf==2.3.0", InstallName = "omegaconf==2.3.0", ImportName = "omegaconf", Category = "tts" },
        new() { Name = "resampy==0.4.3", InstallName = "resampy==0.4.3", ImportName = "resampy", Category = "tts" },
        new() { Name = "peft", InstallName = "peft", ImportName = "peft", Category = "tts" },
        new() { Name = "langdetect", InstallName = "langdetect", ImportName = "langdetect", Category = "tts" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "tts" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Chatterbox TTS", Description = "High-quality voice synthesis with expressive controls (Exaggeration, CFG Weight)", SourceUrl = "https://github.com/resemble-ai/chatterbox", License = "MIT", EstimatedSize = "~2GB", EstimatedVram = "~4GB" }
    ];

    #endregion
}
