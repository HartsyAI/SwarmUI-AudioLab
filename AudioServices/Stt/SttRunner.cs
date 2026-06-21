using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>A loaded STT model reduced to: transcribe a mono 16 kHz waveform to text.</summary>
public interface ISttRunner : IDisposable
{
    string Transcribe(IBackend backend, float[] audioMono16k);
}

/// <summary>Wraps any pipeline + a transcribe delegate, so no per-model runner class is needed.</summary>
public sealed class SttRunner(IDisposable pipeline, Func<IBackend, float[], string> transcribe) : ISttRunner
{
    public string Transcribe(IBackend backend, float[] audioMono16k) => transcribe(backend, audioMono16k);

    public void Dispose() => pipeline.Dispose();
}
