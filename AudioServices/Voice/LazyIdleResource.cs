namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>A lazily created, shared, async-disposable resource that outlives any one caller.
///
/// <para>Created on the first <see cref="GetOrCreateAsync"/> after being empty; reference-counted while callers
/// hold it (<see cref="GetOrCreateAsync"/> / <see cref="ReleaseAsync"/> must be paired); disposed automatically
/// once idle for <c>idleDelay</c> after the last release, or on demand via <see cref="ForceDisposeAsync"/>, which
/// also cancels a pending idle timer. A disposed resource is rebuilt lazily on the next
/// <see cref="GetOrCreateAsync"/> -- never reused once disposed.</para>
///
/// <para>Generic and engine-agnostic on purpose: the one thing under test here is the create/idle-dispose/
/// force-dispose/rebuild state machine, which a real <c>VoiceModelSet</c> (sealed, internal constructor, needs a
/// live engine and model weights) cannot exercise in a unit test. Production code supplies the real factory and
/// disposer; tests supply trivial ones and an injectable <paramref name="delay"/> seam instead of waiting out a
/// real idle window.</para></summary>
internal sealed class LazyIdleResource<T> where T : class
{
    private readonly Func<CancellationToken, Task<T>> _factory;
    private readonly Func<T, ValueTask> _disposer;
    private readonly TimeSpan _idleDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T _value;
    private int _activeCount;
    private CancellationTokenSource _idleTimerCancel;

    public LazyIdleResource(Func<CancellationToken, Task<T>> factory, Func<T, ValueTask> disposer, TimeSpan idleDelay,
        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));
        _idleDelay = idleDelay;
        _delay = delay ?? ((span, cancel) => Task.Delay(span, cancel));
    }

    /// <summary>Holders that have called <see cref="GetOrCreateAsync"/> without a matching <see cref="ReleaseAsync"/> yet.</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>The current value without creating one, for diagnostics/tests; null when not created or disposed.</summary>
    public T Current => _value;

    /// <summary>Returns the shared value, creating it via the factory if none exists. A failed create leaves the
    /// resource empty for the next caller to retry -- nothing here loops or caches a failure. Pairs with
    /// <see cref="ReleaseAsync"/>: every successful call here must be matched by exactly one release once the
    /// caller is done using the value.</summary>
    public async Task<T> GetOrCreateAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            CancelIdleTimerLocked();
            _value ??= await _factory(cancel).ConfigureAwait(false);
            _activeCount++;
            return _value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases one hold taken by <see cref="GetOrCreateAsync"/>. When the count reaches zero, schedules
    /// disposal after the idle delay unless a new <see cref="GetOrCreateAsync"/> or an explicit
    /// <see cref="ForceDisposeAsync"/> cancels it first.</summary>
    public async Task ReleaseAsync()
    {
        CancellationTokenSource started = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeCount > 0)
            {
                _activeCount--;
            }
            if (_activeCount == 0 && _value is not null)
            {
                CancelIdleTimerLocked();
                _idleTimerCancel = new CancellationTokenSource();
                started = _idleTimerCancel;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (started is not null)
        {
            _ = RunIdleTimerAsync(started);
        }
    }

    /// <summary>Drops the current value immediately, regardless of <see cref="ActiveCount"/>, and cancels any
    /// pending idle timer. Callers that track active sessions of their own (eg an engine-release hook) must end
    /// those first -- this neither waits for nor checks that; it only tears down the shared value itself.</summary>
    public async Task ForceDisposeAsync()
    {
        T toDispose;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleTimerLocked();
            toDispose = _value;
            _value = null;
            _activeCount = 0;
        }
        finally
        {
            _gate.Release();
        }
        if (toDispose is not null)
        {
            await _disposer(toDispose).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels and clears the pending idle timer, if any. Caller must hold <see cref="_gate"/>.</summary>
    private void CancelIdleTimerLocked()
    {
        _idleTimerCancel?.Cancel();
        _idleTimerCancel?.Dispose();
        _idleTimerCancel = null;
    }

    private async Task RunIdleTimerAsync(CancellationTokenSource owned)
    {
        try
        {
            await _delay(_idleDelay, owned.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new GetOrCreateAsync or an explicit ForceDisposeAsync; whichever ran it already
            // handled (or will handle) disposal, so this timer has nothing left to do.
            return;
        }
        T toDispose = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Reaching here means the delay ran to completion without owned.Token firing, which only
            // CancelIdleTimerLocked ever does -- so _idleTimerCancel still names this exact timer and nothing
            // reopened the resource behind our back. The reference check is defensive belt-and-suspenders.
            if (ReferenceEquals(_idleTimerCancel, owned) && _activeCount == 0)
            {
                toDispose = _value;
                _value = null;
                _idleTimerCancel = null;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (toDispose is not null)
        {
            await _disposer(toDispose).ConfigureAwait(false);
        }
    }
}
