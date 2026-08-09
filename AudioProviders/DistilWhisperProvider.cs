using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Distil-Whisper STT provider — 6x faster than Whisper large-v3, within 1% WER.</summary>
public sealed class DistilWhisperProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Distil-Whisper provider.</summary>
    public static DistilWhisperProvider Instance { get; } = new();

    /// <summary>Builds and returns the Distil-Whisper STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("distilwhisper_stt")
        .WithName("Distil-Whisper STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("DistilWhisper")
        .WithModelClass("distilwhisper_stt", "Distil-Whisper STT")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "large-v3", Name = "Distil-Whisper Large V3", Description = "6x faster than Whisper large-v3, within 1% WER", SourceUrl = "https://huggingface.co/distil-whisper/distil-large-v3", License = "MIT", EstimatedSize = "~1.5GB", EstimatedVram = "~2GB", EngineConfig = new() { ["model_name"] = "distil-whisper/distil-large-v3" } },
        new() { Id = "large-v3.5", Name = "Distil-Whisper Large V3.5", Description = "Latest, trained on 98k hours, 1.5x faster than Turbo", SourceUrl = "https://huggingface.co/distil-whisper/distil-large-v3.5", License = "MIT", EstimatedSize = "~1.5GB", EstimatedVram = "~2GB", EngineConfig = new() { ["model_name"] = "distil-whisper/distil-large-v3.5" } }
    ];

    #endregion
}
