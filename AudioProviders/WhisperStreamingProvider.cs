using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Whisper Streaming provider — Whisper weights driven through the engine's streaming pipeline
/// (LocalAgreement-2 stabilizer), 16 kHz. Engine-hosted (see <see cref="AudioServices.Stt.SttModels.WhisperStreaming"/>).</summary>
public sealed class WhisperStreamingProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Whisper Streaming provider.</summary>
    public static WhisperStreamingProvider Instance { get; } = new();

    /// <summary>Builds and returns the Whisper Streaming provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("whisperstreaming_stt")
        .WithName("Whisper Streaming")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("WhisperStreaming")
        .WithModelClass("whisperstreaming_stt", "Whisper Streaming")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "base", Name = "Whisper Streaming (base)", Description = "Whisper base via the LocalAgreement-2 streaming stabilizer, 16 kHz", SourceUrl = "https://huggingface.co/openai/whisper-base", License = "MIT", EstimatedSize = "~150MB", EstimatedVram = "~1GB (or CPU)" }
    ];
}
