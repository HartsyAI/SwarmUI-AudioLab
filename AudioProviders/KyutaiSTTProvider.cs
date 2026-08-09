using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Kyutai STT provider — delayed-streams speech-to-text with capitalization and punctuation.</summary>
public sealed class KyutaiSTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Kyutai STT provider.</summary>
    public static KyutaiSTTProvider Instance { get; } = new();

    /// <summary>Builds and returns the Kyutai STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("kyutaistt_stt")
        .WithName("Kyutai STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("KyutaiSTT")
        .WithModelClass("kyutaistt_stt", "Kyutai STT")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "1b-en-fr",
            Name = "Kyutai STT 1B (English + French)",
            Description = "1B params, bilingual English/French transcription with semantic voice activity detection. 0.5s latency.",
            SourceUrl = "https://huggingface.co/kyutai/stt-1b-en_fr-trfs",
            License = "CC-BY 4.0",
            EstimatedSize = "~2.7GB",
            EstimatedVram = "~3 GB",
            EngineConfig = new() { ["model_name"] = "kyutai/stt-1b-en_fr-trfs" }
        },
        new()
        {
            Id = "2.6b-en",
            Name = "Kyutai STT 2.6B (English)",
            Description = "2.6B params, high-accuracy English-only transcription with auto punctuation and capitalization.",
            SourceUrl = "https://huggingface.co/kyutai/stt-2.6b-en-trfs",
            License = "CC-BY 4.0",
            EstimatedSize = "~5.9GB",
            EstimatedVram = "~6 GB",
            EngineConfig = new() { ["model_name"] = "kyutai/stt-2.6b-en-trfs" }
        }
    ];

    #endregion
}
