using System.Collections.Concurrent;
using System.Threading.Channels;
using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using HartsyInference.Engine.Audio.Wake;
using HartsyInference.Tools;
using HartsyInference.Voice;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>The engine's host-claim surface (<c>WakeService.Claim</c>/<c>Release</c>), as a seam so the session
/// lifecycle is testable without a live wake listener.</summary>
internal interface ISatelliteClaimHost
{
    /// <summary>See <see cref="WakeWordService.Claim"/>: null when there is no live connection to claim; throws
    /// <see cref="InvalidOperationException"/> when the device is already claimed.</summary>
    WakeDeviceClaim Claim(string deviceId, WakeInboundFrameHandler onFrame, Action onDisconnected = null);

    /// <summary>See <see cref="WakeWordService.Release"/>.</summary>
    void Release(string deviceId, WakeDeviceClaim claim);
}

/// <summary>The real <see cref="ISatelliteClaimHost"/>, over <see cref="WakeWordService"/>'s static surface.</summary>
internal sealed class WakeServiceClaimHost : ISatelliteClaimHost
{
    public static readonly WakeServiceClaimHost Instance = new();

    private WakeServiceClaimHost()
    {
    }

    public WakeDeviceClaim Claim(string deviceId, WakeInboundFrameHandler onFrame, Action onDisconnected = null) =>
        WakeWordService.Claim(deviceId, onFrame, onDisconnected);

    public void Release(string deviceId, WakeDeviceClaim claim) => WakeWordService.Release(deviceId, claim);
}

/// <summary>One reply's outbound audio to a satellite: 16 kHz mono PCM16, written as it is produced. A seam over
/// <see cref="WakeAudioStream"/>, which cannot be constructed without a live codec.</summary>
internal interface ISatelliteAudioSink : IAsyncDisposable
{
    Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancel);

    /// <summary>Ends the reply: the final frame is what tells the device the reply is over.</summary>
    Task CompleteAsync(CancellationToken cancel);
}

/// <summary>The real <see cref="ISatelliteAudioSink"/>, over a <see cref="WakeAudioStream"/>.</summary>
internal sealed class WakeAudioStreamSink : ISatelliteAudioSink
{
    private readonly WakeAudioStream _stream;

    public WakeAudioStreamSink(WakeAudioStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancel) => _stream.WriteAsync(pcm, cancel);

    public Task CompleteAsync(CancellationToken cancel) => _stream.CompleteAsync(cancel);

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

internal enum SatelliteSessionEventKind
{
    Listening,
    Thinking,
    Speaking,
    UserTranscript,
    AssistantTranscript,
    BargeIn,
    ToolCall,
    ToolResult,
    TurnCompleted,
    Error,
    Ended,
}

/// <summary>The slice of a <see cref="VoiceAgentEvent"/> the satellite call acts on.</summary>
internal readonly record struct SatelliteSessionEvent(SatelliteSessionEventKind Kind, int TurnId = 0, string Text = null);

/// <summary>The slice of <see cref="VoiceAgentSession"/> a satellite call drives, as a seam: a fake stands in for
/// it in tests, since a real one needs the engine's voice models.</summary>
internal interface ISatelliteVoiceSession : IAsyncDisposable
{
    /// <summary>Rate of <see cref="ReadOutbound"/>'s samples (24 kHz for the Kokoro-backed session).</summary>
    int OutboundSampleRate { get; }

    event Action<SatelliteSessionEvent> EventRaised;

    Task StartAsync(CancellationToken cancel);

    /// <summary>16 kHz mono float in [-1, 1]; never blocks.</summary>
    void PushInbound(ReadOnlySpan<float> samples);

    /// <summary>See <see cref="VoiceAgentSession.ReadOutbound(Span{float}, out int)"/>.</summary>
    int ReadOutbound(Span<float> destination, out int turnId);
}

internal interface ISatelliteVoiceSessionFactory
{
    Task<ISatelliteVoiceSession> CreateAsync(string deviceId, CancellationToken cancel);
}

/// <summary>Timing knobs of a satellite call; the defaults are the production values.</summary>
internal sealed class SatelliteSessionOptions
{
    /// <summary>How long a session with nothing happening stays open, waiting for a follow-up, before the claim
    /// is released and the device goes back to wake-word listening.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the outbound pump polls the session for reply audio (the engine's own sender cadence).</summary>
    public TimeSpan PumpInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>A reply stream with no new audio for this long is completed even if no turn-completed event
    /// arrived, so the device is never left holding a reply open.</summary>
    public TimeSpan StreamIdleGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Inbound audio held between the claim and the session being ready (models load, the session
    /// starts), so the start of the user's speech is not lost. Oldest dropped beyond this.</summary>
    public int MaxPendingInboundSamples { get; init; } = 16000 * 5;
}

/// <summary>Owns the satellite voice sessions: at most one per device, started from a detection.</summary>
internal sealed class SatelliteVoiceSessionManager
{
    /// <summary>The satellite plays 16 kHz mono PCM16 (the wire format carries no rate; see the wake protocol).</summary>
    internal const int DeviceSampleRate = 16000;

    private readonly ISatelliteClaimHost _claims;
    private readonly ISatelliteLink _link;
    private readonly ISatelliteVoiceSessionFactory _factory;
    private readonly SatelliteSessionOptions _options;
    private readonly ConcurrentDictionary<string, SatelliteVoiceCall> _calls = new();

    public SatelliteVoiceSessionManager(ISatelliteClaimHost claims, ISatelliteLink link,
        ISatelliteVoiceSessionFactory factory, SatelliteSessionOptions options = null)
    {
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new SatelliteSessionOptions();
    }

    /// <summary>Sessions currently claimed or starting.</summary>
    public int ActiveCount => _calls.Count;

    /// <summary>Claims the device and starts a session for it. Returns true when a session now owns the device's
    /// turns (including when one already did: a detection that was already in flight when the claim was taken
    /// is not a second turn). Returns false when the claim could not be taken -- the caller then runs the
    /// Legacy turn for this detection.</summary>
    public bool TryStart(string deviceId)
    {
        if (_calls.ContainsKey(deviceId))
        {
            Logs.Debug($"[AudioLab][Session] '{deviceId}': a session already owns this device; ignoring the detection.");
            return true;
        }
        SatelliteVoiceCall call = new(deviceId, _claims, _link, _factory, _options, c => _calls.TryRemove(
            new KeyValuePair<string, SatelliteVoiceCall>(c.DeviceId, c)));
        if (!_calls.TryAdd(deviceId, call))
        {
            return true;
        }
        string failure;
        try
        {
            if (call.Claim())
            {
                call.Start();
                return true;
            }
            failure = "the device has no live connection to claim";
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        _calls.TryRemove(new KeyValuePair<string, SatelliteVoiceCall>(deviceId, call));
        Logs.Warning($"[AudioLab][Session] '{deviceId}': could not start a session ({failure}); running the Legacy turn instead.");
        return false;
    }

    /// <summary>Ends every session and waits for them to release their claims (shutdown).</summary>
    public async Task StopAllAsync()
    {
        SatelliteVoiceCall[] calls = [.. _calls.Values];
        foreach (SatelliteVoiceCall call in calls)
        {
            call.End("shutting down");
        }
        await Task.WhenAll(calls.Select(c => c.Completion)).ConfigureAwait(false);
    }

    public void StopAll() => _ = StopAllAsync();
}

/// <summary>One satellite's session: the claim, the engine session, the outbound pump, the status relay.</summary>
internal sealed class SatelliteVoiceCall
{
    private readonly string _deviceId;
    private readonly ISatelliteClaimHost _claims;
    private readonly ISatelliteLink _link;
    private readonly ISatelliteVoiceSessionFactory _factory;
    private readonly SatelliteSessionOptions _options;
    private readonly Action<SatelliteVoiceCall> _onFinished;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(string State, string Detail)> _statuses = Channel.CreateUnbounded<(string, string)>();
    private readonly object _frameLock = new();
    private readonly List<float[]> _pending = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WakeDeviceClaim _claim;
    private ISatelliteVoiceSession _session;
    private int _pendingSamples;
    private bool _frameClosed;
    private int _endRequested;
    private int _flushedTurn;
    private int _completedTurn;
    private int _lastSeenTurn;
    private long _activityTicks = Environment.TickCount64;
    private volatile bool _busy;
    private string _lastStatus;

    public SatelliteVoiceCall(string deviceId, ISatelliteClaimHost claims, ISatelliteLink link,
        ISatelliteVoiceSessionFactory factory, SatelliteSessionOptions options, Action<SatelliteVoiceCall> onFinished)
    {
        _deviceId = deviceId;
        _claims = claims;
        _link = link;
        _factory = factory;
        _options = options;
        _onFinished = onFinished;
    }

    public string DeviceId => _deviceId;

    /// <summary>Completes once the claim is released, the session disposed and the final status sent.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Takes the claim. False when the device has no live connection; throws
    /// <see cref="InvalidOperationException"/> when something else already holds it.</summary>
    public bool Claim()
    {
        WakeDeviceClaim claim = _claims.Claim(_deviceId, OnFrame, OnDisconnected);
        if (claim is null)
        {
            return false;
        }
        _claim = claim;
        return true;
    }

    public void Start() => _ = Task.Run(RunAsync, CancellationToken.None);

    /// <summary>Asks the call to finish. Idempotent and non-blocking: safe from the engine's callbacks.</summary>
    public void End(string reason)
    {
        if (Interlocked.Exchange(ref _endRequested, 1) == 0)
        {
            Logs.Debug($"[AudioLab][Session] '{_deviceId}': ending ({reason}).");
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Runs on the engine's wake worker thread: must not block or throw.</summary>
    private void OnFrame(ReadOnlySpan<float> samples)
    {
        try
        {
            lock (_frameLock)
            {
                if (_frameClosed)
                {
                    return;
                }
                if (_session is not null)
                {
                    _session.PushInbound(samples);
                    return;
                }
                _pending.Add(samples.ToArray());
                _pendingSamples += samples.Length;
                while (_pendingSamples > _options.MaxPendingInboundSamples && _pending.Count > 1)
                {
                    _pendingSamples -= _pending[0].Length;
                    _pending.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab][Session] '{_deviceId}': dropping an inbound frame: {ex.Message}");
        }
    }

    /// <summary>The device's connection ended (the engine has already cleared the claim). The old connection's
    /// reply audio is gone, so it is flushed; then the device is re-claimed so a reconnect continues the
    /// session rather than ending it. No live connection, or a claim someone else took, ends the call.</summary>
    private void OnDisconnected()
    {
        if (Volatile.Read(ref _endRequested) != 0)
        {
            return;
        }
        MarkFlushed(Volatile.Read(ref _lastSeenTurn));
        try
        {
            WakeDeviceClaim claim = _claims.Claim(_deviceId, OnFrame, OnDisconnected);
            if (claim is null)
            {
                End("the device disconnected");
                return;
            }
            _claim = claim;
            Logs.Debug($"[AudioLab][Session] '{_deviceId}': the connection ended and was re-claimed.");
            if (Volatile.Read(ref _endRequested) != 0)
            {
                // Ended while re-claiming: RunAsync's release may have run against the old claim.
                _claims.Release(_deviceId, claim);
            }
        }
        catch (Exception ex)
        {
            End($"re-claiming after a disconnect failed: {ex.Message}");
        }
    }

    private async Task RunAsync()
    {
        CancellationToken cancel = _cts.Token;
        Task statusLoop = Task.Run(RelayStatusesAsync, CancellationToken.None);
        ISatelliteVoiceSession session = null;
        Task pump = Task.CompletedTask;
        try
        {
            session = await _factory.CreateAsync(_deviceId, cancel).ConfigureAwait(false);
            session.EventRaised += OnEvent;
            await session.StartAsync(cancel).ConfigureAwait(false);
            lock (_frameLock)
            {
                foreach (float[] frame in _pending)
                {
                    session.PushInbound(frame);
                }
                _pending.Clear();
                _pendingSamples = 0;
                _session = session;
            }
            _activityTicks = Environment.TickCount64;
            pump = PumpOutboundAsync(session, cancel);
            while (true)
            {
                await Task.Delay(_options.IdleCheckInterval, cancel).ConfigureAwait(false);
                if (!_busy && Environment.TickCount64 - Interlocked.Read(ref _activityTicks) > _options.IdleTimeout.TotalMilliseconds)
                {
                    End("idle");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Session] '{_deviceId}' failed: {ex.ReadableString()}");
            Enqueue(WakeStatus.Error, ex.Message);
        }
        finally
        {
            await FinishAsync(session, pump, statusLoop).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(ISatelliteVoiceSession session, Task pump, Task statusLoop)
    {
        try
        {
            lock (_frameLock)
            {
                _frameClosed = true;
                _session = null;
                _pending.Clear();
            }
            // First, so the engine resumes its own wake scoring as soon as the call is over.
            try { _claims.Release(_deviceId, _claim); }
            catch (Exception ex) { Logs.Debug($"[AudioLab][Session] '{_deviceId}': releasing the claim threw: {ex.Message}"); }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logs.Debug($"[AudioLab][Session] '{_deviceId}': the outbound pump ended on {ex.Message}"); }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Logs.Debug($"[AudioLab][Session] '{_deviceId}': ending the session threw: {ex.Message}"); }
            }
            Enqueue(WakeStatus.Done, null);
            _statuses.Writer.TryComplete();
            await statusLoop.ConfigureAwait(false);
        }
        finally
        {
            _onFinished(this);
            _cts.Dispose();
            _completion.TrySetResult();
        }
    }

    private void OnEvent(SatelliteSessionEvent ev)
    {
        try
        {
            switch (ev.Kind)
            {
                case SatelliteSessionEventKind.Listening:
                    _busy = false;
                    Touch();
                    break;
                case SatelliteSessionEventKind.Thinking:
                    _busy = true;
                    Touch();
                    EnqueueOnChange(WakeStatus.Thinking);
                    break;
                case SatelliteSessionEventKind.Speaking:
                    _busy = true;
                    Touch();
                    EnqueueOnChange(WakeStatus.Speaking);
                    break;
                case SatelliteSessionEventKind.UserTranscript:
                    Touch();
                    Logs.Debug($"[AudioLab][Session] '{_deviceId}': heard \"{ev.Text}\".");
                    break;
                case SatelliteSessionEventKind.AssistantTranscript:
                    Touch();
                    break;
                case SatelliteSessionEventKind.BargeIn:
                    MarkFlushed(ev.TurnId);
                    Touch();
                    break;
                case SatelliteSessionEventKind.ToolCall:
                    // The satellite protocol has no frame for a device action (its server-to-device frames are
                    // status and audio), so a tool the assistant runs is logged, never forwarded, and never
                    // ends the session.
                    Logs.Info($"[AudioLab][Session] '{_deviceId}': device action {ev.Text} (no satellite protocol frame carries it; logged only).");
                    break;
                case SatelliteSessionEventKind.TurnCompleted:
                    InterlockedMax(ref _completedTurn, ev.TurnId);
                    Touch();
                    break;
                case SatelliteSessionEventKind.Error:
                    Logs.Warning($"[AudioLab][Session] '{_deviceId}': {ev.Text}");
                    Enqueue(WakeStatus.Error, ev.Text);
                    break;
                case SatelliteSessionEventKind.Ended:
                    End("the voice session ended");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab][Session] '{_deviceId}': handling {ev.Kind} threw: {ex.Message}");
        }
    }

    private void Touch() => Interlocked.Exchange(ref _activityTicks, Environment.TickCount64);

    private void MarkFlushed(int turnId) => InterlockedMax(ref _flushedTurn, turnId);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private void EnqueueOnChange(string state)
    {
        if (_lastStatus == state)
        {
            return;
        }
        _lastStatus = state;
        Enqueue(state, null);
    }

    private void Enqueue(string state, string detail) => _statuses.Writer.TryWrite((state, detail));

    /// <summary>Sends statuses one at a time, in order, so <see cref="WakeStatus.Done"/> is really last.</summary>
    private async Task RelayStatusesAsync()
    {
        await foreach ((string state, string detail) in _statuses.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _link.SendStatusAsync(_deviceId, state, detail).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab][Session] '{_deviceId}': a status send failed: {ex.Message}");
            }
        }
    }

    /// <summary>Moves the session's reply audio to the satellite: a stream per turn, resampled to the device's
    /// 16 kHz, completed when the turn finishes, goes quiet, or is barged in on. Audio tagged at or below the
    /// last barge-in's turn is dropped, as in the browser route's pump.</summary>
    private async Task PumpOutboundAsync(ISatelliteVoiceSession session, CancellationToken cancel)
    {
        int rate = session.OutboundSampleRate;
        float[] buffer = new float[Math.Max(1, rate / 50)];
        ISatelliteAudioSink sink = null;
        VoiceInboundResampler resampler = null;
        int streamTurn = 0;
        long lastAudio = 0;

        async Task CloseSinkAsync()
        {
            ISatelliteAudioSink closing = sink;
            sink = null;
            resampler = null;
            if (closing is null)
            {
                return;
            }
            try
            {
                await closing.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                await closing.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab][Session] '{_deviceId}': closing a reply stream threw: {ex.Message}");
            }
        }

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(_options.PumpInterval, cancel).ConfigureAwait(false);
                int flushed = Volatile.Read(ref _flushedTurn);
                if (sink is not null && streamTurn <= flushed)
                {
                    await CloseSinkAsync().ConfigureAwait(false);
                }
                int read = session.ReadOutbound(buffer, out int turnId);
                if (read <= 0)
                {
                    if (sink is not null && (Volatile.Read(ref _completedTurn) >= streamTurn
                        || Environment.TickCount64 - lastAudio > _options.StreamIdleGap.TotalMilliseconds))
                    {
                        await CloseSinkAsync().ConfigureAwait(false);
                    }
                    continue;
                }
                if (turnId != 0)
                {
                    InterlockedMax(ref _lastSeenTurn, turnId);
                    if (turnId <= flushed)
                    {
                        continue;
                    }
                }
                if (sink is not null && turnId != streamTurn)
                {
                    await CloseSinkAsync().ConfigureAwait(false);
                }
                if (sink is null)
                {
                    sink = _link.OpenAudioSink(_deviceId, SatelliteVoiceSessionManager.DeviceSampleRate);
                    if (sink is null)
                    {
                        continue; // the satellite dropped; this audio has nowhere to go.
                    }
                    if (!VoiceInboundResampler.TryCreate(rate, out resampler, out string error))
                    {
                        throw new InvalidOperationException($"Cannot resample the session's {rate} Hz output: {error}");
                    }
                    streamTurn = turnId;
                }
                float[] pcm = resampler.Push(buffer.AsSpan(0, read));
                lastAudio = Environment.TickCount64;
                if (pcm.Length > 0)
                {
                    await sink.WriteAsync(Pcm16.ToBytes(pcm), cancel).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await CloseSinkAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The real <see cref="ISatelliteVoiceSessionFactory"/>: builds a <see cref="VoiceAgentSession"/> the same
/// way the browser's Voice Agent route does (shared model set, answered through LLMAssistant), but under a Swarm
/// session opened over loopback, as the Legacy turn does, since a satellite has no browser session.</summary>
internal sealed class EngineSatelliteVoiceSessionFactory : ISatelliteVoiceSessionFactory
{
    public static readonly EngineSatelliteVoiceSessionFactory Instance = new();

    private EngineSatelliteVoiceSessionFactory()
    {
    }

    public async Task<ISatelliteVoiceSession> CreateAsync(string deviceId, CancellationToken cancel)
    {
        WakeWordSettings settings = WakeWordService.GetSettings();
        (string swarmSessionId, string error) = await LoopbackAssistantCaller.Instance.OpenSessionAsync(cancel).ConfigureAwait(false);
        if (swarmSessionId is null)
        {
            throw new InvalidOperationException(error);
        }
        string assistantId = string.IsNullOrWhiteSpace(settings.AssistantId) ? null : settings.AssistantId;
        RemoteTextService text = new(WebServer.PageURL, swarmSessionId, null, assistantId);
        text.Notice += notice => Logs.Debug($"[AudioLab][Session] '{deviceId}': {notice}");

        IDisposable idleHold = await AudioEngineBridge.IdleRelease.BeginAsync(cancel).ConfigureAwait(false);
        VoiceAgentSession voiceSession = null;
        try
        {
            VoiceSessionStartRequest start = new(null, assistantId, null, null, true, 16000);
            VoiceModelLease lease = await VoiceEngineModels.Shared.AcquireAsync(start, text, cancel).ConfigureAwait(false);
            // Tool dispatch has one owner, LLMAssistant; see VoiceSessionEndpoints.
            voiceSession = new VoiceAgentSession(lease.Set, text, new ToolRegistry(), lease.SessionOptions);
            try
            {
                await VoiceEngineModels.Shared.RegisterSession(voiceSession, lease, cancel).ConfigureAwait(false);
            }
            catch
            {
                await lease.Resource.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
            return new EngineSatelliteVoiceSession(voiceSession, idleHold);
        }
        catch
        {
            if (voiceSession is not null)
            {
                try { await voiceSession.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Logs.Debug($"[AudioLab][Session] Disposing a rejected session threw: {ex.Message}"); }
            }
            idleHold.Dispose();
            throw;
        }
    }
}

/// <summary>Adapts a real <see cref="VoiceAgentSession"/> to <see cref="ISatelliteVoiceSession"/>.</summary>
internal sealed class EngineSatelliteVoiceSession : ISatelliteVoiceSession
{
    private readonly VoiceAgentSession _session;
    private readonly IDisposable _idleHold;
    private Action<SatelliteSessionEvent> _handlers;

    public EngineSatelliteVoiceSession(VoiceAgentSession session, IDisposable idleHold)
    {
        _session = session;
        _idleHold = idleHold;
        _session.EventRaised += OnEngineEvent;
    }

    public int OutboundSampleRate => _session.OutboundSampleRate;

    public event Action<SatelliteSessionEvent> EventRaised
    {
        add => _handlers += value;
        remove => _handlers -= value;
    }

    public Task StartAsync(CancellationToken cancel) => _session.StartAsync(cancel);

    public void PushInbound(ReadOnlySpan<float> samples) => _session.PushInbound(samples);

    public int ReadOutbound(Span<float> destination, out int turnId) => _session.ReadOutbound(destination, out turnId);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await VoiceEngineModels.Shared.ReleaseSessionAsync(_session).ConfigureAwait(false);
            _idleHold.Dispose();
        }
    }

    private void OnEngineEvent(VoiceAgentEvent ev)
    {
        SatelliteSessionEvent? mapped = ev.Kind switch
        {
            VoiceAgentEventKind.StateChanged => ev.State switch
            {
                VoiceAgentState.Listening => new SatelliteSessionEvent(SatelliteSessionEventKind.Listening),
                VoiceAgentState.Thinking or VoiceAgentState.ToolRunning => new SatelliteSessionEvent(SatelliteSessionEventKind.Thinking),
                VoiceAgentState.Speaking => new SatelliteSessionEvent(SatelliteSessionEventKind.Speaking),
                VoiceAgentState.Ended => new SatelliteSessionEvent(SatelliteSessionEventKind.Ended),
                _ => null,
            },
            VoiceAgentEventKind.UserTranscript => new SatelliteSessionEvent(SatelliteSessionEventKind.UserTranscript, ev.TurnId, ev.Text),
            VoiceAgentEventKind.AssistantTranscript => new SatelliteSessionEvent(SatelliteSessionEventKind.AssistantTranscript, ev.TurnId, ev.Text),
            VoiceAgentEventKind.BargeIn => new SatelliteSessionEvent(SatelliteSessionEventKind.BargeIn, ev.TurnId),
            VoiceAgentEventKind.ToolCall => new SatelliteSessionEvent(SatelliteSessionEventKind.ToolCall, ev.TurnId, $"{ev.ToolCall?.Name} {ev.ToolCall?.Arguments}"),
            VoiceAgentEventKind.ToolResult => new SatelliteSessionEvent(SatelliteSessionEventKind.ToolResult, ev.TurnId, ev.Text),
            VoiceAgentEventKind.TurnCompleted => new SatelliteSessionEvent(SatelliteSessionEventKind.TurnCompleted, ev.TurnId),
            VoiceAgentEventKind.Error => new SatelliteSessionEvent(SatelliteSessionEventKind.Error, ev.TurnId, ev.Text),
            _ => null,
        };
        if (mapped is SatelliteSessionEvent value)
        {
            _handlers?.Invoke(value);
        }
    }
}
