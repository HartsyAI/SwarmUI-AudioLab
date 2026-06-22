using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>A loaded STT model reduced to: transcribe a mono 16 kHz waveform to text.</summary>
public interface ISttRunner : IDisposable
{
    string Transcribe(IBackend backend, float[] audioMono16k);
}

/// <summary>Wraps a transcribe delegate + the disposables a loaded model owns (pipeline, weight loaders),
/// so no per-model runner class is needed.</summary>
public sealed class SttRunner(Func<IBackend, float[], string> transcribe, params IDisposable[] disposables) : ISttRunner
{
    public string Transcribe(IBackend backend, float[] audioMono16k) => transcribe(backend, audioMono16k);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
