using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Haru-4B: a full-duplex speech-to-speech model. It listens and speaks at once, so it takes a recording of the user (or, over the live route, the
/// user's microphone) instead of text to read. The Prompt box holds the character card. A pretrained base model: it holds a natural conversation but
/// does not follow instructions, call tools or transcribe the user. Only Q8_0 (and native BF16) keep the conversation; the lower-bit variants are unintelligible,
/// so only <c>q8</c> is offered. The model id is the engine's variant token, so it reaches the engine as <c>haru:q8</c>.</summary>
public sealed class HaruProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Haru provider.</summary>
    public static HaruProvider Instance { get; } = new();

    /// <summary>The provider id: AudioLab's own, not the engine's (<c>haru</c>).</summary>
    public const string ProviderId = "haru_s2s";

    /// <summary>Builds and returns the Haru provider definition. No shared sampling flag: its defaults (temperature 0.7, top-k 25) are the model's own.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId(ProviderId)
        .WithName("Haru Speech-to-Speech")
        .WithCategory(AudioCategory.SpeechToSpeech)
        .WithModelPrefix("Haru")
        .WithModelClass(ProviderId, "Haru Speech-to-Speech")
        .AddFeatureFlag("tts_voice_ref")
        .AddFeatureFlag("haru_s2s_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "q8",
            Name = "Haru-4B (Q8)",
            Description = "A full-duplex speech-to-speech model: it listens and answers at once, one 133 ms frame at a time. Qwen3.5-4B with the language model's matrices in Q8_0, converted once at install (a minute or several). "
                + "A base model: it holds a natural conversation but does not follow instructions, call tools or transcribe you. Use headphones live. The voice reference must be a voice you have consent to use.",
            SourceUrl = "https://huggingface.co/higurashi-ai/Haru-4B",
            License = "Apache-2.0 (weights and code); the VibeVoice acoustic codec is MIT and forbids voice impersonation without consent",
            EstimatedSize = "~10.5GB",
            EstimatedVram = "~8GB",
        },
    ];

    #endregion
}
