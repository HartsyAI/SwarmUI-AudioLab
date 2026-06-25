namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Human-readable reasons a local provider isn't yet runnable on the in-process C# engine. The engine
/// implements pipelines for nearly every AudioLab model; what's missing for these is a specific prerequisite
/// (a tokenizer asset, a phonemizer, a checkpoint loader, or confirmed weight layout). Surfacing the exact
/// reason beats a generic "not supported yet" so the user knows what actually has to land.</summary>
public static class AudioUnsupportedReasons
{
    /// <summary>Provider-id → why it can't run in-process yet. Absent ⇒ generic fallback.</summary>
    private static readonly Dictionary<string, string> _reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chatterbox_tts"] =
            "the C# engine has the Chatterbox pipeline but not its 704-token English text tokenizer (that tokenizer "
            + "asset is the only missing piece).",
        ["piper_tts"] =
            "Piper needs an espeak-ng phonemizer and each voice's phoneme map; the engine ships no espeak phonemizer "
            + "(its built-in G2P is CMU-dictionary based and uses different phoneme ids).",
        ["zonos_tts"] =
            "Zonos needs espeak-ng phonemes to build its conditioning prefix; the engine ships no espeak phonemizer.",
        ["gptsovits_clone"] =
            "GPT-SoVITS is compositional — it needs a HuBERT SSL encoder, a Chinese RoBERTa BERT, and a reference-"
            + "spectrogram front-end wired together; that wiring isn't done yet.",
        ["realtimestt_stt"] =
            "RealtimeSTT needs a streaming/chunked transcription loop; the engine's Whisper currently exposes only "
            + "whole-clip transcription. Use the Whisper provider in the meantime.",
        ["resemble_enhance_fx"] =
            "Resemble Enhance publishes only a DeepSpeed .pt checkpoint and the engine's DeepSpeed .pt loader isn't "
            + "implemented yet, so its weights can't be loaded.",
        ["qwen3_tts"] =
            "Qwen3-TTS is built engine-side, but its multi-file checkpoint layout and per-frame text timing still "
            + "need confirming before it can be wired in.",
        ["pockettts_tts"] =
            "Pocket TTS is built engine-side; wiring waits on confirming the gated checkpoint layout and its built-in "
            + "voice-priming embeddings.",
        ["kyutaitts_tts"] =
            "Kyutai TTS is built engine-side; wiring waits on confirming the checkpoint layout and the per-frame text "
            + "(word/pad) timing.",
    };

    /// <summary>A full user-facing message for an unsupported local provider, naming the specific blocker when known.</summary>
    public static string Message(string providerId, string providerName)
        => _reasons.TryGetValue(providerId ?? "", out string reason)
            ? $"{providerName} isn't runnable in the C# engine yet: {reason}"
            : $"{providerName} is not yet supported by the in-process C# engine. Support is being added engine-side; "
                + "this provider will light up automatically once it lands.";
}
