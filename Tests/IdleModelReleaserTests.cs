using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="IdleModelReleaser"/>, the idle-release state machine behind the backend's
/// "Unload Idle Models After Minutes" setting. Driven with a fake release and a delay seam the test completes by hand,
/// so nothing here waits on a clock: each step awaits the timer it just let fire.</summary>
public class IdleModelReleaserTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    /// <summary>A delay seam that hands back one pending task per call, completed only when the test fires it.</summary>
    private sealed class ManualDelays
    {
        private readonly object _lock = new();
        private readonly List<(TimeSpan Span, TaskCompletionSource Done)> _calls = [];

        /// <param name="honourCancel">False makes a cancelled wait still complete when fired, which is what a timer
        /// whose cancellation lost a race looks like; the releaser must then notice it was superseded on its own.</param>
        public ManualDelays(bool honourCancel = true) => HonourCancel = honourCancel;

        public bool HonourCancel { get; }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _calls.Count;
                }
            }
        }

        public TimeSpan SpanOf(int index)
        {
            lock (_lock)
            {
                return _calls[index].Span;
            }
        }

        public Task Delay(TimeSpan span, CancellationToken cancel)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (HonourCancel)
            {
                cancel.Register(() => done.TrySetCanceled(cancel));
            }
            lock (_lock)
            {
                _calls.Add((span, done));
            }
            return done.Task;
        }

        public void Fire(int index)
        {
            TaskCompletionSource done;
            lock (_lock)
            {
                done = _calls[index].Done;
            }
            done.TrySetResult();
        }
    }

    private sealed class Harness
    {
        public int Releases;
        public string Busy;
        public List<string> Log { get; } = [];
        public List<string> Detail { get; } = [];
        public ManualDelays Delays { get; }
        public IdleModelReleaser Releaser { get; }

        public Harness(ManualDelays delays = null, Action release = null)
        {
            Delays = delays ?? new ManualDelays();
            Releaser = new IdleModelReleaser(
                inUse: () => Busy,
                release: release ?? (() => Interlocked.Increment(ref Releases)),
                delay: Delays.Delay,
                log: Log.Add,
                logDetail: Detail.Add);
        }

        /// <summary>One request, begun and finished.</summary>
        public async Task RunRequestAsync()
        {
            using IDisposable activity = await Releaser.BeginAsync(CancellationToken.None);
        }

        /// <summary>Lets timer <paramref name="index"/> fire and waits until it has finished acting on it.</summary>
        public async Task FireAsync(int index)
        {
            Task pending = Releaser.PendingTimer;
            Delays.Fire(index);
            await pending;
        }
    }

    [Fact]
    public async Task ReleasesOnce_AfterTheConfiguredIdleTime()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);

        await h.RunRequestAsync();

        Assert.Equal(1, h.Delays.Count);
        Assert.Equal(Idle, h.Delays.SpanOf(0));
        Assert.Equal(0, h.Releases);
        await h.FireAsync(0);
        Assert.Equal(1, h.Releases);
        Assert.Single(h.Log);
        // Nothing is resident any more, so nothing is scheduled until the next request ends.
        Assert.Equal(1, h.Delays.Count);
    }

    [Fact]
    public async Task ANewRequest_ResetsTheTimer()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task first = h.Releaser.PendingTimer;

        await h.RunRequestAsync();
        await first; // cancelled by the second request's begin

        Assert.Equal(2, h.Delays.Count);
        h.Delays.Fire(0);
        Assert.Equal(0, h.Releases);
        await h.FireAsync(1);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task ATimerThatFiresAfterARequestBegan_DoesNothing_EvenIfItsCancellationLostTheRace()
    {
        Harness h = new(new ManualDelays(honourCancel: false));
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task stale = h.Releaser.PendingTimer;

        using (IDisposable activity = await h.Releaser.BeginAsync(CancellationToken.None))
        {
            // The first timer's wait completes while a request is running: it must see that and stand down.
            h.Delays.Fire(0);
            await stale;
            Assert.Equal(0, h.Releases);
        }

        // Ending that request started a fresh timer; the stale one is still not allowed to act for it.
        Assert.Equal(2, h.Delays.Count);
        await h.FireAsync(1);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task WhileSomethingIsInUse_TheReleaseIsSkippedAndCheckedAgainNextPeriod()
    {
        Harness h = new() { Busy = "a model is kept resident by Keep Tts Stt Resident" };
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();

        await h.FireAsync(0);

        Assert.Equal(0, h.Releases);
        Assert.Equal(2, h.Delays.Count);
        Assert.Contains("Keep Tts Stt Resident", Assert.Single(h.Detail));

        await h.FireAsync(1);
        Assert.Equal(0, h.Releases);
        Assert.Equal(3, h.Delays.Count);

        h.Busy = null;
        await h.FireAsync(2);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task AnOpenRequest_NeverStartsTheTimer_ASlowStreamIncluded()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);

        IDisposable stream = await h.Releaser.BeginAsync(CancellationToken.None);
        await h.RunRequestAsync(); // a second, short request ends while the stream is still open
        h.Releaser.Configure(Idle); // re-applying the setting does not start one either

        Assert.Equal(0, h.Delays.Count);
        Assert.Equal(1, h.Releaser.ActiveCount);

        stream.Dispose();
        Assert.Equal(1, h.Delays.Count);
        await h.FireAsync(0);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task ARequestThatStartsDuringARelease_WaitsForIt()
    {
        using ManualResetEventSlim releaseStarted = new(false);
        using ManualResetEventSlim finishRelease = new(false);
        List<string> order = [];
        Harness h = new(release: () =>
        {
            releaseStarted.Set();
            finishRelease.Wait();
            lock (order)
            {
                order.Add("release finished");
            }
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();

        Task timer = h.Releaser.PendingTimer;
        h.Delays.Fire(0);
        Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(30)), "the release never started");

        Task<IDisposable> begin = h.Releaser.BeginAsync(CancellationToken.None);
        // The release holds the gate, so the request cannot begin however long it is given. A bounded wait only
        // keeps the test short: a request that is not held back begins within microseconds.
        Assert.NotSame(begin, await Task.WhenAny(begin, Task.Delay(TimeSpan.FromMilliseconds(250))));

        finishRelease.Set();
        await timer;
        using (IDisposable activity = await begin)
        {
            lock (order)
            {
                order.Add("request began");
            }
        }

        Assert.Equal(["release finished", "request began"], order);
    }

    [Fact]
    public async Task ARequestWaitingOnARelease_CanBeCancelled()
    {
        using ManualResetEventSlim releaseStarted = new(false);
        using ManualResetEventSlim finishRelease = new(false);
        Harness h = new(release: () =>
        {
            releaseStarted.Set();
            finishRelease.Wait();
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task timer = h.Releaser.PendingTimer;
        h.Delays.Fire(0);
        Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(30)), "the release never started");

        using CancellationTokenSource cancel = new();
        Task<IDisposable> begin = h.Releaser.BeginAsync(cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
        finishRelease.Set();
        await timer;
        Assert.Equal(0, h.Releaser.ActiveCount);
    }

    [Fact]
    public async Task Off_NeverSchedules_AndTurningItOffCancelsAPendingTimer()
    {
        Harness h = new();
        h.Releaser.Configure(TimeSpan.Zero);
        await h.RunRequestAsync();
        Assert.Equal(0, h.Delays.Count);

        // Turned on while idle after a request ran: the wait starts now.
        h.Releaser.Configure(Idle);
        Assert.Equal(1, h.Delays.Count);
        Task pending = h.Releaser.PendingTimer;

        h.Releaser.Configure(TimeSpan.FromMinutes(-1)); // negative is off, too
        await pending;
        h.Delays.Fire(0);
        Assert.Equal(0, h.Releases);
    }

    [Fact]
    public void TurningItOn_WithNothingRunSinceTheLastRelease_SchedulesNothing()
    {
        Harness h = new();

        h.Releaser.Configure(Idle);

        Assert.Equal(0, h.Delays.Count);
    }

    [Fact]
    public async Task EndingAnActivityTwice_EndsItOnce()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        IDisposable first = await h.Releaser.BeginAsync(CancellationToken.None);
        IDisposable second = await h.Releaser.BeginAsync(CancellationToken.None);

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, h.Releaser.ActiveCount);
        Assert.Equal(0, h.Delays.Count);
        second.Dispose();
        Assert.Equal(1, h.Delays.Count);
    }

    [Fact]
    public async Task AFailingInUseCheck_KeepsTheModels()
    {
        int checks = 0;
        ManualDelays delays = new();
        int releases = 0;
        IdleModelReleaser releaser = new(
            inUse: () => ++checks == 1 ? throw new InvalidOperationException("boom") : (string)null,
            release: () => releases++,
            delay: delays.Delay);
        releaser.Configure(Idle);
        using (IDisposable activity = await releaser.BeginAsync(CancellationToken.None))
        {
        }

        Task pending = releaser.PendingTimer;
        delays.Fire(0);
        await pending;
        Assert.Equal(0, releases);

        pending = releaser.PendingTimer;
        delays.Fire(1);
        await pending;
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task AFailingRelease_IsLogged_AndTheNextIdlePeriodTriesAgain()
    {
        int attempts = 0;
        Harness h = new(release: () =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("device lost");
            }
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();

        await h.FireAsync(0);
        Assert.Contains("device lost", Assert.Single(h.Log));

        await h.RunRequestAsync();
        await h.FireAsync(1);
        Assert.Equal(2, attempts);
    }
}
