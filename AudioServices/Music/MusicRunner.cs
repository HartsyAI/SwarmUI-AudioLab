using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Parsed inputs for one text-to-music request.</summary>
public sealed class MusicRequest
{
    public required string Prompt { get; init; }
    /// <summary>Genre/style tag (YuE); empty for MusicGen/AudioGen which fold it into the prompt.</summary>
    public string Genre { get; init; } = "";
    public double Duration { get; init; } = 10;
    public int Seed { get; init; }
}

/// <summary>A loaded music model reduced to: prompt → mono PCM at <see cref="SampleRate"/>.</summary>
public interface IMusicRunner : IDisposable
{
    int SampleRate { get; }

    float[] Synthesize(IBackend backend, MusicRequest request);
}

/// <summary>Wraps a synth delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class MusicRunner(int sampleRate, Func<IBackend, MusicRequest, float[]> synth, params IDisposable[] disposables) : IMusicRunner
{
    public int SampleRate => sampleRate;

    public float[] Synthesize(IBackend backend, MusicRequest request) => synth(backend, request);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
