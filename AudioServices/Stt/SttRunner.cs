using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>A loaded speech-to-text model, reduced to the one operation the generic <see cref="SttHandler"/>
/// needs: transcribe a mono 16 kHz waveform to text. Adapting a concrete engine pipeline (Whisper,
/// Moonshine, …) to this is a one-liner, so adding an STT model is a descriptor, not a new class.</summary>
public interface ISttRunner : IDisposable
{
    string Transcribe(IBackend backend, float[] audioMono16k);
}

/// <summary>Generic runner that wraps any disposable pipeline plus a transcribe delegate — so each STT
/// model family needs no bespoke runner class, just the delegate built in its <see cref="SttModelDescriptor"/>.</summary>
public sealed class SttRunner(IDisposable pipeline, Func<IBackend, float[], string> transcribe) : ISttRunner
{
    public string Transcribe(IBackend backend, float[] audioMono16k) => transcribe(backend, audioMono16k);

    public void Dispose() => pipeline.Dispose();
}
