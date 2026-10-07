using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Audio.Wake;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>A scripted <see cref="ISatelliteClaimHost"/>: hands out real <see cref="WakeDeviceClaim"/> objects
/// (a public, plain-data type) and lets a test fire the frame and disconnect callbacks the engine would.</summary>
internal sealed class FakeClaimHost : ISatelliteClaimHost
{
    public readonly object Gate = new();
    public readonly List<string> Log = [];
    public readonly List<WakeDeviceClaim> Claims = [];
    public WakeDeviceClaim Current;
    public bool ReturnNull;
    public bool ThrowAlreadyClaimed;

    public WakeDeviceClaim Claim(string deviceId, WakeInboundFrameHandler onFrame, Action onDisconnected = null)
    {
        lock (Gate)
        {
            Log.Add("Claim");
            if (ThrowAlreadyClaimed)
            {
                throw new InvalidOperationException($"Device '{deviceId}' is already claimed.");
            }
            if (ReturnNull)
            {
                return null;
            }
            WakeDeviceClaim claim = new(onFrame, onDisconnected);
            Claims.Add(claim);
            Current = claim;
            return claim;
        }
    }

    public void Release(string deviceId, WakeDeviceClaim claim)
    {
        lock (Gate)
        {
            Log.Add("Release");
            if (ReferenceEquals(Current, claim))
            {
                Current = null;
            }
        }
    }

    public int Count(string entry)
    {
        lock (Gate)
        {
            return Log.Count(e => e == entry);
        }
    }

    /// <summary>The engine's disconnect path: clears the claim, then calls its OnDisconnected.</summary>
    public void Disconnect()
    {
        WakeDeviceClaim claim;
        lock (Gate)
        {
            claim = Current;
            Current = null;
        }
        claim?.OnDisconnected?.Invoke();
    }
}

internal sealed class FakeSink : ISatelliteAudioSink
{
    public readonly List<byte[]> Writes = [];
    public bool Completed;

    public Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancel)
    {
        lock (Writes)
        {
            Writes.Add(pcm.ToArray());
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancel)
    {
        Completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSessionLink : ISatelliteLink
{
    public readonly List<string> Statuses = [];
    public readonly List<FakeSink> Sinks = [];

    public Task<bool> SendStatusAsync(string deviceId, string state, string detail = null)
    {
        lock (Statuses)
        {
            Statuses.Add(state);
        }
        return Task.FromResult(true);
    }

    public WakeAudioStream BeginAudio(string deviceId, int sampleRate) => throw new NotSupportedException();

    public Task<int> SendAudioAsync(string deviceId, ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken cancel) =>
        throw new NotSupportedException();

    public ISatelliteAudioSink OpenAudioSink(string deviceId, int sampleRate)
    {
        FakeSink sink = new();
        lock (Sinks)
        {
            Sinks.Add(sink);
        }
        return sink;
    }

    public string[] StatusSnapshot()
    {
        lock (Statuses)
        {
            return [.. Statuses];
        }
    }

    public FakeSink[] SinkSnapshot()
    {
        lock (Sinks)
        {
            return [.. Sinks];
        }
    }
}

/// <summary>A voice session that records pushed audio and plays back scripted (turnId, samples) chunks.</summary>
internal sealed class FakeVoiceSession : ISatelliteVoiceSession
{
    private readonly Queue<(int TurnId, float[] Samples)> _outbound = new();
    public readonly List<float> Pushed = [];
    public bool Started;
    public bool Disposed;

    public int OutboundSampleRate => 16000; // passthrough in the resampler: samples go out one for one.

    public event Action<SatelliteSessionEvent> EventRaised;

    public Task StartAsync(CancellationToken cancel)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public void PushInbound(ReadOnlySpan<float> samples)
    {
        lock (Pushed)
        {
            Pushed.AddRange(samples.ToArray());
        }
    }

    public int PushedCount
    {
        get
        {
            lock (Pushed)
            {
                return Pushed.Count;
            }
        }
    }

    public int ReadOutbound(Span<float> destination, out int turnId)
    {
        lock (_outbound)
        {
            if (_outbound.Count == 0)
            {
                turnId = 0;
                return 0;
            }
            (int id, float[] samples) = _outbound.Dequeue();
            int n = Math.Min(destination.Length, samples.Length);
            samples.AsSpan(0, n).CopyTo(destination);
            turnId = id;
            return n;
        }
    }

    public void Enqueue(int turnId, params float[] samples)
    {
        lock (_outbound)
        {
            _outbound.Enqueue((turnId, samples));
        }
    }

    public void Raise(SatelliteSessionEvent ev) => EventRaised?.Invoke(ev);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSessionFactory : ISatelliteVoiceSessionFactory
{
    public readonly FakeVoiceSession Session = new();
    public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Created;

    public FakeSessionFactory(bool ready = true)
    {
        if (ready)
        {
            Gate.SetResult();
        }
    }

    public async Task<ISatelliteVoiceSession> CreateAsync(string deviceId, CancellationToken cancel)
    {
        await Gate.Task.WaitAsync(cancel).ConfigureAwait(false);
        Created++;
        return Session;
    }
}

/// <summary>The Session-mode lifecycle against fakes: the engine's claim API, the voice session, and the
/// satellite link. No wake listener, socket, voice model or GPU.</summary>
public class SatelliteVoiceSessionTests
{
    private static readonly SatelliteSessionOptions FastOptions = new()
    {
        IdleTimeout = TimeSpan.FromSeconds(30),
        IdleCheckInterval = TimeSpan.FromMilliseconds(10),
        PumpInterval = TimeSpan.FromMilliseconds(5),
        StreamIdleGap = TimeSpan.FromMilliseconds(100),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static (SatelliteVoiceSessionManager Manager, FakeClaimHost Claims, FakeSessionLink Link, FakeSessionFactory Factory)
        Build(SatelliteSessionOptions options = null, bool factoryReady = true)
    {
        FakeClaimHost claims = new();
        FakeSessionLink link = new();
        FakeSessionFactory factory = new(factoryReady);
        return (new SatelliteVoiceSessionManager(claims, link, factory, options ?? FastOptions), claims, link, factory);
    }

    private static void Frame(FakeClaimHost claims, params float[] samples) => claims.Current.OnFrame(samples);

    [Fact]
    public async Task TryStart_ClaimsTheDevice_AndDeliversInboundFramesToTheSession()
    {
        var (manager, claims, _, factory) = Build();

        Assert.True(manager.TryStart("sat-1"));
        Assert.Equal(1, claims.Count("Claim"));
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");

        Frame(claims, 0.1f, 0.2f, 0.3f);
        Assert.Equal(3, factory.Session.PushedCount);

        await manager.StopAllAsync();
    }

    [Fact]
    public async Task FramesArrivingBeforeTheSessionIsReady_AreHeldAndPushedInOrder()
    {
        var (manager, claims, _, factory) = Build(factoryReady: false);

        Assert.True(manager.TryStart("sat-1"));
        Frame(claims, 1f, 2f);
        Frame(claims, 3f);
        Assert.False(factory.Session.Started);
        factory.Gate.SetResult();
        await WaitUntilAsync(() => factory.Session.Started && factory.Session.PushedCount == 3, "pending audio to drain");
        Frame(claims, 4f);

        Assert.Equal([1f, 2f, 3f, 4f], factory.Session.Pushed);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task EndingTheCall_ReleasesTheClaim_DisposesTheSession_AndSendsDoneLast()
    {
        var (manager, claims, link, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.Thinking));

        await manager.StopAllAsync();

        Assert.Equal(1, claims.Count("Release"));
        Assert.Null(claims.Current);
        Assert.True(factory.Session.Disposed);
        Assert.Equal(0, manager.ActiveCount);
        string[] statuses = link.StatusSnapshot();
        Assert.Equal(WakeStatus.Thinking, statuses[0]);
        Assert.Equal(WakeStatus.Done, statuses[^1]);
    }

    [Fact]
    public async Task AnIdleSession_EndsItselfAndReleasesTheClaim()
    {
        var options = new SatelliteSessionOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(60),
            IdleCheckInterval = TimeSpan.FromMilliseconds(10),
            PumpInterval = TimeSpan.FromMilliseconds(5),
        };
        var (manager, claims, link, factory) = Build(options);
        manager.TryStart("sat-1");

        await WaitUntilAsync(() => manager.ActiveCount == 0, "the idle session to end");

        Assert.Equal(1, claims.Count("Release"));
        Assert.True(factory.Session.Disposed);
        Assert.Equal(WakeStatus.Done, link.StatusSnapshot()[^1]);
    }

    [Fact]
    public async Task ABusySession_IsNotEndedByTheIdleTimer()
    {
        var options = new SatelliteSessionOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(60),
            IdleCheckInterval = TimeSpan.FromMilliseconds(10),
            PumpInterval = TimeSpan.FromMilliseconds(5),
        };
        var (manager, _, _, factory) = Build(options);
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.Speaking));

        await Task.Delay(250);

        Assert.Equal(1, manager.ActiveCount);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task AnErrorMidTurn_DoesNotHoldTheClaimOpenForever()
    {
        var options = new SatelliteSessionOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(60),
            IdleCheckInterval = TimeSpan.FromMilliseconds(10),
            PumpInterval = TimeSpan.FromMilliseconds(5),
        };
        var (manager, claims, _, factory) = Build(options);
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.Thinking));
        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.Error, 1, "boom"));

        await WaitUntilAsync(() => manager.ActiveCount == 0, "the call to end after the error");

        Assert.Equal(1, claims.Count("Release"));
    }

    [Fact]
    public async Task ADisconnect_ReclaimsTheDevice_AndTheNewClaimFeedsTheSameSession()
    {
        var (manager, claims, _, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        WakeDeviceClaim first = claims.Current;

        claims.Disconnect();

        Assert.Equal(2, claims.Count("Claim"));
        Assert.NotSame(first, claims.Current);
        Frame(claims, 0.5f);
        Assert.Equal(1, factory.Session.PushedCount);
        Assert.Equal(1, manager.ActiveCount);
        await manager.StopAllAsync();
        Assert.Equal(1, claims.Count("Release"));
    }

    [Fact]
    public async Task ADisconnect_WithNoLiveConnectionToReclaim_EndsTheCall()
    {
        var (manager, claims, link, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        claims.ReturnNull = true;

        claims.Disconnect();

        await WaitUntilAsync(() => manager.ActiveCount == 0, "the call to end");
        Assert.True(factory.Session.Disposed);
        Assert.Equal(WakeStatus.Done, link.StatusSnapshot()[^1]);
    }

    [Fact]
    public void TryStart_WhenClaimReturnsNull_ReportsFailureAndLeavesNothingBehind()
    {
        var (manager, claims, link, factory) = Build();
        claims.ReturnNull = true;

        Assert.False(manager.TryStart("sat-1"));

        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(0, factory.Created);
        Assert.Empty(link.StatusSnapshot());
    }

    [Fact]
    public void TryStart_WhenTheDeviceIsAlreadyClaimedByAnotherHost_ConsumesTheDetectionWithoutFallingBack()
    {
        var (manager, claims, _, factory) = Build();
        claims.ThrowAlreadyClaimed = true;

        Assert.True(manager.TryStart("sat-1")); // true = no Legacy turn on top of the other host

        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task ASecondDetection_OnADeviceWeAlreadyOwn_DoesNotClaimAgain()
    {
        var (manager, claims, _, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");

        Assert.True(manager.TryStart("sat-1"));

        Assert.Equal(1, claims.Count("Claim"));
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task ReplyAudio_ReachesTheSatelliteAsOneStreamPerTurn_AndCompletesWhenTheTurnDoes()
    {
        var (manager, _, link, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");

        factory.Session.Enqueue(1, 0.5f, 0.5f);
        factory.Session.Enqueue(1, 0.5f);
        await WaitUntilAsync(() => link.SinkSnapshot().Length == 1 && link.SinkSnapshot()[0].Writes.Count == 2, "turn 1 audio");
        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.TurnCompleted, 1));
        await WaitUntilAsync(() => link.SinkSnapshot()[0].Completed, "turn 1's stream to complete");

        factory.Session.Enqueue(2, 0.25f);
        await WaitUntilAsync(() => link.SinkSnapshot().Length == 2, "a second stream for turn 2");

        Assert.Equal(6, link.SinkSnapshot()[0].Writes.Sum(w => w.Length)); // 3 samples, PCM16
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task BargeIn_CompletesTheOpenStream_AndDropsStragglersOfTheFlushedTurn()
    {
        var (manager, _, link, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");
        factory.Session.Enqueue(1, 0.5f);
        await WaitUntilAsync(() => link.SinkSnapshot().Length == 1 && link.SinkSnapshot()[0].Writes.Count == 1, "turn 1 audio");

        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.BargeIn, 1));
        await WaitUntilAsync(() => link.SinkSnapshot()[0].Completed, "the barged-in stream to be flushed");
        factory.Session.Enqueue(1, 0.5f, 0.5f, 0.5f); // a straggler tagged with the flushed turn
        factory.Session.Enqueue(2, 0.25f);
        await WaitUntilAsync(() => link.SinkSnapshot().Length == 2, "turn 2 to open its own stream");

        Assert.Single(link.SinkSnapshot()[0].Writes); // the straggler never reached the device
        Assert.Equal(2, link.SinkSnapshot()[1].Writes.Sum(w => w.Length));
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task ToolCalls_AreLoggedOnly_AndNeverEndTheSession()
    {
        var (manager, _, _, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");

        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.ToolCall, 1, "lights_on {}"));
        await Task.Delay(50);

        Assert.Equal(1, manager.ActiveCount);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task AnEndedEngineSession_EndsTheCall()
    {
        var (manager, claims, _, factory) = Build();
        manager.TryStart("sat-1");
        await WaitUntilAsync(() => factory.Session.Started, "the session to start");

        factory.Session.Raise(new SatelliteSessionEvent(SatelliteSessionEventKind.Ended));

        await WaitUntilAsync(() => manager.ActiveCount == 0, "the call to end");
        Assert.Equal(1, claims.Count("Release"));
    }

    [Fact]
    public async Task SessionMode_ThroughTheRunner_ClaimsInsteadOfRunningTheLegacyTurn()
    {
        var (manager, claims, link, factory) = Build();
        RecordingAssistantCaller assistant = new([]);
        SatelliteTurnRunner runner = new(link, assistant, (_, _, _) => Task.FromResult(0), manager);

        runner.OnDetected(new JObject { ["device_id"] = "sat-1" }, SatelliteVoiceMode.Session); // wake word alone

        Assert.Equal(1, claims.Count("Claim"));
        Assert.Empty(assistant.Calls);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task SessionMode_ThroughTheRunner_FallsBackToLegacy_OnlyWhenClaimReturnsNull()
    {
        var (manager, claims, link, _) = Build();
        claims.ReturnNull = true;
        List<string> calls = [];
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSatelliteLink recording = new(calls, ended);
        RecordingAssistantCaller assistant = new(calls);
        SatelliteTurnRunner runner = new(recording, assistant, (_, _, _) => Task.FromResult(1), manager);

        runner.OnDetected(new JObject { ["device_id"] = "sat-1", ["command"] = "what time is it" }, SatelliteVoiceMode.Session);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Ask:what time is it", calls);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task SessionMode_RouteWords_StillStandAside()
    {
        var (manager, claims, link, _) = Build();
        SatelliteTurnRunner runner = new(link, new RecordingAssistantCaller([]), (_, _, _) => Task.FromResult(0), manager);

        runner.OnDetected(new JObject { ["device_id"] = "sat-1", ["route"] = "other" }, SatelliteVoiceMode.Session);
        await WaitUntilAsync(() => link.StatusSnapshot().Length == 1, "the Done status");

        Assert.Equal(0, claims.Count("Claim"));
    }
}
