using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Zonos TTS provider -- multilingual TTS trained on 200k+ hours with zero-shot cloning.</summary>
public sealed class ZonosProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Zonos provider.</summary>
    public static ZonosProvider Instance { get; } = new();

    /// <summary>Builds and returns the Zonos provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("zonos_tts")
        .WithName("Zonos TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Zonos")
        .WithModelClass("zonos_tts", "Zonos TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("zonos_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "transformer", Name = "Zonos Transformer", Description = "Transformer-based, multilingual (EN/JP/CN/FR/DE)", SourceUrl = "https://huggingface.co/Zyphra/Zonos-v0.1-transformer", License = "Apache 2.0", EstimatedSize = "~2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "Zyphra/Zonos-v0.1-transformer" } },
        new() { Id = "hybrid", Name = "Zonos Hybrid", Description = "Hybrid architecture, best quality with zero-shot cloning", SourceUrl = "https://huggingface.co/Zyphra/Zonos-v0.1-hybrid", License = "Apache 2.0", EstimatedSize = "~2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "Zyphra/Zonos-v0.1-hybrid" } }
    ];

    #endregion
}
