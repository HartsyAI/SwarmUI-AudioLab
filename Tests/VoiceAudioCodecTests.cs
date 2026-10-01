using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for the PCM16 codec, the turn-tagged outbound frame, the resample frame-size formula, and
/// the inbound resampler's buffering -- all pure, no socket and no engine.</summary>
public class VoiceAudioCodecTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    public void Pcm16_RoundTrips_WithinOneLsb(float sample)
    {
        byte[] bytes = Pcm16.ToBytes([sample]);
        float[] back = Pcm16.ToFloat(bytes);
        Assert.Equal(sample, back[0], 0.0001);
    }

    [Fact]
    public void Pcm16_ToBytes_ClampsOutOfRangeSamples()
    {
        byte[] bytes = Pcm16.ToBytes([2.5f, -3f]);
        float[] back = Pcm16.ToFloat(bytes);
        Assert.InRange(back[0], 0.99, 1.0);
        Assert.InRange(back[1], -1.0, -0.99);
    }

    [Fact]
    public void Pcm16_ToFloat_EmptyInput_IsEmptyOutput()
    {
        Assert.Empty(Pcm16.ToFloat([]));
    }

    [Fact]
    public void VoiceOutboundFrame_EncodeThenDecode_RecoversTurnIdAndSamples()
    {
        float[] samples = [0.1f, -0.2f, 0.3f, 0f];
        byte[] frame = VoiceOutboundFrame.Encode(42, samples);
        Assert.Equal(VoiceOutboundFrame.HeaderBytes + (samples.Length * 2), frame.Length);
        (int turnId, float[] decoded) = VoiceOutboundFrame.Decode(frame);
        Assert.Equal(42, turnId);
        Assert.Equal(samples.Length, decoded.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], decoded[i], 0.0001);
        }
    }

    [Fact]
    public void VoiceOutboundFrame_Encode_WithTurnIdZero_StillCarriesNoAudio()
    {
        byte[] frame = VoiceOutboundFrame.Encode(0, []);
        Assert.Equal(VoiceOutboundFrame.HeaderBytes, frame.Length);
        (int turnId, float[] samples) = VoiceOutboundFrame.Decode(frame);
        Assert.Equal(0, turnId);
        Assert.Empty(samples);
    }

    // The advisor's own worked examples: 48 kHz -> 16 kHz needs a multiple of 3 at least 66 samples (20 ms = 960);
    // 44.1 kHz -> 16 kHz needs a multiple of 441 (20 ms = 882, already >= its own pad).
    [Theory]
    [InlineData(48000, 960)]
    [InlineData(44100, 882)]
    public void TryComputeFrameSize_MatchesTheWorkedExamples(int inRate, int expectedFrameSize)
    {
        bool ok = VoiceAudioFraming.TryComputeFrameSize(inRate, 16000, 64, 20, 8192, out int frameSize, out string error);
        Assert.True(ok, error);
        Assert.Equal(expectedFrameSize, frameSize);
    }

    [Fact]
    public void TryComputeFrameSize_RejectsAPathologicalRate_RatherThanReturningAHugeFrame()
    {
        // 16001 Hz is coprime-ish with 16000: down = 16001, forcing an enormous frame before anything could run.
        bool ok = VoiceAudioFraming.TryComputeFrameSize(16001, 16000, 64, 20, 8192, out _, out string error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void VoiceInboundResampler_At16kHz_IsAPassthrough()
    {
        Assert.True(VoiceInboundResampler.TryCreate(16000, out VoiceInboundResampler resampler, out _));
        float[] input = [0.1f, 0.2f, 0.3f];
        float[] output = resampler.Push(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void VoiceInboundResampler_At48kHz_BuffersUntilOneWholeFrame()
    {
        Assert.True(VoiceInboundResampler.TryCreate(48000, out VoiceInboundResampler resampler, out string error), error);
        // 960 samples/frame at 48k->16k (20 ms); feeding fewer than that yields nothing yet.
        float[] partial = new float[500];
        Assert.Empty(resampler.Push(partial));
        float[] rest = new float[460];
        float[] output = resampler.Push(rest);
        Assert.Equal(resampler.OutputFrameSize, output.Length);
        Assert.Equal(320, resampler.OutputFrameSize); // 960 samples at 48k is 20 ms = 320 samples at 16k.
    }

    [Fact]
    public void VoiceInboundResampler_RejectsAPathologicalRate()
    {
        Assert.False(VoiceInboundResampler.TryCreate(16001, out _, out string error));
        Assert.NotNull(error);
    }
}
