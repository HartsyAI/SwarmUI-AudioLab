using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Moonshine 2nd-gen streaming STT provider — Useful Sensors' sliding-window-attention architecture
/// (tiny/small/medium), a distinct model family from the original Moonshine (see <see cref="MoonshineProvider"/>):
/// mel-free frame-CMVN front end, no encoder RoPE, untied LM head. Full-utterance batch decode today (not yet
/// true chunked/incremental streaming).</summary>
public sealed class MoonshineStreamingProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the streaming Moonshine provider.</summary>
    public static MoonshineStreamingProvider Instance { get; } = new();

    /// <summary>Builds and returns the streaming Moonshine STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("moonshinestreaming_stt")
        .WithName("Moonshine Streaming STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("MoonshineStreaming")
        .WithModelClass("moonshinestreaming_stt", "Moonshine Streaming STT")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "tiny", Name = "Moonshine Streaming Tiny", Description = "44.1M params, fastest of the streaming family", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-streaming-tiny", License = "MIT", EstimatedSize = "~175MB", EstimatedVram = "~1GB (or CPU)", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshinestreaming/tiny" } },
        new() { Id = "small", Name = "Moonshine Streaming Small", Description = "0.1B params, balanced speed/accuracy", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-streaming-small", License = "MIT", EstimatedSize = "~440MB", EstimatedVram = "~1.5GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshinestreaming/small" } },
        new() { Id = "medium", Name = "Moonshine Streaming Medium", Description = "0.3B params, outperforms Whisper large-v3 per the model card", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-streaming-medium", License = "MIT", EstimatedSize = "~1.2GB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshinestreaming/medium" } },
    ];

    #endregion
}
