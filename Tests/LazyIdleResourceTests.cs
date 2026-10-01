using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="LazyIdleResource{T}"/>'s lazy-create / idle-dispose / force-dispose / rebuild
/// state machine, driven entirely with a fake disposable and an injected delay seam -- the real
/// <c>HartsyInference.Voice.VoiceModelSet</c> this backs in production is sealed with an internal constructor and
/// needs a live engine, so this is the only place that state machine can be exercised at all.</summary>
public class LazyIdleResourceTests
{
    private sealed class FakeResource
    {
        public int DisposeCount;
    }

    /// <param name="delay">Replaces the real idle-timer wait; null keeps the real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
    /// which every test below avoids actually letting fire (either by never idling, or by passing its own fast
    /// seam) so the suite stays instant.</param>
    private static LazyIdleResource<FakeResource> Make(out int[] created, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        int[] createdCounter = [0];
        created = createdCounter;
        return new LazyIdleResource<FakeResource>(
            factory: _ =>
            {
                createdCounter[0]++;
                return Task.FromResult(new FakeResource());
            },
            disposer: r =>
            {
                r.DisposeCount++;
                return ValueTask.CompletedTask;
            },
            idleDelay: TimeSpan.FromMinutes(5),
            delay: delay);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatesOnlyOnce_ForConcurrentCallers()
    {
        LazyIdleResource<FakeResource> resource = Make(out int[] created);
        FakeResource a = await resource.GetOrCreateAsync(CancellationToken.None);
        FakeResource b = await resource.GetOrCreateAsync(CancellationToken.None);
        Assert.Same(a, b);
        Assert.Equal(1, created[0]);
        Assert.Equal(2, resource.ActiveCount);
    }

    [Fact]
    public async Task ReleaseAsync_DecrementsActiveCount_AndNeverGoesNegative()
    {
        LazyIdleResource<FakeResource> resource = Make(out _, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await resource.ReleaseAsync(); // nothing acquired yet
        Assert.Equal(0, resource.ActiveCount);
        await resource.GetOrCreateAsync(CancellationToken.None);
        await resource.ReleaseAsync();
        await resource.ReleaseAsync(); // one extra release
        Assert.Equal(0, resource.ActiveCount);
    }

    [Fact]
    public async Task IdleTimer_DisposesAfterTheConfiguredDelay_OnceActiveCountReachesZero()
    {
        TimeSpan? sawDelay = null;
        LazyIdleResource<FakeResource> resource = Make(out _, delay: (span, ct) => { sawDelay = span; return Task.Delay(5, ct); });
        FakeResource value = await resource.GetOrCreateAsync(CancellationToken.None);
        await resource.ReleaseAsync();
        // The timer is a fire-and-forget Task started from ReleaseAsync; give it a moment to run its 5ms delay.
        await Task.Delay(200);
        Assert.Equal(TimeSpan.FromMinutes(5), sawDelay);
        Assert.Equal(1, value.DisposeCount);
        Assert.Null(resource.Current);
    }

    [Fact]
    public async Task IdleTimer_IsCancelled_ByANewGetOrCreateAsyncBeforeItFires()
    {
        LazyIdleResource<FakeResource> resource = Make(out int[] created, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        FakeResource first = await resource.GetOrCreateAsync(CancellationToken.None);
        await resource.ReleaseAsync(); // starts an idle timer that will never fire (infinite delay)
        FakeResource second = await resource.GetOrCreateAsync(CancellationToken.None); // cancels that timer
        Assert.Same(first, second);
        Assert.Equal(1, created[0]); // never recreated
        Assert.Equal(0, first.DisposeCount);
    }

    [Fact]
    public async Task ForceDisposeAsync_DisposesImmediately_RegardlessOfActiveCount()
    {
        LazyIdleResource<FakeResource> resource = Make(out int[] created, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        FakeResource value = await resource.GetOrCreateAsync(CancellationToken.None); // ActiveCount 1, never released
        await resource.ForceDisposeAsync();
        Assert.Equal(1, value.DisposeCount);
        Assert.Equal(0, resource.ActiveCount);
        Assert.Null(resource.Current);
        Assert.Equal(1, created[0]);
    }

    [Fact]
    public async Task ARebuiltResource_CreatesAFreshValue_AfterForceDispose()
    {
        LazyIdleResource<FakeResource> resource = Make(out int[] created);
        FakeResource first = await resource.GetOrCreateAsync(CancellationToken.None);
        await resource.ForceDisposeAsync();
        FakeResource second = await resource.GetOrCreateAsync(CancellationToken.None);
        Assert.NotSame(first, second);
        Assert.Equal(2, created[0]);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
    }

    [Fact]
    public async Task AFailedFactory_LeavesTheResourceEmpty_ForTheNextCallerToRetry()
    {
        int attempts = 0;
        LazyIdleResource<FakeResource> resource = new(
            factory: _ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<FakeResource>(new InvalidOperationException("boom"))
                    : Task.FromResult(new FakeResource());
            },
            disposer: _ => ValueTask.CompletedTask,
            idleDelay: TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.GetOrCreateAsync(CancellationToken.None));
        Assert.Equal(0, resource.ActiveCount);
        Assert.Null(resource.Current);
        FakeResource value = await resource.GetOrCreateAsync(CancellationToken.None);
        Assert.NotNull(value);
        Assert.Equal(2, attempts);
    }
}
