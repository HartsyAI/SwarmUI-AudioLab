using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Deepgram Nova-3 STT provider -- fastest API STT with diarization.</summary>
public sealed class DeepgramSTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static DeepgramSTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("deepgram_stt")
        .WithName("Deepgram Nova-3 STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("DeepgramSTT")
        .WithModelClass("deepgram_stt", "Deepgram Nova-3 STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("deepgram_stt_params")
        .WithApiProvider("deepgram_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Deepgram Nova-3", Description = "Fastest API STT with speaker diarization and smart formatting", SourceUrl = "https://deepgram.com", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
