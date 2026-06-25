using System;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Piper (VITS) — CPU TTS at 22.05 kHz. Provider id <c>piper_tts</c>. The engine's
/// <c>PiperPipeline</c> is implemented, but driving it needs two pieces the extension/engine don't ship yet:
/// an <b>espeak-ng phonemizer</b> (text → espeak phonemes) and each voice's <b>phoneme_id_map</b> (phoneme →
/// id), plus a loader for Piper's <c>.onnx</c>/JSON voice format into the engine's tensor dict. Wired as a
/// dispatchable model that fails with a clear message until those land (see engine-work list).</summary>
public static class PiperModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "rhasspy/piper-voices",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][Piper] Not runnable yet: needs (1) an espeak-ng phonemizer, (2) the voice phoneme_id_map, "
            + "and (3) a Piper .onnx/JSON → tensor loader. The engine PiperPipeline (VitsConfig.PiperMedium) is ready; "
            + "these front-end/format pieces are the gap. See engine-work list."),
    };
}
