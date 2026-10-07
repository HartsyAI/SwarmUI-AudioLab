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
/// and the whole release, in-use check included. A request that began first is seen by the release's re-check, and
/// the release does nothing; a request that begins while a release runs waits for it to finish, then loads what it
/// needs afresh. The engine's own generation lock is not enough on its own: an engine release waits for it only so
/// long, then goes ahead. Only <see cref="BeginAsync"/> and the timer wait on that gate; <see cref="Configure"/>,
/// the end of an activity and <see cref="NoteExternalRelease"/> take a separate short lock, so none of them waits
/// for a release that is itself waiting on a model to finish.</para>
///
/// <para>A release that fails (the engine threw, or reported that it could not free) is logged and tried again one
/// period later instead of counting as done, so models it left resident are not forgotten until the next request
/// happens to end.</para>
///
/// <para>Generic and engine-agnostic, like <see cref="Voice.LazyIdleResource{T}"/>: tests drive the state machine
/// with a fake release and an injected delay rather than a live engine.</para></summary>
internal sealed class IdleModelReleaser
{
    /// <summary>The longest idle time <see cref="Configure"/> accepts; anything above is clamped to it.
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> throws beyond about 49.7 days, and the timer's task is
    /// not observed, so an overlong setting would switch idle release off without a word. Thirty days is far
    /// longer than anyone wants an idle model resident.</summary>
    internal static readonly TimeSpan MaxIdleAfter = TimeSpan.FromMinutes(43200);

    private readonly Func<string> _inUse;
    private readonly Func<bool> _release;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;
    private readonly Action<string> _logDetail;
    // Taken by BeginAsync and by the timer, which holds it across the in-use check and the release. Lock order:
    // _gate, then _state.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Guards every field below. Held only to read or write them: never across an await, the in-use check or the
    // release, so Configure, End and NoteExternalRelease cannot be made to wait behind a release.
    private readonly object _state = new();
    private TimeSpan _idleAfter;
    private int _active;
    private long _epoch;
    private bool _usedSinceRelease;
    private CancellationTokenSource _timer;
    private Task _pendingTimer = Task.CompletedTask;

    /// <param name="inUse">Called when the timer fires, under the gate: a short description of what is still using
    /// the models (the release then waits another period), or null when nothing is.</param>
    /// <param name="release">Frees the models and returns whether it did; runs under the gate, so no activity can
    /// begin until it returns. A false return counts as a failed release, like an exception, and is tried again a
    /// period later.</param>
    /// <param name="delay">The idle wait; null for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Called
    /// while the scheduling lock is held, so it must return promptly; a task that is already complete is fine (the
    /// timer yields before going on).</param>
    /// <param name="log">Where a release, or a failed one, is reported; null for nowhere.</param>
    /// <param name="logDetail">Where a release put off because something is still in use is reported, which can
    /// repeat every period for as long as it stays in use; null for nowhere.</param>
    public IdleModelReleaser(Func<string> inUse, Func<bool> release, Func<TimeSpan, CancellationToken, Task> delay = null,
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

    /// <summary>Sets the idle time; zero or less turns idle release off and cancels a pending timer, and anything
    /// above <see cref="MaxIdleAfter"/> is clamped to it. Turning it on while idle starts the wait only when a model
    /// has run since the last release, so restarting the backend does not schedule a release of an engine that
    /// holds nothing. Never waits for a release in progress: a timer started meanwhile is dropped when that release
    /// succeeds, and kept as the retry when it fails.</summary>
    public void Configure(TimeSpan idleAfter)
    {
        lock (_state)
        {
            _idleAfter = idleAfter <= TimeSpan.Zero ? TimeSpan.Zero : idleAfter > MaxIdleAfter ? MaxIdleAfter : idleAfter;
            CancelTimerLocked();
            if (_idleAfter > TimeSpan.Zero && _active == 0 && _usedSinceRelease)
            {
                ScheduleLocked();
            }
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
            lock (_state)
            {
                _active++;
                _epoch++;
                _usedSinceRelease = true;
                CancelTimerLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
        return new Activity(this);
    }

    /// <summary>Runs <paramref name="work"/> as one activity: <see cref="BeginAsync"/> first, and ended when the work
    /// finishes, normally or not. For callers that wrap a single awaited operation, such as the wake listener's
    /// transcription hook.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(work);
        using IDisposable activity = await BeginAsync(cancel).ConfigureAwait(false);
        return await work().ConfigureAwait(false);
    }

    /// <summary>Tells the releaser that something other than its own timer just freed the models (an explicit unload,
    /// the backend's free-memory call). A pending timer would only repeat that release, so it is cancelled and
    /// nothing is scheduled until a model runs again. Ignored while an activity is open: it may already have loaded
    /// something since.</summary>
    public void NoteExternalRelease()
    {
        lock (_state)
        {
            if (_active > 0)
            {
                return;
            }
            CancelTimerLocked();
            _usedSinceRelease = false;
        }
    }

    /// <summary>Ends one activity. Never waits behind a release: one only runs while no activity is open, and every
    /// activity's end is its own, so there is none to end while it does.</summary>
    private void End()
    {
        lock (_state)
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
    }

    /// <summary>Starts a fresh timer, superseding any earlier one. Caller must hold <see cref="_state"/>.</summary>
    private void ScheduleLocked()
    {
        CancelTimerLocked();
        _epoch++;
        CancellationTokenSource timer = new();
        _timer = timer;
        Volatile.Write(ref _pendingTimer, RunTimerAsync(_epoch, _idleAfter, timer));
    }

    /// <summary>Cancels the pending timer, if any. The epoch is left alone: a timer already past its delay is not
    /// reached by this, and has to find for itself that it is stale. Caller must hold <see cref="_state"/>.</summary>
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

    /// <summary>Puts the timer back for another full period when idle release is still on and nothing is active.
    /// Returns that period, or zero when it did not.</summary>
    private TimeSpan Reschedule()
    {
        lock (_state)
        {
            if (_active > 0 || _idleAfter <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }
            ScheduleLocked();
            return _idleAfter;
        }
    }

    private async Task RunTimerAsync(long epoch, TimeSpan idleAfter, CancellationTokenSource timer)
    {
        try
        {
            Task wait = _delay(idleAfter, timer.Token);
            if (wait.IsCompleted)
            {
                // A delay that completes at once (a sub-millisecond one does) would run everything below inside
                // the lock of whoever scheduled this timer.
                await Task.Yield();
            }
            await wait.ConfigureAwait(false);
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
            lock (_state)
            {
                // The epoch moves on every begin and every reschedule, so a match means nothing started since this
                // timer was set and it is still the current one. With the gate held from here on, nothing can
                // start until the release is done. And an explicit release in the meantime leaves nothing to free.
                if (epoch != _epoch || _active > 0 || _idleAfter <= TimeSpan.Zero || !_usedSinceRelease)
                {
                    return;
                }
                CancelTimerLocked();
            }
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
                TimeSpan again = Reschedule();
                _logDetail(again > TimeSpan.Zero
                    ? $"[AudioLab] Audio models idle for {Describe(idleAfter)} but kept: {busy}. Checking again in {Describe(again)}."
                    : $"[AudioLab] Audio models idle for {Describe(idleAfter)} but kept: {busy}.");
                return;
            }
            string failure = null;
            try
            {
                if (!_release())
                {
                    failure = "the engine could not free them";
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            if (failure is null)
            {
                lock (_state)
                {
                    // Nothing is resident now. A timer set during the release (a Configure while it ran) would only
                    // repeat it.
                    _usedSinceRelease = false;
                    CancelTimerLocked();
                }
                _log($"[AudioLab] Released the resident audio models after {Describe(idleAfter)} without an audio request.");
                return;
            }
            // Whatever the release left resident is still there: keep the flag, and look again in a period.
            TimeSpan retry = Reschedule();
            _log(retry > TimeSpan.Zero
                ? $"[AudioLab] Releasing idle audio models failed: {failure}. Trying again in {Describe(retry)}."
                : $"[AudioLab] Releasing idle audio models failed: {failure}.");
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
