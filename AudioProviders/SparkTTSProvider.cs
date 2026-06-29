using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Spark-TTS provider — Qwen2.5-0.5B LM + BiCodec, 16 kHz (SparkAudio). Engine-hosted; gated on the
/// engine's token-offset/BiCodec checkpoint reconciliation (see <see cref="AudioServices.Tts.SparkTtsModel"/>).</summary>
public sealed class SparkTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Spark-TTS provider.</summary>
    public static SparkTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Spark-TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("sparktts_tts")
        .WithName("Spark-TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("SparkTTS")
        .WithModelClass("sparktts_tts", "Spark-TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "0.5B", Name = "Spark-TTS 0.5B", Description = "Qwen2.5-0.5B LM + BiCodec, 16 kHz, voice cloning", SourceUrl = "https://huggingface.co/SparkAudio/Spark-TTS-0.5B", License = "CC-BY-NC-SA-4.0", EstimatedSize = "~1.2GB", EstimatedVram = "~4GB" }
    ];
}
