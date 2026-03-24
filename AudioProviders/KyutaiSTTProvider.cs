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
        .WithPythonEngine("stt_kyutai", "KyutaiSTTEngine")
        .WithModelPrefix("KyutaiSTT")
        .WithModelClass("kyutaistt_stt", "Kyutai STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("kyutaistt_stt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "transformers>=4.53.0", InstallName = "transformers>=4.53.0", ImportName = "transformers", Category = "stt", EstimatedInstallTimeMinutes = 5 }
    ];

    #endregion

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
