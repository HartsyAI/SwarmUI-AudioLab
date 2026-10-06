using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>IndexTTS-2.5 provider — emotion-controllable zero-shot voice cloning: a GPT-2 text-to-speech
/// decoder with CAM++ speaker conditioning generates semantic-codec codes, an S2Mel flow-matching DiT turns
/// them into an 80-band mel, vocoded by the stock BigVGAN-v2 22kHz. Only zero-shot cloning with the
/// speaker's own default emotion is wired in the Engine today — the explicit emotion-vector/audio-reference/
/// free-text (QwenEmotion) modes the Engine's pipeline already supports aren't yet exposed here, so no
/// instruction/emotion feature flag is added (unlike <see cref="AukProvider"/>'s "auk_tts_params"). IndexTTS-2.0
/// is not wired in the Engine either, so only the 2.5 model is listed.</summary>
public sealed class IndexTts2Provider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the IndexTTS-2.5 provider.</summary>
    public static IndexTts2Provider Instance { get; } = new();

    /// <summary>Builds and returns the IndexTTS-2.5 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("indextts2_tts")
        .WithName("IndexTTS-2.5")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("IndexTTS2")
        .WithModelClass("indextts2_tts", "IndexTTS-2.5")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2_5", Name = "IndexTTS-2.5", Description = "Emotion-controllable zero-shot voice cloning (speaker's own default emotion for now): CAM++ speaker conditioning, a GPT-2 text-to-speech decoder, an S2Mel flow-matching DiT, and the stock 22.05 kHz BigVGAN-v2 vocoder. Also auto-downloads facebook/w2v-bert-2.0, funasr/campplus and nvidia/bigvgan_v2_22khz_80band_256x.", SourceUrl = "https://huggingface.co/IndexTeam/IndexTTS-2.5", License = "Apache-2.0", EstimatedSize = "~6GB (incl. ~1.2GB w2v-bert-2.0)", EstimatedVram = "~5GB", EngineConfig = new() { ["model_name"] = "IndexTeam/IndexTTS-2.5" } }
    ];

    #endregion
}
