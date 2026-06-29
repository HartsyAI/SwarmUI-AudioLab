using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>Optional per-request voice-conversion knobs (honored by the models that support them; e.g. RVC pitch).</summary>
public sealed class VcRequest
{
    /// <summary>Pitch shift in semitones (RVC); 0 = no shift.</summary>
    public double PitchShift { get; init; }
}

/// <summary>A loaded voice-conversion model reduced to: source audio (+ optional target voice) → re-voiced
/// PCM at <see cref="SampleRate"/>. Both inputs are decoded to the descriptor's input sample rate.</summary>
public interface IVcRunner : IDisposable
{
    int SampleRate { get; }

    /// <summary>Re-voices <paramref name="sourceMono"/>. <paramref name="targetMono"/> is the target voice for
    /// models that condition on one (e.g. OpenVoice tone-color transfer); null/empty for source-only models
    /// (e.g. RVC, which carries the target voice in its trained weights).</summary>
    float[] Convert(IBackend backend, float[] sourceMono, float[] targetMono, VcRequest request);
}

/// <summary>Wraps a convert delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class VcRunner(int sampleRate, Func<IBackend, float[], float[], VcRequest, float[]> convert, params IDisposable[] disposables) : IVcRunner
{
    public int SampleRate => sampleRate;

    public float[] Convert(IBackend backend, float[] sourceMono, float[] targetMono, VcRequest request) => convert(backend, sourceMono, targetMono, request);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
