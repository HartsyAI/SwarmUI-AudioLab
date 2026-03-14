using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenAI Whisper STT provider — robust speech recognition across languages.</summary>
public sealed class WhisperProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Whisper provider.</summary>
    public static WhisperProvider Instance { get; } = new();

    /// <summary>Builds and returns the Whisper STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("whisper_stt")
        .WithName("Whisper STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_whisper", "WhisperEngine")
        .WithModelPrefix("Whisper")
        .WithModelClass("whisper_stt", "Whisper STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("whisper_stt_params")
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
        new() { Name = "openai-whisper", InstallName = "openai-whisper", ImportName = "whisper", Category = "stt", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "ffmpeg-python", InstallName = "ffmpeg-python", ImportName = "ffmpeg", Category = "stt" },
        new() { Name = "imageio-ffmpeg", InstallName = "imageio-ffmpeg", ImportName = "imageio_ffmpeg", Category = "stt" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "tiny", Name = "Whisper Tiny", Description = "Fastest model, lowest accuracy (39M params)", SourceUrl = "https://huggingface.co/openai/whisper-tiny", License = "MIT", EstimatedSize = "~75MB", EstimatedVram = "~1GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "tiny" } },
        new() { Id = "base", Name = "Whisper Base", Description = "Good balance of speed and accuracy (74M params)", SourceUrl = "https://huggingface.co/openai/whisper-base", License = "MIT", EstimatedSize = "~150MB", EstimatedVram = "~1GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "base" } },
        new() { Id = "small", Name = "Whisper Small", Description = "Better accuracy, moderate speed (244M params)", SourceUrl = "https://huggingface.co/openai/whisper-small", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "small" } },
        new() { Id = "medium", Name = "Whisper Medium", Description = "High accuracy, slower (769M params)", SourceUrl = "https://huggingface.co/openai/whisper-medium", License = "MIT", EstimatedSize = "~1.5GB", EstimatedVram = "~5GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "medium" } },
        new() { Id = "large-v2", Name = "Whisper Large V2", Description = "Best accuracy for many languages (1.5B params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v2", License = "MIT", EstimatedSize = "~3GB", EstimatedVram = "~10GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "large-v2" } },
        new() { Id = "large-v3", Name = "Whisper Large V3", Description = "Latest large model, improved accuracy (1.5B params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v3", License = "MIT", EstimatedSize = "~3GB", EstimatedVram = "~10GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "large-v3" } },
        new() { Id = "turbo", Name = "Whisper Turbo", Description = "Distilled large-v3, ~8x faster with near-large accuracy (809M params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v3-turbo", License = "MIT", EstimatedSize = "~1.6GB", EstimatedVram = "~6GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "turbo" } }
    ];

    #endregion
}
