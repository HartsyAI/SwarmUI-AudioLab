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
    /// <summary>Noise-schedule shift (ACE-Step); null = use the variant/pipeline default.</summary>
    public double? Shift { get; init; }
    /// <summary>Diffusion step count (ACE-Step); null = variant default (turbo 8, sft 50, base 32).</summary>
    public int? InferSteps { get; init; }
    /// <summary>Classifier-free guidance scale (HeartMuLa); null = pipeline default. 1.0 disables the 2× CFG pass.</summary>
    public double? CfgScale { get; init; }
    /// <summary>Sampling temperature (HeartMuLa); null = pipeline default.</summary>
    public double? Temperature { get; init; }
    /// <summary>Top-K sampling cutoff (HeartMuLa); null = pipeline default.</summary>
    public int? TopK { get; init; }
    /// <summary>BPM meta (ACE-Step prompt template); null = "N/A".</summary>
    public int? Bpm { get; init; }
    /// <summary>Key/scale meta (ACE-Step prompt template); empty = "N/A".</summary>
    public string KeyScale { get; init; } = "";
    /// <summary>Time-signature meta (ACE-Step prompt template); empty = "N/A".</summary>
    public string TimeSignature { get; init; } = "";
    /// <summary>Lyric vocal language (ACE-Step lyric template); empty = "en".</summary>
    public string VocalLanguage { get; init; } = "";
    /// <summary>ODE (default) or SDE diffusion solver (ACE-Step base/sft).</summary>
    public string InferMethod { get; init; } = "";
    /// <summary>ADG guidance instead of the default APG (ACE-Step base/sft CFG).</summary>
    public bool UseAdg { get; init; }
    /// <summary>CFG active-interval bounds over t (ACE-Step base/sft).</summary>
    public double CfgIntervalStart { get; init; } = 0d;
    public double CfgIntervalEnd { get; init; } = 1d;
    /// <summary>ACE-Step 5 Hz LM planner selection: "", "none", "0.6b", or "4b".</summary>
    public string LmModel { get; init; } = "";
    /// <summary>LM planner CoT thinking phase on/off.</summary>
    public bool Thinking { get; init; } = true;
    /// <summary>LM planner sampling controls (upstream defaults 0.85 / 2.0 / 0 / 0.9).</summary>
    public double LmTemperature { get; init; } = 0.85;
    public double LmCfgScale { get; init; } = 2.0;
    public int LmTopK { get; init; }
    public double LmTopP { get; init; } = 0.9;
    public string LmNegativePrompt { get; init; } = "";
}

/// <summary>Generated audio — mono (Right null) or stereo.</summary>
public readonly struct MusicAudio
{
    public float[] Left { get; init; }
    public float[] Right { get; init; }

    public static MusicAudio Mono(float[] samples) => new() { Left = samples, Right = null };
    public static MusicAudio Stereo(float[] left, float[] right) => new() { Left = left, Right = right };
}

/// <summary>A loaded music model reduced to: prompt → PCM at <see cref="SampleRate"/>. The cancellation token
/// is observed inside the synth loop so Stop Generation interrupts long autoregressive decodes mid-flight.</summary>
public interface IMusicRunner : IDisposable
{
    int SampleRate { get; }

    MusicAudio Synthesize(IBackend backend, MusicRequest request, CancellationToken cancel);
}

/// <summary>Wraps a synth delegate + the disposables a loaded model owns, so each model is a descriptor.</summary>
public sealed class MusicRunner(int sampleRate, Func<IBackend, MusicRequest, CancellationToken, MusicAudio> synth, params IDisposable[] disposables) : IMusicRunner
{
    public int SampleRate => sampleRate;

    public MusicAudio Synthesize(IBackend backend, MusicRequest request, CancellationToken cancel) => synth(backend, request, cancel);

    public void Dispose()
    {
        foreach (IDisposable d in disposables)
        {
            d?.Dispose();
        }
    }
}
