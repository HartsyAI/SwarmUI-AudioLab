using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Audio.Wake;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>A scripted <see cref="ISatelliteLink"/> that records every call and signals a
/// <see cref="TaskCompletionSource"/> on a <see cref="WakeStatus.Done"/> status -- which
/// <see cref="SatelliteTurnRunner.OnDetected"/>'s own <c>finally</c> block sends last, for every turn that is
/// still "current" for its device when it ends, success or error alike -- so a test can await "the turn
/// finished" without a sleep or a race against that same <c>finally</c> block still having to run.
/// <see cref="BeginAudio"/>/<see cref="SendAudioAsync"/> are never exercised by these tests: every test supplies
/// its own "speak" delegate to <see cref="SatelliteTurnRunner"/> instead of the real TTS-backed one, so nothing
/// here needs a live engine.</summary>
internal sealed class RecordingSatelliteLink : ISatelliteLink
{
    public readonly List<string> Calls;
    private readonly TaskCompletionSource _turnEnded;

    public RecordingSatelliteLink(List<string> calls, TaskCompletionSource turnEnded)
    {
        Calls = calls;
        _turnEnded = turnEnded;
    }

    public Task<bool> SendStatusAsync(string deviceId, string state, string detail = null)
    {
        lock (Calls)
        {
            Calls.Add($"Status:{state}");
        }
        if (state == WakeStatus.Done)
        {
            _turnEnded.TrySetResult();
        }
        return Task.FromResult(true);
    }

    public WakeAudioStream BeginAudio(string deviceId, int sampleRate) =>
        throw new NotSupportedException("Not exercised: every test here supplies its own speak delegate.");

    public Task<int> SendAudioAsync(string deviceId, ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken cancel) =>
        throw new NotSupportedException("Not exercised: every test here supplies its own speak delegate.");
}

/// <summary>A scripted <see cref="IVoiceAssistantCaller"/> that records every question asked and returns a
/// canned reply or error.</summary>
internal sealed class RecordingAssistantCaller : IVoiceAssistantCaller
{
    public readonly List<string> Calls;
    public string Reply = "a reply";
    public string Error;

    public RecordingAssistantCaller(List<string> calls)
    {
        Calls = calls;
    }

    public Task<(string Reply, string Error)> AskAsync(string text, CancellationToken cancel)
    {
        lock (Calls)
        {
            Calls.Add($"Ask:{text}");
        }
        return Task.FromResult((Reply, Error));
    }
}

/// <summary>Unit tests for <see cref="SatelliteTurnRunner"/> -- the orchestration <see cref="VoiceTurnOrchestrator"/>
/// delegates every detection to -- against fakes for the satellite link, the assistant call, and speech
/// delivery. No live wake listener, no HTTP, no TTS engine, no GPU.</summary>
public class SatelliteTurnRunnerTests
{
    /// <summary>Builds a detection payload, omitting a null field entirely rather than setting it to a JSON
    /// null -- matching the real wire shape (<c>WakeWordService.ToJson</c>) closely enough to matter here:
    /// Newtonsoft's object-initializer indexer tags a null C# string as <see cref="JTokenType.String"/> with a
    /// null value, not <see cref="JTokenType.Null"/>, so a key that is really meant to be absent (the doc on
    /// <c>SatelliteTurnRunner.OnDetected</c>'s command/transcript fallback says exactly that: "absent means an
    /// engine old enough not to separate them") must never be assigned at all, or the production code's own
    /// <c>commandToken is null || commandToken.Type == JTokenType.Null</c> check does not see what this test
    /// means it to.</summary>
    private static JObject Detection(string deviceId, string command = null, string transcript = null, string route = null)
    {
        JObject payload = new() { ["device_id"] = deviceId };
        if (command is not null)
        {
            payload["command"] = command;
        }
        if (transcript is not null)
        {
            payload["transcript"] = transcript;
        }
        if (route is not null)
        {
            payload["route"] = route;
        }
        return payload;
    }

    private static async Task<List<string>> RunAndCollectAsync(SatelliteVoiceMode mode, string reply = "a reply",
        string assistantError = null, JObject detection = null, Action<SatelliteTurnRunner> beforeDetect = null)
    {
        List<string> calls = [];
        TaskCompletionSource turnEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSatelliteLink link = new(calls, turnEnded);
        RecordingAssistantCaller assistant = new(calls) { Reply = reply, Error = assistantError };
        SatelliteTurnRunner runner = new(link, assistant, (deviceId, text, cancel) =>
        {
            lock (calls)
            {
                calls.Add($"Speak:{deviceId}:{text}");
            }
            return Task.FromResult(42);
        });
        beforeDetect?.Invoke(runner);
        runner.OnDetected(detection ?? Detection("sat-1", command: "what time is it"), mode);
        await turnEnded.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return calls;
    }

    [Fact]
    public void DefaultSettings_SelectLegacy()
    {
        Assert.Equal(SatelliteVoiceMode.Legacy, new WakeWordSettings().SatelliteVoiceMode);
    }

    [Fact]
    public async Task OnDetected_LegacyMode_RunsStatusAskSpeakStatusInOrder()
    {
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy, reply: "it is noon");

        Assert.Equal(
            [
                $"Status:{WakeStatus.Thinking}",
                "Ask:what time is it",
                $"Status:{WakeStatus.Speaking}",
                "Speak:sat-1:it is noon",
                $"Status:{WakeStatus.Done}",
            ],
            calls);
    }

    [Fact]
    public async Task OnDetected_SessionMode_RunsTheExactSameSequenceAsLegacy()
    {
        // The headline property this PR adds: selecting Session does not change what actually runs, because
        // Session is not implemented yet (see SatelliteVoiceMode.Session's own remarks on why) -- it only logs
        // one warning. This proves the call sequence is identical to the Legacy test above, mode for mode.
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Session, reply: "it is noon");

        Assert.Equal(
            [
                $"Status:{WakeStatus.Thinking}",
                "Ask:what time is it",
                $"Status:{WakeStatus.Speaking}",
                "Speak:sat-1:it is noon",
                $"Status:{WakeStatus.Done}",
            ],
            calls);
    }

    [Fact]
    public async Task OnDetected_PrefersCommandOverTranscript()
    {
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy,
            detection: Detection("sat-1", command: "the real command", transcript: "hey jarvis the real command"));

        Assert.Contains("Ask:the real command", calls);
    }

    [Fact]
    public async Task OnDetected_FallsBackToTranscript_WhenTheCommandKeyIsAbsent()
    {
        // Named for exactly the "absent" case the production doc comment describes, not "the engine did not
        // separate them": WakeWordService.ToJson builds the real event via `["command"] = evt.Command`, and if
        // evt.Command is a genuine null, Newtonsoft tags that as JTokenType.String with a null Value, not
        // JTokenType.Null (confirmed empirically -- see the Detection helper's own remarks) -- so OnDetected's
        // `Type == JTokenType.Null` branch never actually fires on that path, and commandToken.ToString()
        // returns "" there instead of falling back. That looks like a real, pre-existing Legacy bug (a
        // wake-only detection where the engine did not separate command from transcript would send Done and
        // never ask the assistant), separate from this PR and preserved verbatim rather than fixed here, since
        // fixing it would contradict the Legacy-unchanged requirement every other test in this file is for.
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy,
            detection: Detection("sat-1", command: null, transcript: "only a transcript"));

        Assert.Contains("Ask:only a transcript", calls);
    }

    [Fact]
    public async Task OnDetected_ConfiguredRoute_SendsDoneOnly_NeverAsksOrSpeaks()
    {
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy,
            detection: Detection("sat-1", command: "ignored", route: "some-other-handler"));

        Assert.Equal([$"Status:{WakeStatus.Done}"], calls);
    }

    [Fact]
    public async Task OnDetected_NoCommandOrTranscript_SendsDoneOnly_NeverAsksOrSpeaks()
    {
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy,
            detection: Detection("sat-1", command: "", transcript: ""));

        Assert.Equal([$"Status:{WakeStatus.Done}"], calls);
    }

    [Fact]
    public async Task OnDetected_AssistantError_SendsThinkingThenErrorThenDone_NeverSpeaks()
    {
        // The trailing Done is not optional here: it is the same finally block every path through RunTurnAsync
        // goes through, success or error alike, and the device needs it to know the turn is over and go back to
        // listening -- only a barge-in (a newer turn replacing this one as "current") skips it, which is its own
        // test below.
        List<string> calls = await RunAndCollectAsync(SatelliteVoiceMode.Legacy, assistantError: "the assistant is unreachable");

        Assert.Equal(
            [
                $"Status:{WakeStatus.Thinking}",
                "Ask:what time is it",
                $"Status:{WakeStatus.Error}",
                $"Status:{WakeStatus.Done}",
            ],
            calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("Speak:"));
    }

    [Fact]
    public async Task OnDetected_SecondDetectionForTheSameDevice_CancelsTheFirstTurn()
    {
        List<string> calls = [];
        TaskCompletionSource firstAskReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstAsk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondTurnEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSatelliteLink link = new(calls, secondTurnEnded);
        BlockingThenRecordingAssistantCaller assistant = new(calls, firstAskReached, releaseFirstAsk);
        SatelliteTurnRunner runner = new(link, assistant, (deviceId, text, cancel) =>
        {
            lock (calls)
            {
                calls.Add($"Speak:{deviceId}:{text}");
            }
            return Task.FromResult(1);
        });

        runner.OnDetected(Detection("sat-1", command: "first question"), SatelliteVoiceMode.Legacy);
        await firstAskReached.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // The first turn is now blocked inside AskAsync, holding its own CancellationTokenSource as "current"
        // for sat-1. A second detection must cancel that token (observable as OperationCanceledException from
        // the first AskAsync once it is released) and become the new current turn.
        runner.OnDetected(Detection("sat-1", command: "second question"), SatelliteVoiceMode.Legacy);
        releaseFirstAsk.TrySetResult();
        await secondTurnEnded.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Only the second turn's Ask/Speak/Done should appear -- the first was cancelled before it could do
        // anything past asking, and its own finally block must see it is no longer "current" for sat-1 and so
        // must not send a Done that would close the second (still live) turn on the device.
        Assert.DoesNotContain("Speak:sat-1:first question's reply", calls);
        Assert.Contains("Ask:second question", calls);
        Assert.Equal(1, calls.Count(c => c == $"Status:{WakeStatus.Done}"));
    }

    /// <summary>Blocks the first call to <see cref="AskAsync"/> until released (signalling
    /// <paramref name="reached"/> once inside), so a test can deterministically land a second detection while
    /// the first turn is still in flight. Every call after the first behaves like
    /// <see cref="RecordingAssistantCaller"/>.</summary>
    private sealed class BlockingThenRecordingAssistantCaller(List<string> calls, TaskCompletionSource reached, TaskCompletionSource release) : IVoiceAssistantCaller
    {
        private int _calls;

        public async Task<(string Reply, string Error)> AskAsync(string text, CancellationToken cancel)
        {
            lock (calls)
            {
                calls.Add($"Ask:{text}");
            }
            if (Interlocked.Exchange(ref _calls, 1) == 0)
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            return ("a reply", null);
        }
    }
}
