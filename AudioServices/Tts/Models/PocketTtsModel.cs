using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Pocket-TTS (kyutai/pocket-tts) — flow-LM over continuous Mimi latents, 24 kHz, built-in voices +
/// zero-shot cloning. Provider id <c>pockettts_tts</c>. The engine's <c>PocketTtsPipeline</c> is implemented, but
/// <c>PocketTtsConfig.Default</c> is config-gated: core dims (<c>DModel</c>, <c>LatentDim</c>, <c>NumHeads</c>,
/// <c>FfnDim</c>, <c>VocabSize</c>) are placeholder zeros pending reconciliation from the released checkpoint,
/// so it can't load yet. Also needs the SentencePiece text tokenizer asset. Gated at load (engine-work list).
///
/// <para><b>Weights repo:</b> the upstream <c>kyutai/pocket-tts</c> is HF-gated (auto), which blocks a token-less
/// download. Use the non-gated repack <c>Verylicious/pocket-tts-ungated</c> — same original layout
/// (<c>tts_b6369a24.safetensors</c> + <c>tokenizer.model</c> SPM + voice embeddings). Alternatively
/// <c>smdesai/pocket-tts</c> additionally ships <c>config.json</c>, which supplies the real dims to reconcile the
/// placeholder <c>PocketTtsConfig</c> above.</para></summary>
public static class PocketTtsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        // Non-gated repack of the (auto-gated) kyutai/pocket-tts — identical checkpoint (hash b6369a24).
        ResolveRepo = _ => "Verylicious/pocket-tts-ungated",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][Pocket-TTS] Not runnable yet: PocketTtsConfig has placeholder (zero) dims that reconcile from the "
            + "checkpoint, and the SentencePiece tokenizer asset isn't wired. The engine pipeline is ready once its config "
            + "is reconciled against the released weights. See engine-work list."),
    };
}
