using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Dia TTS provider — ultra-realistic dialogue generation with nonverbal sounds.</summary>
public sealed class DiaTTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Dia TTS provider.</summary>
    public static DiaTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Dia TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("dia_tts")
        .WithName("Dia TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Dia")
        .WithModelClass("dia_tts", "Dia TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("dia_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.6b", Name = "Dia 1.6B", Description = "Dialogue model, not a single-sentence narrator: start with [S1] and alternate [S1]/[S2] for each speaker turn, and give it roughly 5-20 seconds of speech worth of text. Very short, single-sentence prompts often come back as non-speech rather than silence -- that's upstream Dia's own behavior, not an error. Also does nonverbal sounds (laughs, coughs) inline.", SourceUrl = "https://huggingface.co/nari-labs/Dia-1.6B-0626", License = "Apache 2.0", EstimatedSize = "~6.4GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "nari-labs/Dia-1.6B-0626" } }
    ];

    #endregion
}
