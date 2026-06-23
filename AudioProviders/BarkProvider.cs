using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Bark TTS provider — text-to-audio model with speech, music, and sound effects.</summary>
public sealed class BarkProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Bark TTS provider.</summary>
    public static BarkProvider Instance { get; } = new();

    /// <summary>Builds and returns the Bark TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("bark_tts")
        .WithName("Bark TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Bark")
        .WithModelClass("bark_tts", "Bark TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("bark_tts_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Bark TTS", Description = "Text-to-audio generation with speech, music, and sound effects", SourceUrl = "https://huggingface.co/suno/bark", License = "MIT", EstimatedSize = "~5GB", EstimatedVram = "~5GB" }
    ];

    #endregion
}
