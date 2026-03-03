using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenAI Whisper STT provider — robust speech recognition across languages.</summary>
public sealed class WhisperProvider : IAudioProviderSource
{
    public static WhisperProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("whisper_stt")
        .WithName("Whisper STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_whisper", "WhisperEngine")
        .WithModelPrefix("Whisper")
        .WithModelClass("whisper_stt", "Whisper STT")
        .AddFeatureFlag("whisper_stt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy<2.0.0", InstallName = "numpy<2.0.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "faster-whisper==1.1.0", InstallName = "faster-whisper==1.1.0", ImportName = "faster_whisper", Category = "stt" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "tiny", Name = "Whisper Tiny", Description = "Fastest model, lowest accuracy", SourceUrl = "https://huggingface.co/openai/whisper-tiny", License = "MIT", EstimatedSize = "~75MB", EstimatedVram = "~1GB", EngineConfig = new() { ["model_size"] = "tiny" } },
        new() { Id = "base", Name = "Whisper Base", Description = "Good balance of speed and accuracy", SourceUrl = "https://huggingface.co/openai/whisper-base", License = "MIT", EstimatedSize = "~150MB", EstimatedVram = "~1GB", EngineConfig = new() { ["model_size"] = "base" } },
        new() { Id = "small", Name = "Whisper Small", Description = "Better accuracy, moderate speed", SourceUrl = "https://huggingface.co/openai/whisper-small", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", EngineConfig = new() { ["model_size"] = "small" } },
        new() { Id = "medium", Name = "Whisper Medium", Description = "High accuracy, slower", SourceUrl = "https://huggingface.co/openai/whisper-medium", License = "MIT", EstimatedSize = "~1.5GB", EstimatedVram = "~5GB", EngineConfig = new() { ["model_size"] = "medium" } },
        new() { Id = "large-v3", Name = "Whisper Large V3", Description = "Best accuracy, slowest", SourceUrl = "https://huggingface.co/openai/whisper-large-v3", License = "MIT", EstimatedSize = "~3GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_size"] = "large-v3" } }
    ];
}
