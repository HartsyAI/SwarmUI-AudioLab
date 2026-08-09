using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Meta AudioGen provider — text-to-sound-effects generation (AudioCraft family).</summary>
public sealed class AudioGenProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the AudioGen provider.</summary>
    public static AudioGenProvider Instance { get; } = new();

    /// <summary>Builds and returns the AudioGen SFX provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("audiogen_sfx")
        .WithName("AudioGen SFX")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("AudioGen")
        .WithModelClass("audiogen_sfx", "AudioGen SFX")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("audiocraft_sampling")
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "medium", Name = "AudioGen Medium", Description = "Text-to-sound-effects, 1.5B params", SourceUrl = "https://huggingface.co/facebook/audiogen-medium", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "facebook/audiogen-medium" } }
    ];

    #endregion
}
