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

    /// <summary>Awaits <paramref name="task"/> for at most 30 s, so a regression fails the test instead of hanging it.</summary>
    private static async Task Bounded(Task task)
    {
        Task done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(done, task), "the timer never finished");
        await task;
    }

    private sealed class Harness
    {
        public int Releases;
        public string Busy;
        public List<string> Log { get; } = [];
        public List<string> Detail { get; } = [];
        public ManualDelays Delays { get; }
        public IdleModelReleaser Releaser { get; }

        public Harness(ManualDelays delays = null, Func<bool> release = null)
        {
            Delays = delays ?? new ManualDelays();
            Releaser = new IdleModelReleaser(
                inUse: () => Busy,
                release: release ?? (() => { Interlocked.Increment(ref Releases); return true; }),
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
            await Bounded(pending);
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
            return true;
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
            return true;
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
        await Bounded(pending);
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
            release: () => { releases++; return true; },
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
    public async Task AFailingRelease_IsLogged_AndRetriedOnePeriodLater_UntilItSucceeds()
    {
        int attempts = 0;
        Harness h = new(release: () =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("device lost");
            }
            return attempts > 2;
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();

        await h.FireAsync(0);
        Assert.Contains("device lost", Assert.Single(h.Log));
        // No request in between: the retry is scheduled by the failure itself, a full period out.
        Assert.Equal(2, h.Delays.Count);
        Assert.Equal(Idle, h.Delays.SpanOf(1));

        // A release that reports false (could not free) is a failure too.
        await h.FireAsync(1);
        Assert.Equal(2, attempts);
        Assert.Equal(2, h.Log.Count);
        Assert.Contains("could not free", h.Log[1]);
        Assert.Equal(3, h.Delays.Count);

        await h.FireAsync(2);
        Assert.Equal(3, attempts);
        Assert.Contains("Released the resident audio models", h.Log[2]);
        // Succeeded: nothing resident, nothing more scheduled.
        Assert.Equal(3, h.Delays.Count);
    }

    [Fact]
    public async Task AFailedRelease_KeepsTheUsedFlag_SoAReconfigureStillSchedules()
    {
        Harness h = new(release: () => false);
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        await h.FireAsync(0);
        int before = h.Delays.Count;

        h.Releaser.Configure(Idle);

        Assert.Equal(before + 1, h.Delays.Count);
    }

    [Fact]
    public async Task AfterASuccessfulRelease_ReconfiguringSchedulesNothing()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        await h.FireAsync(0);
        Assert.Equal(1, h.Releases);

        h.Releaser.Configure(Idle);

        Assert.Equal(1, h.Delays.Count);
    }

    [Fact]
    public async Task ATimerPastItsDelay_StandsDown_WhenIdleReleaseWasTurnedOffMeanwhile()
    {
        // Cancellation lost the race: the wait still completes after Configure(0).
        Harness h = new(new ManualDelays(honourCancel: false));
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task pending = h.Releaser.PendingTimer;

        h.Releaser.Configure(TimeSpan.Zero);
        h.Delays.Fire(0);
        await Bounded(pending);

        Assert.Equal(0, h.Releases);
    }

    [Fact]
    public async Task ASupersededTimer_DoesNotRelease_EvenWithNoActivityAndReleaseOn()
    {
        // Reconfiguring bumps the epoch via a fresh timer; the earlier one, whose cancellation lost the race,
        // fires with nothing active and release on, and must still not act for itself.
        Harness h = new(new ManualDelays(honourCancel: false));
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task first = h.Releaser.PendingTimer;
        h.Releaser.Configure(Idle); // schedules a second timer
        Assert.Equal(2, h.Delays.Count);

        h.Delays.Fire(0);
        await Bounded(first);
        Assert.Equal(0, h.Releases);

        await h.FireAsync(1);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task Configure_ClampsAnOverlongIdleTime_ToThirtyDays()
    {
        Assert.Equal(TimeSpan.FromMinutes(43200), IdleModelReleaser.MaxIdleAfter);
        Harness h = new();
        h.Releaser.Configure(TimeSpan.FromMinutes(43200 * 2));
        await h.RunRequestAsync();
        Assert.Equal(IdleModelReleaser.MaxIdleAfter, h.Delays.SpanOf(0));

        Harness exact = new();
        exact.Releaser.Configure(TimeSpan.FromMinutes(43200));
        await exact.RunRequestAsync();
        Assert.Equal(IdleModelReleaser.MaxIdleAfter, exact.Delays.SpanOf(0));

        Harness below = new();
        below.Releaser.Configure(TimeSpan.FromMinutes(43199));
        await below.RunRequestAsync();
        Assert.Equal(TimeSpan.FromMinutes(43199), below.Delays.SpanOf(0));
    }

    [Fact]
    public async Task NoteExternalRelease_CancelsThePendingTimer_AndSchedulesNothingUntilTheNextRequest()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task pending = h.Releaser.PendingTimer;

        h.Releaser.NoteExternalRelease();
        await Bounded(pending);
        h.Delays.Fire(0);
        Assert.Equal(0, h.Releases);

        // Reconfiguring does not bring it back: nothing has run since.
        h.Releaser.Configure(Idle);
        Assert.Equal(1, h.Delays.Count);

        await h.RunRequestAsync();
        Assert.Equal(2, h.Delays.Count);
        await h.FireAsync(1);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task NoteExternalRelease_IsIgnoredWhileAnActivityIsOpen()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        IDisposable open = await h.Releaser.BeginAsync(CancellationToken.None);

        h.Releaser.NoteExternalRelease();
        open.Dispose();

        // The activity may have loaded something after the external release, so its end still schedules, and the
        // release runs.
        Assert.Equal(1, h.Delays.Count);
        await h.FireAsync(0);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task NoteExternalRelease_DoesNotWaitBehindARunningRelease()
    {
        using ManualResetEventSlim releaseStarted = new(false);
        using ManualResetEventSlim finishRelease = new(false);
        Harness h = new(release: () =>
        {
            releaseStarted.Set();
            finishRelease.Wait();
            return true;
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task timer = h.Releaser.PendingTimer;
        h.Delays.Fire(0);
        Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(30)), "the release never started");

        Task call = Task.Run(() =>
        {
            h.Releaser.NoteExternalRelease();
            h.Releaser.Configure(Idle);
        });
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))));

        finishRelease.Set();
        await Bounded(timer);
    }

    [Fact]
    public async Task RunAsync_HoldsAnActivityWhileTheWorkRuns_AndEndsItEvenWhenTheWorkThrows()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        int during = -1;

        string result = await h.Releaser.RunAsync(() =>
        {
            during = h.Releaser.ActiveCount;
            return Task.FromResult("ok");
        }, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(1, during);
        Assert.Equal(0, h.Releaser.ActiveCount);
        Assert.Equal(1, h.Delays.Count); // its end started the timer

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Releaser.RunAsync<int>(() => throw new InvalidOperationException("boom"), CancellationToken.None));
        Assert.Equal(0, h.Releaser.ActiveCount);
    }

    [Fact]
    public async Task RunAsync_WaitsForARunningRelease_BeforeTheWorkStarts()
    {
        using ManualResetEventSlim releaseStarted = new(false);
        using ManualResetEventSlim finishRelease = new(false);
        Harness h = new(release: () =>
        {
            releaseStarted.Set();
            finishRelease.Wait();
            return true;
        });
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task timer = h.Releaser.PendingTimer;
        h.Delays.Fire(0);
        Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(30)), "the release never started");
        bool started = false;

        Task<int> run = h.Releaser.RunAsync(() =>
        {
            started = true;
            return Task.FromResult(1);
        }, CancellationToken.None);
        Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(250))));
        Assert.False(started);

        finishRelease.Set();
        await Bounded(timer);
        Assert.Equal(1, await run);
        Assert.True(started);
    }

    [Fact]
    public async Task NoteExternalRelease_WithAStaleGeneration_IsIgnored_BecauseARequestBeganDuringTheRelease()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        long before = h.Releaser.Generation;

        // A request begins and ends while the external release was running: it may have loaded models the release
        // missed, so its timer must survive.
        await h.RunRequestAsync();
        h.Releaser.NoteExternalRelease(before);

        Assert.Equal(2, h.Delays.Count);
        await h.FireAsync(1);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public async Task NoteExternalRelease_WithTheCurrentGeneration_StillSettlesTheTimer()
    {
        Harness h = new();
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        Task pending = h.Releaser.PendingTimer;

        h.Releaser.NoteExternalRelease(h.Releaser.Generation);

        await Bounded(pending);
        h.Delays.Fire(0);
        Assert.Equal(0, h.Releases);
    }

    [Fact]
    public async Task ARetryingRelease_StopsWhenIdleReleaseIsTurnedOff()
    {
        Harness h = new(release: () => false);
        h.Releaser.Configure(Idle);
        await h.RunRequestAsync();
        await h.FireAsync(0);
        Assert.Equal(2, h.Delays.Count); // the retry is pending
        Task retry = h.Releaser.PendingTimer;

        h.Releaser.Configure(TimeSpan.Zero);
        await Bounded(retry);
        h.Delays.Fire(1);

        Assert.Equal(2, h.Delays.Count);
        Assert.Single(h.Log); // no second attempt
    }

    [Fact]
    public async Task ATinyPositiveIdleTime_IsRaisedToTheMinimum_SoAFailingReleaseCannotSpin()
    {
        Harness h = new(release: () => false);
        h.Releaser.Configure(TimeSpan.FromMilliseconds(1));
        await h.RunRequestAsync();
        Assert.Equal(IdleModelReleaser.MinIdleAfter, h.Delays.SpanOf(0));

        await h.FireAsync(0);

        Assert.Equal(IdleModelReleaser.MinIdleAfter, h.Delays.SpanOf(1));
    }
}
