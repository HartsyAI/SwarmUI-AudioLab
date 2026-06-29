using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>Decode options for one transcription request. Honored by Whisper (language + translate task);
/// ignored by models whose architecture has no language/task tokens (Moonshine, Kyutai STT).</summary>
public sealed class SttRequest
{
    /// <summary>Source language hint (ISO code, e.g. "en"); null/empty lets the model auto-detect.</summary>
    public string Language { get; init; } = "en";

    /// <summary>True to translate speech to English instead of transcribing in the source language.</summary>
    public bool Translate { get; init; }
}

/// <summary>A loaded STT model reduced to: transcribe a mono 16 kHz waveform to text.</summary>
public interface ISttRunner : IDisposable
{
    string Transcribe(IBackend backend, float[] audioMono16k, SttRequest request);
}

/// <summary>Wraps a transcribe delegate + the disposables a loaded model owns (pipeline, weight loaders),
/// so no per-model runner class is needed.</summary>
public sealed class SttRunner(Func<IBackend, float[], SttRequest, string> transcribe, params IDisposable[] disposables) : ISttRunner
{
    public string Transcribe(IBackend backend, float[] audioMono16k, SttRequest request) => transcribe(backend, audioMono16k, request);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
