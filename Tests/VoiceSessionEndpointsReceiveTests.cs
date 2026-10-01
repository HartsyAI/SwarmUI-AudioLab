using System.Net.WebSockets;
using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Hartsy.Extensions.AudioLab.Tests.Support;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceSessionEndpoints.ReceiveInboundAsync"/>'s per-message size cap, driven
/// by a scripted <see cref="FakeInboundWebSocket"/> and a recording <see cref="VoiceSessionEndpoints.PushInboundDelegate"/>
/// instead of a real socket and session.</summary>
public class VoiceSessionEndpointsReceiveTests
{
    private static VoiceInboundResampler Passthrough()
    {
        Assert.True(VoiceInboundResampler.TryCreate(16000, out VoiceInboundResampler resampler, out string error), error);
        return resampler;
    }

    [Fact]
    public async Task ReceiveInboundAsync_OversizedBinaryMessage_ClosesMessageTooBig_AndNeverPushesAudio()
    {
        byte[] huge = new byte[VoiceSessionEndpoints.MaxInboundAudioMessageBytes + 2];
        FakeInboundWebSocket socket = new([new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Binary, huge)]);
        List<float[]> pushed = [];

        await VoiceSessionEndpoints.ReceiveInboundAsync(socket, samples => pushed.Add(samples.ToArray()), Passthrough(), CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
        Assert.Empty(pushed);
    }

    [Fact]
    public async Task ReceiveInboundAsync_OversizedControlMessage_ClosesMessageTooBig()
    {
        byte[] huge = new byte[VoiceSessionEndpoints.MaxInboundControlMessageBytes + 2];
        Array.Fill(huge, (byte)' '); // valid (if pointless) UTF8, so a hypothetical fix bug isn't masked by a decode error
        FakeInboundWebSocket socket = new([new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Text, huge)]);

        await VoiceSessionEndpoints.ReceiveInboundAsync(socket, _ => { }, Passthrough(), CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
    }

    [Fact]
    public async Task ReceiveInboundAsync_BinaryMessageSplitAcrossManyContinuationFrames_UnderTheCap_StillWorks()
    {
        // 10,000 samples (20,000 bytes) is many times the 16 KiB per-ReceiveAsync buffer the production code
        // uses, forcing several continuation frames for one message, and well under the 64 KiB audio cap.
        float[] samples = new float[10_000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 2 == 0) ? 0.5f : -0.5f;
        }
        byte[] pcm16 = Pcm16.ToBytes(samples);
        Assert.True(pcm16.Length > 16 * 1024);
        FakeInboundWebSocket socket = new([
            new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Binary, pcm16),
        ]);
        List<float[]> pushed = [];

        await VoiceSessionEndpoints.ReceiveInboundAsync(socket, s => pushed.Add(s.ToArray()), Passthrough(), CancellationToken.None);

        float[] received = Assert.Single(pushed);
        Assert.Equal(samples.Length, received.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], received[i], 0.001);
        }
    }

    [Fact]
    public async Task ReceiveInboundAsync_EndMessage_StopsTheLoop()
    {
        FakeInboundWebSocket socket = new([
            new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes("{\"end\":true}")),
        ]);

        await VoiceSessionEndpoints.ReceiveInboundAsync(socket, _ => { }, Passthrough(), CancellationToken.None);

        // No explicit close expected here -- the caller's own finally block ends the session; this just
        // confirms the loop returns instead of looping forever waiting on a socket with nothing left queued.
        Assert.Null(socket.ClosedWith);
    }

    [Fact]
    public async Task ReceiveInboundAsync_UnrecognizedControlMessage_IsIgnored_LoopContinues()
    {
        FakeInboundWebSocket socket = new([
            new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes("{\"somethingElse\":true}")),
            new FakeInboundWebSocket.ScriptedMessage(WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes("{\"end\":true}")),
        ]);

        await VoiceSessionEndpoints.ReceiveInboundAsync(socket, _ => { }, Passthrough(), CancellationToken.None);

        Assert.Null(socket.ClosedWith);
    }
}
