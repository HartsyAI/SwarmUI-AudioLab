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
        .WithModelPrefix("KyutaiTTS")
        .WithModelClass("kyutaitts_tts", "Kyutai TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("kyutaitts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

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
