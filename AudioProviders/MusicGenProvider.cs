using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Meta MusicGen provider — text-to-music with optional melody conditioning (AudioCraft).</summary>
public sealed class MusicGenProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the MusicGen provider.</summary>
    public static MusicGenProvider Instance { get; } = new();

    /// <summary>Builds and returns the MusicGen provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("musicgen_music")
        .WithName("MusicGen")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("MusicGen")
        .WithModelClass("musicgen_music", "MusicGen")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("musicgen_music_params")
        .AddFeatureFlag("audiocraft_sampling")
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    #region Models

    // MusicGen: small/medium load from a combined file (model.safetensors / pytorch_model.bin); large loads from its
    // AudioCraft single-file state_dict.bin + compression_state_dict.bin (its combined HF model is sharded).
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "small", Name = "MusicGen Small", Description = "300M params, fast mono generation", SourceUrl = "https://huggingface.co/facebook/musicgen-small", License = "CC-BY-NC-4.0", EstimatedSize = "~1.2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-small" } },
        new() { Id = "medium", Name = "MusicGen Medium", Description = "1.5B params, better mono quality", SourceUrl = "https://huggingface.co/facebook/musicgen-medium", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~6GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-medium" } },
        new() { Id = "large", Name = "MusicGen Large", Description = "3.3B params, best mono quality", SourceUrl = "https://huggingface.co/facebook/musicgen-large", License = "CC-BY-NC-4.0", EstimatedSize = "~7GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-large" } }
    ];

    #endregion
}
