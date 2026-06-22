using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>A loaded voice-conversion model reduced to: source mono 16 kHz audio → re-voiced PCM at
/// <see cref="SampleRate"/>.</summary>
public interface IVcRunner : IDisposable
{
    int SampleRate { get; }

    float[] Convert(IBackend backend, float[] sourceMono16k);
}

/// <summary>Wraps a convert delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class VcRunner(int sampleRate, Func<IBackend, float[], float[]> convert, params IDisposable[] disposables) : IVcRunner
{
    public int SampleRate => sampleRate;

    public float[] Convert(IBackend backend, float[] sourceMono16k) => convert(backend, sourceMono16k);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
