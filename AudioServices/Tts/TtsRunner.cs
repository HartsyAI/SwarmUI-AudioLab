using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Parsed inputs for one TTS request. The handler materializes the optional voice reference as both
/// samples and a temp WAV path so a descriptor uses whichever its pipeline wants.</summary>
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

    /// <summary>Sampling seed (from SwarmUI's Seed param) for reproducibility; 0 if unset. Passed to pipelines
    /// whose <c>Synthesize</c> accepts a seed.</summary>
    public int Seed { get; init; }

    /// <summary>Raw base64 of the voice reference (whatever the user uploaded), or null. Lets a descriptor
    /// decode the reference at whatever sample rate its codec needs (e.g. NeuTTS encodes at 16 kHz).</summary>
    public string ReferenceB64 { get; init; }

    // --- Optional per-model knobs (populated from the UI params; honored only by pipelines that accept them). ---

    /// <summary>Built-in voice name (Kokoro voice pack, etc.); null/empty = the model's default.</summary>
    public string Voice { get; init; }

    /// <summary>Speaking-rate multiplier (Kokoro, F5); null = the model default.</summary>
    public double? Speed { get; init; }

    /// <summary>Chatterbox expressiveness; null = the config default.</summary>
    public double? Exaggeration { get; init; }

    /// <summary>F5 flow-matching steps (NFE); null = the model default.</summary>
    public int? NfeStep { get; init; }

    /// <summary>Classifier-free guidance strength (F5 CfgStrength, etc.); null = the model default.</summary>
    public double? CfgScale { get; init; }
}

/// <summary>A loaded TTS model reduced to: text (+ optional voice reference) → mono PCM at <see cref="SampleRate"/>.</summary>
public interface ITtsRunner : IDisposable
{
    int SampleRate { get; }

    float[] Synthesize(IBackend backend, TtsRequest request);
}

/// <summary>Wraps a synth delegate + the disposables a loaded model owns (pipeline, and any weight loaders
/// the model tensors still reference), so no per-model runner class is needed.</summary>
public sealed class TtsRunner(int sampleRate, Func<IBackend, TtsRequest, float[]> synth, params IDisposable[] disposables) : ITtsRunner
{
    public int SampleRate => sampleRate;

    public float[] Synthesize(IBackend backend, TtsRequest request) => synth(backend, request);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
