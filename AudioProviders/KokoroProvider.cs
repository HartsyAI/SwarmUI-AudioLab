using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Kokoro TTS provider — ultra-fast lightweight TTS (82M params, CPU-capable).</summary>
public sealed class KokoroProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Kokoro TTS provider.</summary>
    public static KokoroProvider Instance { get; } = new();

    /// <summary>Builds and returns the Kokoro TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("kokoro_tts")
        .WithName("Kokoro TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Kokoro")
        .WithModelClass("kokoro_tts", "Kokoro TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("kokoro_tts_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Kokoro Default", Description = "82M param model, 96x real-time on GPU, CPU-capable", SourceUrl = "https://huggingface.co/hexgrad/Kokoro-82M", License = "Apache 2.0", EstimatedSize = "~200MB", EstimatedVram = "~1GB (or CPU)", SelfManaged = true }
    ];

    #endregion
}
