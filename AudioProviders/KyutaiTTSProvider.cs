using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Kyutai TTS 1.6B provider — streaming TTS with voice conditioning via delayed-streams modeling.</summary>
public sealed class KyutaiTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Kyutai TTS provider.</summary>
    public static KyutaiTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Kyutai TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("kyutaitts_tts")
        .WithName("Kyutai TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_kyutai", "KyutaiTTSEngine")
        .WithModelPrefix("KyutaiTTS")
        .WithModelClass("kyutaitts_tts", "Kyutai TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("kyutaitts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
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
        new() { Name = "moshi>=0.2.11", InstallName = "moshi>=0.2.11", ImportName = "moshi", Category = "tts", EstimatedInstallTimeMinutes = 8 }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "1.6b-en-fr",
            Name = "Kyutai TTS 1.6B",
            Description = "1.8B params, English + French, streaming generation, voice conditioning from audio samples. ~200ms latency, 75x real-time on GPU.",
            SourceUrl = "https://huggingface.co/kyutai/tts-1.6b-en_fr",
            License = "CC-BY 4.0",
            EstimatedSize = "~4GB",
            EstimatedVram = "~8 GB"
        }
    ];

    #endregion
}
