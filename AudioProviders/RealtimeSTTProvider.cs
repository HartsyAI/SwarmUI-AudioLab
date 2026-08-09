using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>RealtimeSTT provider — real-time streaming speech-to-text with wake word detection.</summary>
public sealed class RealtimeSTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the RealtimeSTT provider.</summary>
    public static RealtimeSTTProvider Instance { get; } = new();

    /// <summary>Builds and returns the RealtimeSTT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("realtimestt_stt")
        .WithName("RealtimeSTT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("RealtimeSTT")
        .WithModelClass("realtimestt_stt", "RealtimeSTT")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .WithRequiresDocker()
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "RealtimeSTT Default", Description = "Real-time streaming transcription with wake word detection", SourceUrl = "https://github.com/KoljaB/RealtimeSTT", License = "MIT", EstimatedSize = "~1GB", EstimatedVram = "~2GB" }
    ];

    #endregion
}
