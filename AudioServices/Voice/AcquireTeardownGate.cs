namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>Serializes "acquire the shared resource" against "end every active session and reset the resource"
/// so the two can never interleave.
///
/// <para>Without this, a session whose acquire has already returned a lease -- but has not yet reached whatever
/// registers it as "active" -- is invisible to a teardown's active-session snapshot. The teardown then resets
/// the resource out from under that session, which was never told to end. Wrapping <em>both</em> the whole
/// acquire (lease fetch included, not just the bookkeeping around it) and the whole teardown in the same mutual
/// exclusion narrows that window considerably, but does not by itself close it: this gate is released as soon
/// as the acquire callback returns, and registering "active" is necessarily a separate call the caller makes
/// afterward (it needs the lease's own contents first, eg to construct the thing being registered). A teardown
/// can still land in the gap between the two. Closing it needs both halves: the registration call must itself
/// run inside this same gate (another <see cref="AcquireAsync{T}"/>, not a bare dictionary write), and it must
/// re-check that what it is registering is still live -- a reference-equality check of the specific value the
/// lease was handed against the resource's current one is enough, since a teardown that force-disposed the
/// resource in between leaves <c>Current</c> pointing at something else (null, or a later rebuild), never the
/// same instance. See <c>VoiceEngineModels.RegisterSession</c> for the concrete version of this. What this gate
/// alone guarantees is narrower: a teardown that starts while an acquire is in flight waits for it to finish
/// (so the acquired lease is real and stable before the teardown's snapshot runs), and an acquire that starts
/// while a teardown is running waits for the teardown (so it is handed a freshly reset resource rather than one
/// about to be torn down). Generic and resource-agnostic on purpose, same reason as
/// <see cref="LazyIdleResource{T}"/>: the real session/model-set types involved are sealed with internal
/// constructors and need a live engine, so the mutual-exclusion pattern itself is what a test can actually
/// drive.</para></summary>
internal sealed class AcquireTeardownGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs <paramref name="acquire"/> with exclusive access, waiting out any <see cref="TeardownAsync"/>
    /// already in progress first.</summary>
    public async Task<T> AcquireAsync<T>(Func<Task<T>> acquire, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return await acquire().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Non-generic form of <see cref="AcquireAsync{T}"/>, for a critical section with nothing to
    /// return -- eg a late registration step that only needs to run, and possibly throw, under the same
    /// exclusion a lease fetch did.</summary>
    public async Task AcquireAsync(Func<Task> acquire, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await acquire().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs <paramref name="teardown"/> with exclusive access, waiting out any <see cref="AcquireAsync{T}"/>
    /// already in progress first -- so by the time <paramref name="teardown"/>'s own snapshot of active sessions
    /// runs, every acquire that had already returned a lease is reflected in it, and no new acquire can start
    /// until this finishes.</summary>
    public async Task TeardownAsync(Func<Task> teardown)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await teardown().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
