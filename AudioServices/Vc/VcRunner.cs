using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>A loaded voice-conversion model reduced to: source audio (+ optional target voice) → re-voiced
/// PCM at <see cref="SampleRate"/>. Both inputs are decoded to the descriptor's input sample rate.</summary>
public interface IVcRunner : IDisposable
{
    int SampleRate { get; }

    /// <summary>Re-voices <paramref name="sourceMono"/>. <paramref name="targetMono"/> is the target voice for
    /// models that condition on one (e.g. OpenVoice tone-color transfer); null/empty for source-only models
    /// (e.g. RVC, which carries the target voice in its trained weights).</summary>
    float[] Convert(IBackend backend, float[] sourceMono, float[] targetMono);
}

/// <summary>Wraps a convert delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class VcRunner(int sampleRate, Func<IBackend, float[], float[], float[]> convert, params IDisposable[] disposables) : IVcRunner
{
    public int SampleRate => sampleRate;

    public float[] Convert(IBackend backend, float[] sourceMono, float[] targetMono) => convert(backend, sourceMono, targetMono);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
