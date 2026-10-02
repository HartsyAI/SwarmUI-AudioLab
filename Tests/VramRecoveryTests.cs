using HartsyInference.Core.Exceptions;
using Hartsy.Extensions.AudioLab.AudioServices;
using SwarmUI.Backends;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for the VRAM-OOM recovery AudioLab added around <see cref="AudioEngineBridge.ProcessAsync"/>:
/// when SwarmUI's image/video backends and AudioLab share one GPU with no coordination, an image backend that
/// still holds weights resident can leave an audio model with nowhere to load. <see cref="AudioEngineBridge.RunWithVramRecoveryAsync{T}"/>
/// and <see cref="AudioEngineBridge.FreeIdleOtherBackendsCoreAsync"/> are both parameterized over every side
/// effect specifically so these tests can drive the decision logic with fakes -- no live Engine, no real
/// SwarmUI backend registry, no GPU, no real delay (same pattern <see cref="AudioEngineBridge.OpenResidentPinCoreAsync{TLease}"/>
/// already uses, see ResidencyPinRaceTests).
///
/// <para>Two independent-review rounds of an earlier version of this feature found real problems in the REAL
/// wiring -- not reachable by a suite that only drives the parameterized core with fakes using
/// behavior-correct stand-ins. Round 1: an explicit "evict AudioLab's own idle models" step called
/// <c>IInferenceEngine.FreeMemory()</c> with no busy check, unsafe because the Engine's generation lock
/// releases before this code's catch block ever runs; and freeing another SwarmUI backend read its idle
/// state once with nothing stopping the scheduler from assigning it new work in between. Round 2, after a
/// reservation step was added: the reservation itself was not exclusive (two overlapping AudioLab
/// recoveries, or an existing reservation from elsewhere, could both pass it and both call
/// <c>FreeMemory</c> on the same backend at once -- demonstrated with a real concurrent-call probe), and a
/// remote SwarmUI backend was a candidate at all (its own <c>FreeMemory</c> hits that remote machine's
/// unconditional <c>/API/FreeBackendMemory</c>, which this process cannot verify is safe). All four are
/// fixed: the own-eviction step is gone entirely (the Engine's in-lock memory-pressure sweep covers that,
/// and nothing outside the lock can safely duplicate it -- see
/// <see cref="AudioEngineBridge.RunWithVramRecoveryAsync{T}"/>'s doc); each OTHER backend is now reserved
/// EXCLUSIVELY (<see cref="AudioEngineBridge.VramBackendCandidate.TryReserve"/>, only the caller whose
/// <see cref="AbstractBackend.Reservations"/> increment lands on exactly 1 proceeds) before its idle state
/// is rechecked, mirroring SwarmUI core's own <c>ModelsAPI.cs</c> model-resave recovery path; and a
/// <c>SwarmSwarmBackend</c> instance is never a candidate at all. These tests cover all of it directly.</para></summary>
public class VramRecoveryTests
{
    private static OutOfVramException Oom() => new(64 * 1024 * 1024, 152 * 1024 * 1024, 24082L * 1024 * 1024);

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task RunWithVramRecoveryAsync_OnOutOfVram_AsksOtherBackends_ThenDelays_ThenRetries()
    {
        List<string> order = [];
        int calls = 0;
        Task<int> Operation()
        {
            calls++;
            return calls == 1 ? throw Oom() : Task.FromResult(42);
        }

        int result = await AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation,
            enabled: true,
            freeOtherBackends: _ =>
            {
                order.Add("other");
                return Task.FromResult<IReadOnlyList<string>>(["comfy-backend #1"]);
            },
            delay: (_, _) => { order.Add("delay"); return Task.CompletedTask; },
            log: _ => { },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(["other", "delay"], order);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_Delay_UsesAtLeastOneSecond()
    {
        TimeSpan? delayUsed = null;
        int calls = 0;
        Task<int> Operation()
        {
            calls++;
            return calls == 1 ? throw Oom() : Task.FromResult(1);
        }

        await AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, _ => Task.FromResult<IReadOnlyList<string>>([]),
            (ts, _) => { delayUsed = ts; return Task.CompletedTask; }, _ => { }, CancellationToken.None);

        Assert.True(delayUsed >= TimeSpan.FromSeconds(1), $"expected at least 1s, got {delayUsed}");
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_SecondFailure_PropagatesWithoutAFurtherRetry()
    {
        int calls = 0;
        Task<int> Operation()
        {
            calls++;
            throw Oom();
        }

        await Assert.ThrowsAsync<OutOfVramException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, _ => Task.FromResult<IReadOnlyList<string>>([]), NoDelay, _ => { }, CancellationToken.None));

        // The original attempt plus exactly one retry -- never a second retry on the second failure.
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_SettingOff_PropagatesImmediately_TodaysBehavior()
    {
        int calls = 0;
        bool freeCalled = false;
        Task<int> Operation()
        {
            calls++;
            throw Oom();
        }

        await Assert.ThrowsAsync<OutOfVramException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, enabled: false,
            freeOtherBackends: _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); },
            delay: NoDelay, log: _ => { }, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.False(freeCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_NoFailure_NeverTouchesTheRecoveryPath()
    {
        bool freeCalled = false;

        int result = await AudioEngineBridge.RunWithVramRecoveryAsync(
            () => Task.FromResult(7), enabled: true,
            freeOtherBackends: _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); },
            delay: NoDelay, log: _ => { }, CancellationToken.None);

        Assert.Equal(7, result);
        Assert.False(freeCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_NonVramException_PropagatesWithoutRecovery()
    {
        bool freeCalled = false;
        Task<int> Operation() => throw new InvalidOperationException("not a VRAM problem");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); }, NoDelay, _ => { }, CancellationToken.None));

        Assert.False(freeCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_AlreadyCancelled_SkipsRecoveryEntirely()
    {
        // No point freeing anyone's memory for a request that is about to be cancelled anyway -- the
        // cancellation must win over the OOM recovery path, not be raced by it.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        bool freeCalled = false;
        Task<int> Operation() => throw Oom();

        // ThrowIfCancellationRequested's OperationCanceledException turns the returned Task Canceled, and
        // awaiting a canceled Task surfaces TaskCanceledException (a subclass) to the caller -- ThrowsAny
        // is the deliberate choice here, not ThrowsAsync's exact-type match.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); }, NoDelay, _ => { }, cts.Token));

        Assert.False(freeCalled);
    }

    /// <summary>Builds a candidate whose <c>TryReserve</c>/<c>Release</c> share a real
    /// <see cref="Interlocked"/>-guarded counter -- the same shape <c>FreeIdleOtherBackendsAsync</c> wires a
    /// real <see cref="AbstractBackend.Reservations"/> with -- so tests can exercise genuine exclusivity
    /// instead of a fake that always says yes.</summary>
    /// <summary><paramref name="reservations"/> is a one-element array, not a <c>ref int</c> -- a lambda
    /// cannot capture a byref parameter, and this needs the SAME mutable counter visible to multiple
    /// candidates built from the same test, same as two real <see cref="AbstractBackend"/> instances would
    /// share one <see cref="AbstractBackend.Reservations"/> field.</summary>
    private static AudioEngineBridge.VramBackendCandidate ExclusiveCandidate(
        string name, int[] reservations, Func<bool> isIdleNow, Func<Task<bool>> freeMemoryAsync)
    {
        return new(
            name, IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () =>
            {
                if (Interlocked.Increment(ref reservations[0]) == 1)
                {
                    return true;
                }
                Interlocked.Decrement(ref reservations[0]);
                return false;
            },
            Release: () => Interlocked.Decrement(ref reservations[0]),
            IsIdleNow: isIdleNow,
            FreeMemoryAsync: freeMemoryAsync);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_ReservesBeforeCheckingIdle()
    {
        List<string> order = [];
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "comfy #1", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => { order.Add("reserve"); return true; },
            Release: () => order.Add("release"),
            IsIdleNow: () => { order.Add("check"); return true; },
            FreeMemoryAsync: () => { order.Add("free"); return Task.FromResult(true); });

        await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.Equal(["reserve", "check", "free", "release"], order);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_NotIdleAfterReservation_IsNeverFreed_ButStillReleased()
    {
        // Something was already running on this backend by the time the reservation landed (a reservation
        // can only block NEW work, not undo work already in flight). Must not free it, and must still
        // release the reservation so it isn't stuck forever.
        bool reserved = false, released = false, freeCalled = false;
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "busy-after-reserve", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => { reserved = true; return true; },
            Release: () => released = true,
            IsIdleNow: () => false,
            FreeMemoryAsync: () => { freeCalled = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.True(reserved);
        Assert.True(released);
        Assert.False(freeCalled);
        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_IdleCandidate_IsFreedThenReleased()
    {
        List<string> order = [];
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "idle-comfy #3", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => true,
            Release: () => order.Add("release"),
            IsIdleNow: () => true,
            FreeMemoryAsync: () => { order.Add("free"); return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.Equal(["idle-comfy #3"], freed);
        Assert.Equal(["free", "release"], order);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_AudioLabOwned_IsNeverReservedCheckedOrFreed()
    {
        bool touched = false;
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "own-audio #2", IsAudioLabOwned: true, IsRemote: false,
            TryReserve: () => { touched = true; return true; },
            Release: () => touched = true,
            IsIdleNow: () => { touched = true; return true; },
            FreeMemoryAsync: () => { touched = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.False(touched, "AudioLab's own backend must be skipped entirely, with no reserve/check/free");
        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_Remote_IsNeverReservedCheckedOrFreed()
    {
        // A SwarmSwarmBackend's FreeMemory hits the REMOTE machine's own unconditional
        // /API/FreeBackendMemory; this process cannot verify what else is running there, so it must never
        // even attempt to reserve or free one.
        bool touched = false;
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "remote-swarm #4", IsAudioLabOwned: false, IsRemote: true,
            TryReserve: () => { touched = true; return true; },
            Release: () => touched = true,
            IsIdleNow: () => { touched = true; return true; },
            FreeMemoryAsync: () => { touched = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.False(touched, "a remote backend must be skipped entirely, with no reserve/check/free");
        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_FreeMemoryThrows_StillReleases_AndOthersStillTried()
    {
        bool throwerReleased = false, okReleased = false, okFreed = false;
        AudioEngineBridge.VramBackendCandidate throwing = new(
            "throws", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => true,
            Release: () => throwerReleased = true,
            IsIdleNow: () => true,
            FreeMemoryAsync: () => throw new InvalidOperationException("boom"));
        AudioEngineBridge.VramBackendCandidate ok = new(
            "idle-ok", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => true,
            Release: () => okReleased = true,
            IsIdleNow: () => true,
            FreeMemoryAsync: () => { okFreed = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([throwing, ok], _ => { }, CancellationToken.None);

        Assert.True(throwerReleased, "a reservation must be released even when FreeMemoryAsync throws");
        Assert.True(okReleased);
        Assert.True(okFreed);
        Assert.Equal(["idle-ok"], freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_FreeMemoryReturningFalse_IsNotCountedAsFreed()
    {
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "idle-but-nothing-cached", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => true, Release: () => { }, IsIdleNow: () => true,
            FreeMemoryAsync: () => Task.FromResult(false));

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_TryReserveFails_IsSkipped_NoReleaseNoFree()
    {
        // The direct, single-call-site version of the exclusivity contract: when TryReserve itself reports
        // failure (another holder already has it), nothing else about that candidate runs -- not
        // IsIdleNow, not FreeMemoryAsync, not even Release (TryReserve already backed off its own
        // increment; a second decrement here would under-flow the counter below zero).
        bool idleChecked = false, freeCalled = false, released = false;
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "already-held", IsAudioLabOwned: false, IsRemote: false,
            TryReserve: () => false,
            Release: () => released = true,
            IsIdleNow: () => { idleChecked = true; return true; },
            FreeMemoryAsync: () => { freeCalled = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.False(idleChecked);
        Assert.False(freeCalled);
        Assert.False(released);
        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_TwoConcurrentRecoveries_OnlyOneFreesTheSameBackend()
    {
        // The actual race the second review round demonstrated with a live probe: two AudioLab recoveries
        // running at once (e.g. two queued requests that both OOM) both see the same other backend as a
        // candidate. Driven with real overlapping Tasks and explicit signals -- not simulated by flipping a
        // flag inline -- so the ordering is deterministic without relying on timing, the same reasoning
        // ResidencyPinRaceTests uses for its own genuinely-concurrent case.
        int[] reservations = [0];
        int freeCallCount = 0;
        TaskCompletionSource firstIsInsideFree = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstFree = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AudioEngineBridge.VramBackendCandidate MakeCandidate() => ExclusiveCandidate(
            "shared-backend", reservations,
            isIdleNow: () => true,
            freeMemoryAsync: async () =>
            {
                Interlocked.Increment(ref freeCallCount);
                firstIsInsideFree.TrySetResult();
                await releaseFirstFree.Task; // held open deliberately so the second sweep can race it
                return true;
            });

        Task<IReadOnlyList<string>> first = AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(
            [MakeCandidate()], _ => { }, CancellationToken.None);
        await firstIsInsideFree.Task; // the first sweep now holds the reservation AND is inside FreeMemoryAsync

        IReadOnlyList<string> second = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(
            [MakeCandidate()], _ => { }, CancellationToken.None);

        releaseFirstFree.SetResult();
        IReadOnlyList<string> firstResult = await first;

        Assert.Equal(1, freeCallCount); // only the sweep that actually reserved it ever called FreeMemoryAsync
        Assert.Equal(["shared-backend"], firstResult);
        Assert.Empty(second); // the second sweep saw TryReserve fail and backed off without touching it
        Assert.Equal(0, reservations[0]); // both sweeps fully unwound; nothing left stuck reserved
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_NoCandidates_ReturnsEmpty()
    {
        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(
            [], _ => { }, CancellationToken.None);

        Assert.Empty(freed);
    }

    [Fact]
    public void IsIdleCandidate_RunningWithNoClaimsAndNoReservation_IsIdle()
    {
        Assert.True(AudioEngineBridge.IsIdleCandidate(BackendStatus.RUNNING, reserveModelLoad: false, usages: 0));
    }

    [Fact]
    public void IsIdleCandidate_RunningButClaimedByARequest_IsNotIdle()
    {
        Assert.False(AudioEngineBridge.IsIdleCandidate(BackendStatus.RUNNING, reserveModelLoad: false, usages: 1));
    }

    [Fact]
    public void IsIdleCandidate_RunningButReservedForAModelLoad_IsNotIdle()
    {
        Assert.False(AudioEngineBridge.IsIdleCandidate(BackendStatus.RUNNING, reserveModelLoad: true, usages: 0));
    }

    [Theory]
    [InlineData(BackendStatus.WAITING)]
    [InlineData(BackendStatus.LOADING)]
    [InlineData(BackendStatus.ERRORED)]
    [InlineData(BackendStatus.DISABLED)]
    [InlineData(BackendStatus.IDLE)] // SwarmUI's own "IDLE" status is a different lifecycle state, not this
                                     // method's notion of idle -- only RUNNING ever counts as idle here.
    public void IsIdleCandidate_NotRunning_IsNotIdleEvenWithNoClaims(BackendStatus status)
    {
        // A backend that is not RUNNING yet (or any more) is not a safe target either -- freeing it is not
        // meaningful, and calling FreeMemory on something mid-load/errored is not what "idle" means here.
        Assert.False(AudioEngineBridge.IsIdleCandidate(status, reserveModelLoad: false, usages: 0));
    }

    [Fact]
    public void RequestVramCoordination_TogglingWithNoEngineBuilt_DoesNotThrow()
    {
        // Mirrors AudioEngineBridgeTests.RequestKeepResident_TogglingWithNoEngineBuilt_DoesNotThrow -- this is
        // a plain flag write, safe before any TTS/STT/music call has ever built the shared Engine.
        AudioEngineBridge.RequestVramCoordination(false);
        AudioEngineBridge.RequestVramCoordination(true);
    }
}
