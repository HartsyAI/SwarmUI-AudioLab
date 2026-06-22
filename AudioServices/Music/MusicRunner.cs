using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Parsed inputs for one text-to-music request.</summary>
public sealed class MusicRequest
{
    public required string Prompt { get; init; }
    /// <summary>Genre/style tag (ACE-Step style prompt, YuE genre); empty for MusicGen/AudioGen.</summary>
    public string Genre { get; init; } = "";
    public double Duration { get; init; } = 10;
    public int Seed { get; init; }
}

/// <summary>Generated audio — mono (Right null) or stereo.</summary>
public readonly struct MusicAudio
{
    public float[] Left { get; init; }
    public float[] Right { get; init; }

    public static MusicAudio Mono(float[] samples) => new() { Left = samples, Right = null };
    public static MusicAudio Stereo(float[] left, float[] right) => new() { Left = left, Right = right };
}

/// <summary>A loaded music model reduced to: prompt → PCM at <see cref="SampleRate"/>.</summary>
public interface IMusicRunner : IDisposable
{
    int SampleRate { get; }

    MusicAudio Synthesize(IBackend backend, MusicRequest request);
}

/// <summary>Wraps a synth delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class MusicRunner(int sampleRate, Func<IBackend, MusicRequest, MusicAudio> synth, params IDisposable[] disposables) : IMusicRunner
{
    public int SampleRate => sampleRate;

    public MusicAudio Synthesize(IBackend backend, MusicRequest request) => synth(backend, request);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
