using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Pocket-TTS (kyutai/pocket-tts) — flow-LM over continuous Mimi latents, 24 kHz, built-in voices +
/// zero-shot cloning. Provider id <c>pockettts_tts</c>. The engine's <c>PocketTtsPipeline</c> is implemented, but
/// <c>PocketTtsConfig.Default</c> is config-gated: core dims (<c>DModel</c>, <c>LatentDim</c>, <c>NumHeads</c>,
/// <c>FfnDim</c>, <c>VocabSize</c>) are placeholder zeros pending reconciliation from the released checkpoint,
/// so it can't load yet. Also needs the SentencePiece text tokenizer asset. Gated at load (engine-work list).</summary>
public static class PocketTtsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "kyutai/pocket-tts",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][Pocket-TTS] Not runnable yet: PocketTtsConfig has placeholder (zero) dims that reconcile from the "
            + "checkpoint, and the SentencePiece tokenizer asset isn't wired. The engine pipeline is ready once its config "
            + "is reconciled against the released weights. See engine-work list."),
    };
}
