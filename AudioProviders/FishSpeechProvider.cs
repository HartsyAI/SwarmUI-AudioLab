using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Fish Speech TTS provider -- dual-autoregressive TTS with inline prosody control supporting 80+ languages.</summary>
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
        // No tts_voice_ref: the engine does not port the Firefly encoder, so Fish Speech cannot clone a voice.
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        // The in-process C# engine implements Fish-Speech 1.5 and 1.4 (DualAR + firefly-gan-vq, 44.1 kHz). The newer
        // S1/S2 checkpoints use a different architecture the engine does not load yet, so advertise only these two.
        new()
        {
            Id = "fish-speech-1.5",
            Name = "Fish Speech 1.5",
            Description = "DualAR text-to-semantic (Llama-style backbone + depth transformer over 8 codebooks) decoded by firefly-gan-vq to 44.1 kHz. Multilingual, in the model's own voice (no voice cloning yet). ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/fishaudio/fish-speech-1.5",
            License = "CC-BY-NC-SA-4.0",
            EstimatedSize = "~2GB",
            EstimatedVram = "~4GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/fish-speech-1.5" }
        },
        // Reaches the engine as fishspeech:fish-speech-1.4, which selects the 1.4 layout (HF tokenizer, one semantic sentinel).
        new()
        {
            Id = "fish-speech-1.4",
            Name = "Fish Speech 1.4",
            Description = "The earlier release: the same DualAR text-to-semantic over 8 codebooks with an HF tokenizer, decoded by firefly-gan-vq to 44.1 kHz. Multilingual, in the model's own voice (no voice cloning yet). Unverified: not yet run on the real weights. ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/fishaudio/fish-speech-1.4",
            License = "CC-BY-NC-SA-4.0",
            EstimatedSize = "~1.2GB",
            EstimatedVram = "~4GB",
            EngineConfig = new() { ["model_name"] = "fishaudio/fish-speech-1.4" }
        }
    ];

    #endregion
}
