using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Orpheus TTS provider — LLM-based emotional speech synthesis with emotion tags.</summary>
public sealed class OrpheusTTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Orpheus TTS provider.</summary>
    public static OrpheusTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Orpheus TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("orpheus_tts")
        .WithName("Orpheus TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Orpheus")
        .WithModelClass("orpheus_tts", "Orpheus TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("orpheus_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    // Only the 3B model has been released by Canopy Labs. Smaller variants (1B, 400M, 150M) are
    // on their roadmap but not yet available on HuggingFace. Add them back when released.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "3b", Name = "Orpheus 3B", Description = "Expressive speech with emotion tags: <laugh>, <sigh>, <gasp>, etc.", SourceUrl = "https://huggingface.co/canopylabs/orpheus-3b-0.1-ft", License = "Apache 2.0", EstimatedSize = "~6GB", EstimatedVram = "~16GB", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-3b-0.1-ft", ["model_size"] = "3b" } }
    ];

    #endregion
}
