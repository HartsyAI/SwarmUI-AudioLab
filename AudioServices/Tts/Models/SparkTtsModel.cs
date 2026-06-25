using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Spark-TTS-0.5B (SparkAudio/Spark-TTS-0.5B) — Qwen2.5-0.5B LM + BiCodec decoder → 16 kHz. Provider id
/// <c>sparktts_tts</c>. The engine's <c>SparkTtsPipeline</c> + <c>Qwen2Tokenizer</c> exist, but two engine items
/// are checkpoint-reconciliation-pending: the audio/control token OFFSETS (from the checkpoint's
/// <c>added_tokens.json</c>) that the chat-templated prompt needs, and the exact BiCodec decoder key names /
/// Vocos-ConvNeXt prenet. Wired as dispatchable; fails clearly until the engine reconciles those (engine-work list).</summary>
public static class SparkTtsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "SparkAudio/Spark-TTS-0.5B",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][Spark-TTS] Not runnable yet: the engine's SparkTtsConfig token offsets + BiCodec decoder keys "
            + "are checkpoint-reconciliation-pending, so the prompt control tokens and codec decode aren't weight-valid. "
            + "Wire-ready once the engine reconciles against the 0.5B checkpoint. See engine-work list."),
    };
}
