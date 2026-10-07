using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>IndexTTS-2 provider (2.5 and 2.0) — emotion-controllable zero-shot voice cloning: a GPT-2
/// text-to-speech decoder generates semantic-codec codes, an S2Mel flow-matching DiT turns them into an 80-band
/// mel, vocoded by the stock BigVGAN-v2 22kHz. 2.5 conditions the GPT on a CAM++ speaker vector; 2.0 on a
/// Conformer+Perceiver over the w2v-bert feature, with its own codec and tokenizer. By default the speaker's own
/// clip sets the emotion; the "indextts2_tts_params" feature flag adds a free-text emotion description (classified
/// by the bundled QwenEmotion model) and its strength. The Engine also accepts an emotion-reference clip and an
/// explicit 8-weight vector; those aren't surfaced as UI params here yet.</summary>
public sealed class IndexTts2Provider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the IndexTTS-2.5 provider.</summary>
    public static IndexTts2Provider Instance { get; } = new();

    /// <summary>Builds and returns the IndexTTS-2.5 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("indextts2_tts")
        .WithName("IndexTTS-2")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("IndexTTS2")
        .WithModelClass("indextts2_tts", "IndexTTS-2")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("indextts2_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2_0", Name = "IndexTTS-2.0", Description = "Emotion-controllable zero-shot voice cloning, the original IndexTTS-2 release: a Conformer+Perceiver speaker conditioner and SentencePiece text front end on the GPT, a MaskGCT semantic codec with a second GPT pass into the S2Mel flow-matching DiT, and the stock 22.05 kHz BigVGAN-v2 vocoder. Also auto-downloads facebook/w2v-bert-2.0, funasr/campplus, nvidia/bigvgan_v2_22khz_80band_256x and amphion/MaskGCT's semantic codec.", SourceUrl = "https://huggingface.co/IndexTeam/IndexTTS-2", License = "Apache-2.0", EstimatedSize = "~7GB (incl. ~1.2GB w2v-bert-2.0 and ~1.2GB emotion classifier)", EstimatedVram = "~5GB", EngineConfig = new() { ["model_name"] = "IndexTeam/IndexTTS-2" } },
        new() { Id = "v2_5", Name = "IndexTTS-2.5", Description = "Emotion-controllable zero-shot voice cloning: CAM++ speaker conditioning, a GPT-2 text-to-speech decoder, an S2Mel flow-matching DiT, and the stock 22.05 kHz BigVGAN-v2 vocoder. Also auto-downloads facebook/w2v-bert-2.0, funasr/campplus and nvidia/bigvgan_v2_22khz_80band_256x.", SourceUrl = "https://huggingface.co/IndexTeam/IndexTTS-2.5", License = "Apache-2.0", EstimatedSize = "~7GB (incl. ~1.2GB w2v-bert-2.0 and ~1.2GB emotion classifier)", EstimatedVram = "~5GB", EngineConfig = new() { ["model_name"] = "IndexTeam/IndexTTS-2.5" } }
    ];

    #endregion
}
