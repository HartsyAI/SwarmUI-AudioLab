using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Fish Speech TTS provider -- dual-autoregressive TTS with inline prosody control supporting 80+ languages and voice cloning.</summary>
public sealed class FishSpeechProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Fish Speech provider.</summary>
    public static FishSpeechProvider Instance { get; } = new();

    /// <summary>Builds and returns the Fish Speech provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fishspeech_tts")
        .WithName("Fish Speech TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("FishSpeech")
        .WithModelClass("fishspeech_tts", "Fish Speech TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("fishspeech_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        // The in-process C# engine implements Fish-Speech 1.5 (DualAR + firefly-gan-vq, 44.1 kHz). The newer
        // S1/S2 checkpoints use a different architecture the engine does not load yet — advertise only 1.5.
        new()
        {
            Id = "fish-speech-1.5",
            Name = "Fish Speech 1.5",
            Description = "DualAR text-to-semantic (Llama-style backbone + depth transformer over 8 codebooks) decoded by firefly-gan-vq to 44.1 kHz. Multilingual, voice cloning. ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/fishaudio/fish-speech-1.5",
            License = "CC-BY-NC-SA-4.0",
            EstimatedSize = "~2GB",
            EstimatedVram = "~4GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/fish-speech-1.5" }
        }
    ];

    #endregion
}
