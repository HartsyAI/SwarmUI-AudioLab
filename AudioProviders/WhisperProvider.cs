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
        .WithModelPrefix("Whisper")
        .WithModelClass("whisper_stt", "Whisper STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("whisper_stt_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "tiny", Name = "Whisper Tiny", Description = "Fastest model, lowest accuracy (39M params)", SourceUrl = "https://huggingface.co/openai/whisper-tiny", License = "Apache 2.0", EstimatedSize = "~75MB", EstimatedVram = "~1GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "tiny" } },
        new() { Id = "base", Name = "Whisper Base", Description = "Good balance of speed and accuracy (74M params)", SourceUrl = "https://huggingface.co/openai/whisper-base", License = "Apache 2.0", EstimatedSize = "~150MB", EstimatedVram = "~1GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "base" } },
        new() { Id = "small", Name = "Whisper Small", Description = "Better accuracy, moderate speed (244M params)", SourceUrl = "https://huggingface.co/openai/whisper-small", License = "Apache 2.0", EstimatedSize = "~500MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "small" } },
        new() { Id = "medium", Name = "Whisper Medium", Description = "High accuracy, slower (769M params)", SourceUrl = "https://huggingface.co/openai/whisper-medium", License = "Apache 2.0", EstimatedSize = "~1.5GB", EstimatedVram = "~5GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "medium" } },
        new() { Id = "large-v2", Name = "Whisper Large V2", Description = "Best accuracy for many languages (1.5B params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v2", License = "Apache 2.0", EstimatedSize = "~3GB", EstimatedVram = "~10GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "large-v2" } },
        new() { Id = "large-v3", Name = "Whisper Large V3", Description = "Latest large model, improved accuracy (1.5B params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v3", License = "Apache 2.0", EstimatedSize = "~3GB", EstimatedVram = "~10GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "large-v3" } },
        new() { Id = "turbo", Name = "Whisper Turbo", Description = "Distilled large-v3, ~8x faster with near-large accuracy (809M params)", SourceUrl = "https://huggingface.co/openai/whisper-large-v3-turbo", License = "MIT", EstimatedSize = "~1.6GB", EstimatedVram = "~6GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "turbo" } }
    ];

    #endregion
}
