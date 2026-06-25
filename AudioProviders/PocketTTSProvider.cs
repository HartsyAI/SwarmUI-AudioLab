using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Pocket TTS provider — Kyutai's 100M parameter CPU-capable TTS with built-in voices and voice cloning.</summary>
public sealed class PocketTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Pocket TTS provider.</summary>
    public static PocketTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Pocket TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("pockettts_tts")
        .WithName("Pocket TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("PocketTTS")
        .WithModelClass("pockettts_tts", "Pocket TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("pockettts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "default",
            Name = "Pocket TTS",
            Description = "100M parameter TTS. 8 built-in voices, voice cloning from audio files. ~6x real-time on CPU, ~200ms to first chunk.",
            SourceUrl = "https://github.com/kyutai-labs/pocket-tts",
            License = "MIT",
            EstimatedSize = "~200MB",
            EstimatedVram = "CPU (no GPU needed)"
        }
    ];

    #endregion
}
