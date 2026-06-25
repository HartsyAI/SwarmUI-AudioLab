using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Zonos v0.1 (Zyphra/Zonos-v0.1-transformer) — transformer backbone over 9 DAC codebooks → 44.1 kHz
/// TTS with voice cloning. Provider id <c>zonos_tts</c> (the provider is categorized TTS). The engine's
/// <c>ZonosPipeline</c> + DAC decode are implemented, but <c>Generate</c> takes a precomputed conditioning prefix
/// <c>[1, P, hidden]</c> (espeak phonemes + speaker embedding + emotion/pitch/rate/language controls via the
/// Fourier/Integer conditioners) plus its unconditional counterpart — that prefix construction is entirely
/// caller-side and not yet built. Gated until the conditioning front-end lands (engine-work list).</summary>
public static class ZonosModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "Zyphra/Zonos-v0.1-transformer",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][Zonos] Not runnable yet: ZonosPipeline.Generate needs a precomputed conditioning prefix "
            + "[1,P,hidden] (espeak phonemes + speaker embedding + emotion/pitch/rate/language controls) and its "
            + "unconditional counterpart. The pipeline + DAC are ready; the conditioning front-end is the gap. "
            + "See engine-work list."),
    };
}
