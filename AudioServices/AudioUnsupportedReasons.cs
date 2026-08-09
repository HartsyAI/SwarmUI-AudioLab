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
        ["realtimestt_stt"] =
            "RealtimeSTT needs a streaming/chunked transcription loop; the engine's Whisper currently exposes only "
            + "whole-clip transcription. Use the Whisper provider in the meantime.",
        ["aws_transcribe"] =
            "AWS Transcribe needs a real client: the batch API is asynchronous and requires the audio to be in S3 "
            + "first (StartTranscriptionJob → poll → fetch), and the streaming API needs an HTTP/2 event-stream "
            + "protocol. The previous single POST matched neither and could never have worked. Use Whisper, "
            + "Deepgram, or AssemblyAI instead.",
    };

    /// <summary>A full user-facing message for an unsupported local provider, naming the specific blocker when known.</summary>
    public static string Message(string providerId, string providerName)
        => _reasons.TryGetValue(providerId ?? "", out string reason)
            ? $"{providerName} isn't runnable in the C# engine yet: {reason}"
            : $"{providerName} is not yet supported by the in-process C# engine. Support is being added engine-side; "
                + "this provider will light up automatically once it lands.";
}
