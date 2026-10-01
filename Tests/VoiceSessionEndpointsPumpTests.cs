using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceSessionEndpoints.PumpOutboundAsync"/>, driven by a fake
/// <see cref="VoiceSessionEndpoints.ReadOutboundDelegate"/> instead of a real <c>VoiceAgentSession</c> (which
/// needs a live engine). <c>VoiceAgentSession.ReadOutbound(Span{float}, out int)</c> itself already guarantees
/// "one turn's audio at most per call, stopping where the turn changes" (HartsyInference.Voice 2.0.0-alpha.238);
/// what these tests prove is that the pump's own loop passes that through faithfully -- each sent frame gets
/// exactly the turn id its read returned, never a stale or blended one.</summary>
public class VoiceSessionEndpointsPumpTests
{
    /// <summary>Hands back one scripted (turnId, samples) read per call, then silence (turnId 0, no samples)
    /// once the script is empty -- the shape a real idle session's ReadOutbound settles into between turns.</summary>
    private sealed class ScriptedReads
    {
        private readonly Queue<(int TurnId, float[] Samples)> _script = new();

        public void Enqueue(int turnId, float[] samples) => _script.Enqueue((turnId, samples));

        public int Read(Span<float> destination, out int turnId)
        {
            if (_script.Count == 0)
            {
                turnId = 0;
                return 0;
            }
            (int scriptedTurn, float[] samples) = _script.Dequeue();
            samples.AsSpan().CopyTo(destination);
            turnId = scriptedTurn;
            return samples.Length;
        }
    }

    [Fact]
    public async Task PumpOutboundAsync_TagsEachSentFrameWithExactlyOneTurn_NeverMixingTwoTurnsUnderOneTag()
    {
        ScriptedReads reads = new();
        reads.Enqueue(1, [0.10f, 0.20f]);
        reads.Enqueue(2, [0.30f, 0.40f, 0.50f]);
        // Every read after this returns silence (turnId 0), the same as a real session between turns.

        List<(int TurnId, float[] Samples)> sent = [];
        Task SendAsync(byte[] bytes)
        {
            sent.Add(VoiceOutboundFrame.Decode(bytes));
            return Task.CompletedTask;
        }

        using CancellationTokenSource cts = new();
        Task pump = VoiceSessionEndpoints.PumpOutboundAsync(reads.Read, outboundSampleRate: 24000,
            lastFlushedTurnIdRef: () => 0, SendAsync, cts.Token);
        // The pump ticks every 20 ms; this is many times over what two scripted reads need to drain.
        await Task.Delay(300);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);

        // Exactly two frames were ever sent (silence/zero-length reads are never encoded), each carrying only
        // the one turn's samples its read returned -- not merged with the other turn's, and not re-tagged with
        // a later or earlier turn's id.
        Assert.Equal(2, sent.Count);
        Assert.Equal(1, sent[0].TurnId);
        Assert.Equal(2, sent[0].Samples.Length);
        Assert.Equal(0.10f, sent[0].Samples[0], 0.001);
        Assert.Equal(0.20f, sent[0].Samples[1], 0.001);
        Assert.Equal(2, sent[1].TurnId);
        Assert.Equal(3, sent[1].Samples.Length);
        Assert.Equal(0.30f, sent[1].Samples[0], 0.001);
        Assert.Equal(0.50f, sent[1].Samples[2], 0.001);
    }

    [Fact]
    public async Task PumpOutboundAsync_DropsAudioAtOrBelowTheLastFlushedTurn()
    {
        // The watermark moves to 2 as a side effect of the SAME read call that returns turn 2's straggler
        // audio -- deterministically reproducing "a barge-in lands exactly as that turn's last samples are
        // read" without relying on wall-clock timing between the test and the pump's 20 ms loop.
        int flushed = 0;
        int call = 0;
        int Read(Span<float> destination, out int turnId)
        {
            call++;
            switch (call)
            {
                case 1:
                    destination[0] = 0.1f;
                    turnId = 1;
                    return 1;
                case 2:
                    flushed = 2; // the barge-in that makes this very turn's audio (and anything before it) stale.
                    destination[0] = 0.2f;
                    turnId = 2;
                    return 1;
                case 3:
                    destination[0] = 0.3f;
                    turnId = 3;
                    return 1;
                default:
                    turnId = 0;
                    return 0;
            }
        }

        List<int> sentTurnIds = [];
        Task SendAsync(byte[] bytes)
        {
            (int turnId, _) = VoiceOutboundFrame.Decode(bytes);
            sentTurnIds.Add(turnId);
            return Task.CompletedTask;
        }

        using CancellationTokenSource cts = new();
        Task pump = VoiceSessionEndpoints.PumpOutboundAsync(Read, outboundSampleRate: 24000,
            lastFlushedTurnIdRef: () => flushed, SendAsync, cts.Token);
        await Task.Delay(300);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);

        // Turn 1 was already sent before the flush landed; turn 2's own straggler is dropped by the same
        // watermark it triggered; turn 3 postdates the flush and goes through untouched.

        Assert.Equal([1, 3], sentTurnIds);
    }
}
