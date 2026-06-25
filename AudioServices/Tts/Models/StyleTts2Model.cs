using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>StyleTTS 2 (yl4579/StyleTTS2-LibriTTS) — diffusion-style TTS reusing the Kokoro modules, 24 kHz.
/// Provider id <c>styletts2_tts</c>. Text → IPA is available via the engine's <c>EnglishG2P</c> (as Kokoro uses),
/// and all three synth modes (Clone/Random/ClonePerturbed) are engine-complete. The gap is construction: the
/// pipeline has NO single <c>LoadWeights</c> — the caller must load weights into each Kokoro submodule (PLBERT,
/// text encoder, prosody predictor, iSTFTNet decoder, StyleEncoder, StyleDenoiser) from the LibriTTS checkpoint,
/// and there is no engine test/recipe for that key mapping. Gated until the engine adds a load helper or the
/// submodule key map is verified (engine-work list).</summary>
public static class StyleTts2Model
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "yl4579/StyleTTS2-LibriTTS",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][StyleTTS2] Not runnable yet: StyleTts2Pipeline has no unified LoadWeights — its Kokoro submodules "
            + "(PLBERT/text-encoder/prosody/decoder/StyleEncoder/StyleDenoiser) need a verified per-key load from the "
            + "LibriTTS checkpoint, which has no engine recipe. Add a StyleTts2Pipeline.LoadFromCheckpoint helper. "
            + "See engine-work list."),
    };
}
