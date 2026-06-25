using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Chatterbox TTS provider — high-quality voice synthesis with expressive controls.</summary>
public sealed class ChatterboxProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Chatterbox TTS provider.</summary>
    public static ChatterboxProvider Instance { get; } = new();

    /// <summary>Builds and returns the Chatterbox TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("chatterbox_tts")
        .WithName("Chatterbox TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Chatterbox")
        .WithModelClass("chatterbox_tts", "Chatterbox TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("chatterbox_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("chatterbox")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Chatterbox TTS", Description = "High-quality voice synthesis with expressive controls (Exaggeration, CFG Weight)", SourceUrl = "https://github.com/resemble-ai/chatterbox", License = "MIT", EstimatedSize = "~2GB", EstimatedVram = "~4GB" }
    ];

    #endregion
}
