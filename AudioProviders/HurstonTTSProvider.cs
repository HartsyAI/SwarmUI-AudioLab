using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Hurston-1 TTS provider — accented English speech steered by a plain-language voice description.</summary>
public sealed class HurstonTTSProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Hurston-1 TTS provider.</summary>
    public static HurstonTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Hurston-1 TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("hurston_tts")
        .WithName("Hurston-1 TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Hurston")
        .WithModelClass("hurston_tts", "Hurston-1 TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("hurston_tts_params")
        .AddFeatureFlag("tts_streaming")
        .AddModels(Models)
        .WithFileBackedModels()
        .WithEngineGroup("main")
        .Build();

    #region Models

    // Hurston-1 is a Maya1 LoRA fine-tune on the Orpheus stack. Its weights are gated upstream; the engine loads the
    // single-file repack, so EngineConfig names that repo.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Hurston-1", Description = "Accented English speech (Southern American, Nigerian, West and East African) from a voice description. Phone mode and streaming supported.", SourceUrl = "https://huggingface.co/destined/hurston-1", License = "Apache 2.0", EstimatedSize = "~6.6GB", EstimatedVram = "~8GB", EngineConfig = new() { ["model_name"] = "destined/hurston-1", ["model_size"] = "default" } }
    ];

    #endregion
}
