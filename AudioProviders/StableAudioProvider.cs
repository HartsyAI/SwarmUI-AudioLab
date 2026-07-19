using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Stable Audio Open Small provider — Stability AI's fast (8-step, no CFG) text-to-audio/SFX
/// rectified-flow DiT. Weights auto-download from HuggingFace on first generation.</summary>
public sealed class StableAudioProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Stable Audio provider.</summary>
    public static StableAudioProvider Instance { get; } = new();

    /// <summary>Builds and returns the Stable Audio Open Small provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("stableaudio_music")
        .WithName("Stable Audio Open Small")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("StableAudio")
        .WithModelClass("stableaudio_music", "Stable Audio Open Small")
        .AddFeatureFlag("audiolab_audiogen")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private const string RepoUrl = "https://huggingface.co/stabilityai/stable-audio-open-small";

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "open-small", Name = "Stable Audio Open Small", Description = "341M rectified-flow DiT, 8-step pingpong sampling, no CFG. Text-to-audio/SFX up to ~11.89s.", SourceUrl = RepoUrl, License = "Stability AI Community License", EstimatedSize = "~1.9GB", EstimatedVram = "~3GB" },
    ];

    #endregion
}
