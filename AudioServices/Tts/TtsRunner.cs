using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Parsed inputs for one text-to-speech request, shared across all TTS models. The handler
/// materializes the optional voice reference both as decoded samples and as a temp WAV path, so a model
/// descriptor can use whichever form its pipeline wants without re-decoding.</summary>
public sealed class TtsRequest
{
    /// <summary>The text to speak.</summary>
    public required string Text { get; init; }

    /// <summary>Transcript of the reference clip, for models that use it (e.g. F5); else empty.</summary>
    public string RefText { get; init; } = "";

    /// <summary>Voice-reference samples (mono 24 kHz), or null when no reference was supplied.</summary>
    public float[] ReferenceMono24k { get; init; }

    /// <summary>Temp 24 kHz mono WAV of the reference (for models that take a file path, e.g. VibeVoice),
    /// or null when no reference was supplied. Owned and cleaned up by the handler.</summary>
    public string ReferenceWavPath { get; init; }
}

/// <summary>A loaded TTS model reduced to the one op the generic <see cref="TtsHandler"/> needs:
/// text (+ optional voice reference) → mono PCM at <see cref="SampleRate"/>.</summary>
public interface ITtsRunner : IDisposable
{
    int SampleRate { get; }

    float[] Synthesize(IBackend backend, TtsRequest request);
}

/// <summary>Generic runner that wraps any disposable pipeline + a synth delegate — so each TTS model is a
/// descriptor, not a bespoke runner class.</summary>
public sealed class TtsRunner(IDisposable pipeline, int sampleRate, Func<IBackend, TtsRequest, float[]> synth) : ITtsRunner
{
    public int SampleRate => sampleRate;

    public float[] Synthesize(IBackend backend, TtsRequest request) => synth(backend, request);

    public void Dispose() => pipeline.Dispose();
}
