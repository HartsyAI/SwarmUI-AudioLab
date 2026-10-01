using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Deterministic race tests for <see cref="AcquireTeardownGate"/>, coordinated entirely with
/// <see cref="TaskCompletionSource"/> signals -- no sleeps, no timing assumptions. Each test proves one
/// direction of the mutual exclusion the race this gate closes (see its own remarks) depends on.</summary>
public class AcquireTeardownGateTests
{
    [Fact]
    public async Task TeardownInProgress_BlocksAConcurrentAcquire_UntilTeardownCompletes()
    {
        AcquireTeardownGate gate = new();
        TaskCompletionSource teardownEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTeardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool acquireRan = false;

        // Start a teardown and let it run up to (and block on) releaseTeardown, holding the gate the whole time.
        Task teardownTask = gate.TeardownAsync(async () =>
        {
            teardownEntered.SetResult();
            await releaseTeardown.Task;
        });
        await teardownEntered.Task;

        // Start a concurrent acquire. Its first await is the gate itself, which teardown is holding -- the
        // returned task cannot have completed yet, deterministically (SemaphoreSlim.WaitAsync on a held
        // semaphore never completes synchronously), so checking IsCompleted here is not a race.
        Task<int> acquireTask = gate.AcquireAsync(() =>
        {
            acquireRan = true;
            return Task.FromResult(42);
        }, CancellationToken.None);
        Assert.False(acquireTask.IsCompleted);
        Assert.False(acquireRan);

        // Release the teardown; only now may the acquire proceed.
        releaseTeardown.SetResult();
        int result = await acquireTask;
        Assert.Equal(42, result);
        Assert.True(acquireRan);
        await teardownTask;
    }

    [Fact]
    public async Task AcquireInProgress_BlocksAConcurrentTeardown_UntilTheAcquireCompletes()
    {
        AcquireTeardownGate gate = new();
        TaskCompletionSource acquireEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAcquire = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool teardownRan = false;

        Task<int> acquireTask = gate.AcquireAsync(async () =>
        {
            acquireEntered.SetResult();
            await releaseAcquire.Task;
            return 7;
        }, CancellationToken.None);
        await acquireEntered.Task;

        // A teardown racing this acquire must wait for it: by the time the teardown's own snapshot runs, the
        // lease the acquire is fetching is either fully granted (and so can be seen/ended properly next time)
        // or the acquire has not even started touching shared state yet -- never caught in between.
        Task teardownTask = gate.TeardownAsync(() =>
        {
            teardownRan = true;
            return Task.CompletedTask;
        });
        Assert.False(teardownTask.IsCompleted);
        Assert.False(teardownRan);

        releaseAcquire.SetResult();
        int result = await acquireTask;
        Assert.Equal(7, result);
        await teardownTask;
        Assert.True(teardownRan);
    }

    [Fact]
    public async Task SequentialAcquiresAndTeardowns_AllRunToCompletion_InNoParticularOrderButNeverOverlapping()
    {
        AcquireTeardownGate gate = new();
        int concurrentCount = 0;
        int maxObservedConcurrency = 0;
        object sync = new();

        async Task<int> Enter(int value)
        {
            lock (sync)
            {
                concurrentCount++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCount);
            }
            await Task.Yield();
            lock (sync)
            {
                concurrentCount--;
            }
            return value;
        }

        Task<int>[] acquires =
        [
            gate.AcquireAsync(() => Enter(1), CancellationToken.None),
            gate.AcquireAsync(() => Enter(2), CancellationToken.None),
        ];
        Task teardown = gate.TeardownAsync(() => Enter(0).ContinueWith(_ => { }));
        await Task.WhenAll([.. acquires, teardown]);

        Assert.Equal(1, maxObservedConcurrency);
    }

    private sealed class FakeValue
    {
        public bool Disposed;
    }

    /// <summary>These two tests drive the same combined pattern <c>VoiceEngineModels.RegisterSession</c> uses in
    /// production -- a <see cref="LazyIdleResource{T}"/> behind this gate, and a second gated call later that
    /// re-checks the specific value an earlier gated call was handed against <see cref="LazyIdleResource{T}.Current"/>
    /// -- rather than <see cref="AcquireTeardownGate"/> alone. The gate alone only proves the earlier tests' mutual
    /// exclusion; it says nothing about a teardown that runs in the gap between two separate gated calls, which is
    /// exactly the gap between <c>VoiceEngineModels.AcquireAsync</c> returning and the caller reaching
    /// <c>RegisterSession</c> (see both methods' own remarks). <c>VoiceAgentSession</c>/<c>VoiceModelSet</c> are
    /// sealed with internal constructors and cannot be faked, so <see cref="FakeValue"/> and a real
    /// <see cref="LazyIdleResource{T}"/> stand in for them here, same as <see cref="LazyIdleResourceTests"/>.</summary>
    [Fact]
    public async Task LateRegistration_RecheckedAgainstCurrent_RejectsAValueATeardownDisposedInTheGap()
    {
        AcquireTeardownGate gate = new();
        LazyIdleResource<FakeValue> resource = new(
            factory: _ => Task.FromResult(new FakeValue()),
            disposer: v => { v.Disposed = true; return ValueTask.CompletedTask; },
            idleDelay: TimeSpan.FromMinutes(5));

        // The "acquire": gets a lease, then (as AcquireAsync does) releases the gate before the caller does
        // anything else with it.
        FakeValue leased = await gate.AcquireAsync(() => resource.GetOrCreateAsync(CancellationToken.None), CancellationToken.None);

        // A teardown lands in the gap before this caller reaches its own registration step -- the exact gap the
        // gate's own remarks describe. ForceDisposeAsync resets the resource to empty rather than poisoning it,
        // so `resource` itself is still the same instance; only what it currently holds has changed.
        await gate.TeardownAsync(() => resource.ForceDisposeAsync());
        Assert.True(leased.Disposed);

        // The late "register": same re-check RegisterSession makes, under the same gate.
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.AcquireAsync(() =>
        {
            if (!ReferenceEquals(leased, resource.Current))
            {
                throw new InvalidOperationException("stale");
            }
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal("stale", thrown.Message);
    }

    [Fact]
    public async Task LateRegistration_RecheckedAgainstCurrent_SucceedsWhenNoTeardownRanInTheGap()
    {
        AcquireTeardownGate gate = new();
        LazyIdleResource<FakeValue> resource = new(
            factory: _ => Task.FromResult(new FakeValue()),
            disposer: v => { v.Disposed = true; return ValueTask.CompletedTask; },
            idleDelay: TimeSpan.FromMinutes(5));

        FakeValue leased = await gate.AcquireAsync(() => resource.GetOrCreateAsync(CancellationToken.None), CancellationToken.None);

        bool registered = false;
        await gate.AcquireAsync(() =>
        {
            if (ReferenceEquals(leased, resource.Current))
            {
                registered = true;
            }
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(registered);
        Assert.False(leased.Disposed);
    }
}
