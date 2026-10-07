using System.Buffers.Binary;
using HartsyInference.Audio.Streaming;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>Mono PCM16 little-endian &lt;-&gt; float32 (&#177;1) conversion, the wire format both directions of
/// <c>AudioLabVoiceSession</c> use for raw audio frames.</summary>
internal static class Pcm16
{
    public static float[] ToFloat(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 2;
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
        {
            short sample = (short)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8));
            result[i] = sample / 32768f;
        }
        return result;
    }

    public static byte[] ToBytes(ReadOnlySpan<float> samples)
    {
        byte[] result = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)Math.Clamp(MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            result[i * 2] = (byte)(sample & 0xFF);
            result[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return result;
    }
}

/// <summary>The outbound reply-audio frame: a 4-byte little-endian turn id, then PCM16 mono at
/// <c>OutboundSampleRate</c> (24 kHz). The turn id is what lets the browser's per-turn playback queue, and the
/// server's own outbound pump (<c>VoiceSessionEndpoints.PumpOutboundAsync</c>), drop audio for a turn a
/// <c>bargein</c> event named, per <c>VoiceAgentSession.ReadOutbound(Span{float}, out int turnId)</c>'s own "one
/// turn's audio at most per call, stopping where the turn changes" contract.</summary>
internal static class VoiceOutboundFrame
{
    public const int HeaderBytes = 4;

    public static byte[] Encode(int turnId, ReadOnlySpan<float> samples)
    {
        byte[] frame = new byte[HeaderBytes + (samples.Length * 2)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, turnId);
        Pcm16.ToBytes(samples).CopyTo(frame, HeaderBytes);
        return frame;
    }

    /// <summary>Decodes a frame <see cref="Encode"/> produced. Test/documentation support only -- production code
    /// here never decodes its own outbound frames; the browser does that half in JS.</summary>
    public static (int TurnId, float[] Samples) Decode(ReadOnlySpan<byte> frame)
    {
        int turnId = BinaryPrimitives.ReadInt32LittleEndian(frame);
        return (turnId, Pcm16.ToFloat(frame[HeaderBytes..]));
    }
}

/// <summary>Picks a valid fixed frame size for <see cref="StreamingResampler"/>, which needs one that both divides
/// evenly into the rate ratio and is at least its own padding -- neither of which a browser's arbitrary
/// <c>AudioContext.sampleRate</c> satisfies on its own.</summary>
internal static class VoiceAudioFraming
{
    /// <summary>Computes the smallest frame size at <paramref name="inRate"/> that is both a valid
    /// <see cref="StreamingResampler"/> input frame for <paramref name="inRate"/>-&gt;<paramref name="outRate"/>
    /// and at least <paramref name="targetMs"/> long, failing rather than returning one over
    /// <paramref name="maxSamples"/> (a pathological rate -- eg one chosen to be coprime-ish with 16000 -- would
    /// otherwise demand an enormous frame before the resampler could ever run).</summary>
    public static bool TryComputeFrameSize(int inRate, int outRate, int numTaps, int targetMs, int maxSamples, out int frameSize, out string error)
    {
        if (inRate <= 0 || outRate <= 0)
        {
            frameSize = 0;
            error = "rates must be positive.";
            return false;
        }
        int gcd = Gcd(inRate, outRate);
        int down = inRate / gcd;
        int pad = numTaps;
        if (pad % down != 0)
        {
            pad += down - (pad % down);
        }
        int target = (int)((long)inRate * targetMs / 1000);
        int multiple = Math.Max(1, (target + down - 1) / down);
        int size = multiple * down;
        if (size < pad)
        {
            size = ((pad + down - 1) / down) * down;
        }
        if (size > maxSamples)
        {
            frameSize = 0;
            error = $"{inRate} Hz needs a {size}-sample resample frame to reach 16000 Hz, over the {maxSamples}-sample cap.";
            return false;
        }
        frameSize = size;
        error = null;
        return true;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}

/// <summary>Buffers inbound mono float audio at an arbitrary rate and emits it resampled to 16 kHz in whatever
/// whole frames are ready, carrying leftover samples to the next call. At exactly 16 kHz this is a passthrough --
/// running <see cref="StreamingResampler"/>'s filter on audio that needs no resampling would only dampen it.</summary>
internal sealed class VoiceInboundResampler
{
    /// <summary>How many taps <see cref="StreamingResampler"/> builds its filter with; see <see cref="VoiceAudioFraming"/>.</summary>
    public const int NumTaps = 64;

    /// <summary>Target frame length in milliseconds when a rate needs resampling (the actual frame can be longer;
    /// see <see cref="VoiceAudioFraming.TryComputeFrameSize"/>).</summary>
    public const int TargetFrameMs = 20;

    /// <summary>Hard cap on the computed input frame size; see <see cref="VoiceAudioFraming.TryComputeFrameSize"/>.</summary>
    public const int MaxFrameSamples = 8192;

    private const int OutputRate = 16000;

    private readonly StreamingResampler _resampler;
    private readonly int _frameSize;
    private readonly List<float> _pending = [];

    private VoiceInboundResampler(StreamingResampler resampler, int frameSize)
    {
        _resampler = resampler;
        _frameSize = frameSize;
    }

    /// <summary>The 16 kHz samples per input frame once resampling is needed; 0 for a passthrough instance.</summary>
    public int OutputFrameSize => _resampler?.OutputFrameSize ?? 0;

    public static bool TryCreate(int inputRate, out VoiceInboundResampler resampler, out string error)
    {
        if (inputRate == OutputRate)
        {
            resampler = new VoiceInboundResampler(null, 0);
            error = null;
            return true;
        }
        if (!VoiceAudioFraming.TryComputeFrameSize(inputRate, OutputRate, NumTaps, TargetFrameMs, MaxFrameSamples, out int frameSize, out error))
        {
            resampler = null;
            return false;
        }
        resampler = new VoiceInboundResampler(new StreamingResampler(inputRate, OutputRate, frameSize, NumTaps), frameSize);
        return true;
    }

    /// <summary>Emits what is left in the buffer as one final frame, zero-padded to a whole input frame (so up
    /// to one frame of trailing silence). Empty for a passthrough instance or an empty buffer.</summary>
    public float[] Flush()
    {
        if (_resampler is null || _pending.Count == 0)
        {
            return [];
        }
        _pending.AddRange(new float[_frameSize - _pending.Count]);
        return Push([]);
    }

    /// <summary>Feeds more input samples and returns however many whole 16 kHz frames are now ready to push into
    /// <c>VoiceAgentSession.PushInbound</c> -- an empty array when less than one input frame has accumulated yet.
    /// A passthrough instance returns <paramref name="input"/> unchanged.</summary>
    public float[] Push(ReadOnlySpan<float> input)
    {
        if (_resampler is null)
        {
            return input.ToArray();
        }
        _pending.AddRange(input.ToArray());
        int framesReady = _pending.Count / _frameSize;
        if (framesReady == 0)
        {
            return [];
        }
        float[] output = new float[framesReady * _resampler.OutputFrameSize];
        float[] inBuffer = new float[_frameSize];
        for (int f = 0; f < framesReady; f++)
        {
            _pending.CopyTo(f * _frameSize, inBuffer, 0, _frameSize);
            _resampler.Process(inBuffer, output.AsSpan(f * _resampler.OutputFrameSize, _resampler.OutputFrameSize));
        }
        _pending.RemoveRange(0, framesReady * _frameSize);
        return output;
    }
}
