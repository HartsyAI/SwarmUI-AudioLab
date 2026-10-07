namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Releases AudioLab's resident audio models once nothing has used them for a configured time.
///
/// <para>After a burst of audio work the engine keeps every model it loaded resident, which on a shared card can
/// leave SwarmUI's image backend no room for its next generation until core's own idle VRAM clear runs, minutes
/// later. This frees the audio side sooner: every AudioLab entry point that runs a model holds an activity from
/// <see cref="BeginAsync"/> until it is done, and once the last one ends and nothing begins for the configured
/// time, <c>release</c> runs.</para>
///
/// <para>Two things are never released from under: anything still holding an activity, and whatever
/// <c>inUse</c> names when the timer fires (a model kept resident on purpose, an open voice session). A busy check
/// just puts the timer back for another full period.</para>
///
/// <para><b>No race with a request starting at the same moment.</b> One gate covers both the begin of an activity
/// and the whole release, re-check included. A request that began first is seen by the release's re-check, and the
/// release does nothing; a request that begins while a release runs waits for it to finish, then loads what it needs
/// afresh. The engine's own generation lock is not enough on its own: an engine release waits for it only so long,
/// then goes ahead.</para>
///
/// <para>Generic and engine-agnostic, like <see cref="Voice.LazyIdleResource{T}"/>: tests drive the state machine
/// with a fake release and an injected delay rather than a live engine.</para></summary>
internal sealed class IdleModelReleaser
{
    private readonly Func<string> _inUse;
    private readonly Action _release;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;
    private readonly Action<string> _logDetail;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TimeSpan _idleAfter;
    private int _active;
    private long _epoch;
    private bool _usedSinceRelease;
    private CancellationTokenSource _timer;
    private Task _pendingTimer = Task.CompletedTask;

    /// <param name="inUse">Called when the timer fires, under the gate: a short description of what is still using
    /// the models (the release then waits another period), or null when nothing is.</param>
    /// <param name="release">Frees the models; runs under the gate, so no activity can begin until it returns.</param>
    /// <param name="delay">The idle wait; null for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="log">Where a release, or a failed one, is reported; null for nowhere.</param>
    /// <param name="logDetail">Where a release put off because something is still in use is reported, which can
    /// repeat every period for as long as it stays in use; null for nowhere.</param>
    public IdleModelReleaser(Func<string> inUse, Action release, Func<TimeSpan, CancellationToken, Task> delay = null,
        Action<string> log = null, Action<string> logDetail = null)
    {
        _inUse = inUse ?? throw new ArgumentNullException(nameof(inUse));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _delay = delay ?? ((span, cancel) => Task.Delay(span, cancel));
        _log = log ?? (_ => { });
        _logDetail = logDetail ?? (_ => { });
    }

    /// <summary>Activities begun and not yet ended.</summary>
    internal int ActiveCount => Volatile.Read(ref _active);

    /// <summary>The most recently scheduled timer, which completes once it has fired (released, skipped or found
    /// itself superseded) or been cancelled. For tests.</summary>
    internal Task PendingTimer => Volatile.Read(ref _pendingTimer);

    /// <summary>Sets the idle time; zero or less turns idle release off and cancels a pending timer. Turning it on
    /// while idle starts the wait only when a model has run since the last release, so restarting the backend does
    /// not schedule a release of an engine that holds nothing.</summary>
    public void Configure(TimeSpan idleAfter)
    {
        _gate.Wait();
        try
        {
            _idleAfter = idleAfter > TimeSpan.Zero ? idleAfter : TimeSpan.Zero;
            CancelTimerLocked();
            if (_idleAfter > TimeSpan.Zero && _active == 0 && _usedSinceRelease)
            {
                ScheduleLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks the start of work that uses the audio models and cancels a pending release. Waits for a release
    /// already running to finish first. Dispose the result exactly when the work is done: the release timer starts
    /// when the last activity ends.</summary>
    public async Task<IDisposable> BeginAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            _active++;
            _epoch++;
            _usedSinceRelease = true;
            CancelTimerLocked();
        }
        finally
        {
            _gate.Release();
        }
        return new Activity(this);
    }

    /// <summary>Ends one activity. Never waits behind a release: one only runs while no activity is open.</summary>
    private void End()
    {
        _gate.Wait();
        try
        {
            if (_active > 0)
            {
                _active--;
            }
            if (_active == 0 && _idleAfter > TimeSpan.Zero)
            {
                ScheduleLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a fresh timer, superseding any earlier one. Caller must hold <see cref="_gate"/>.</summary>
    private void ScheduleLocked()
    {
        CancelTimerLocked();
        _epoch++;
        CancellationTokenSource timer = new();
        _timer = timer;
        Volatile.Write(ref _pendingTimer, RunTimerAsync(_epoch, _idleAfter, timer));
    }

    /// <summary>Cancels the pending timer, if any. Caller must hold <see cref="_gate"/>.</summary>
    private void CancelTimerLocked()
    {
        if (_timer is null)
        {
            return;
        }
        _timer.Cancel();
        _timer.Dispose();
        _timer = null;
    }

    private async Task RunTimerAsync(long epoch, TimeSpan idleAfter, CancellationTokenSource timer)
    {
        try
        {
            await _delay(idleAfter, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // An activity began, the timer was rescheduled, or the setting changed; whichever did it owns what
            // happens next.
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The epoch moves on every begin and every reschedule, so a match means nothing started since this
            // timer was set and it is still the current one.
            if (epoch != _epoch || _active > 0 || _idleAfter <= TimeSpan.Zero)
            {
                return;
            }
            CancelTimerLocked();
            string busy;
            try
            {
                busy = _inUse();
            }
            catch (Exception ex)
            {
                // Unknown is not idle: keep the models and look again next period rather than stop checking.
                busy = $"the in-use check failed ({ex.Message})";
            }
            if (busy is not null)
            {
                _logDetail($"[AudioLab] Audio models idle for {Describe(idleAfter)} but kept: {busy}. Checking again in {Describe(idleAfter)}.");
                ScheduleLocked();
                return;
            }
            try
            {
                _release();
                _log($"[AudioLab] Released the resident audio models after {Describe(idleAfter)} without an audio request.");
            }
            catch (Exception ex)
            {
                _log($"[AudioLab] Releasing idle audio models failed: {ex.Message}");
            }
            _usedSinceRelease = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Describe(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes:0.#} min"
        : $"{span.TotalSeconds:0.#} s";

    private sealed class Activity(IdleModelReleaser owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.End();
            }
        }
    }
}
