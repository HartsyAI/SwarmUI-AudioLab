using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Fish Audio S2 Pro TTS provider -- dual-autoregressive 4B model over a 10-codebook codec, inline prosody tags and voice cloning from a reference clip.</summary>
public sealed class FishAudioS2Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Fish Audio S2 provider.</summary>
    public static FishAudioS2Provider Instance { get; } = new();

    /// <summary>Builds and returns the Fish Audio S2 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fishaudio_tts")
        .WithName("Fish Audio S2 TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("FishAudioS2")
        .WithModelClass("fishaudio_tts", "Fish Audio S2 TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "s2-pro",
            Name = "Fish Audio S2 Pro",
            Description = "Dual-AR (36-layer slow + 4-layer fast) over a 10-codebook modified-DAC codec at 44.1 kHz. Voice cloning from a reference clip plus its transcript.",
            SourceUrl = "https://huggingface.co/fishaudio/s2-pro",
            License = "Fish Audio Research License (non-commercial)",
            EstimatedSize = "~11GB",
            EstimatedVram = "~12GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/s2-pro" }
        }
    ];

    #endregion
}
