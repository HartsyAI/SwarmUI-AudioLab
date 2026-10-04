using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>IndexTTS-1.5 provider — zero-shot voice cloning via a GPT-2 text-to-speech decoder with
/// Conformer-Perceiver speech conditioning, vocoded by a custom 24 kHz BigVGAN-v2. No instruction/emotion or
/// duration control (that's IndexTTS-2, not yet in the engine).</summary>
public sealed class IndexTtsProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the IndexTTS provider.</summary>
    public static IndexTtsProvider Instance { get; } = new();

    /// <summary>Builds and returns the IndexTTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("indextts_tts")
        .WithName("IndexTTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("IndexTTS")
        .WithModelClass("indextts_tts", "IndexTTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v1_5", Name = "IndexTTS-1.5", Description = "Zero-shot voice cloning from a reference clip: Conformer-Perceiver speech conditioning, a biased GPT-2 text-to-speech decoder, and a custom 24 kHz BigVGAN-v2 vocoder with an embedded ECAPA-TDNN speaker d-vector.", SourceUrl = "https://huggingface.co/IndexTeam/IndexTTS-1.5", License = "Apache-2.0", EstimatedSize = "~3.5GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "IndexTeam/IndexTTS-1.5" } }
    ];

    #endregion
}
