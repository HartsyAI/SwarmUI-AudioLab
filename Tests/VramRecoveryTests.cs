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
/// <para>Independent review of an earlier version of this feature found two real problems with the REAL
/// wiring (not reachable by a suite that only drives the parameterized core with fakes): (1) an explicit
/// "evict AudioLab's own idle models" step actually called <c>IInferenceEngine.FreeMemory()</c>, which drops
/// EVERY loaded model with no busy check at all -- unsafe, since the Engine's generation lock is released
/// before this code's catch block ever runs, so a second, already-queued request could be genuinely
/// generating against a different resident model at that exact moment. (2) freeing another SwarmUI backend
/// read its idle state once and acted on it, with nothing stopping the scheduler from assigning it new work
/// in between. Both are fixed now: (1) by removing AudioLab's own eviction step entirely (the Engine's own
/// in-lock memory-pressure sweep is what covers that, and nothing outside the lock can safely duplicate it --
/// see <see cref="AudioEngineBridge.RunWithVramRecoveryAsync{T}"/>'s doc), and (2) by reserving each other
/// backend (<see cref="AbstractBackend.Reservations"/>) before rechecking idle, mirroring SwarmUI core's own
/// <c>ModelsAPI.cs</c> model-resave recovery path. These tests cover both fixes directly.</para></summary>
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

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_ReservesBeforeCheckingIdle()
    {
        List<string> order = [];
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "comfy #1", IsAudioLabOwned: false,
            Reserve: () => order.Add("reserve"),
            Release: () => order.Add("release"),
            IsIdleNow: () => { order.Add("check"); return true; },
            FreeMemoryAsync: () => { order.Add("free"); return Task.FromResult(true); });

        await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.Equal(["reserve", "check", "free", "release"], order);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_NotIdleAfterReservation_IsNeverFreed_ButStillReleased()
    {
        // The race Finding 2 closed: something was already running on this backend by the time the
        // reservation landed (the reservation can only block NEW work, not undo work already in flight).
        // Must not free it, and must still release the reservation so it isn't stuck forever.
        bool reserved = false, released = false, freeCalled = false;
        AudioEngineBridge.VramBackendCandidate candidate = new(
            "busy-after-reserve", IsAudioLabOwned: false,
            Reserve: () => reserved = true,
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
            "idle-comfy #3", IsAudioLabOwned: false,
            Reserve: () => { },
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
            "own-audio #2", IsAudioLabOwned: true,
            Reserve: () => touched = true,
            Release: () => touched = true,
            IsIdleNow: () => { touched = true; return true; },
            FreeMemoryAsync: () => { touched = true; return Task.FromResult(true); });

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.False(touched, "AudioLab's own backend must be skipped entirely, with no reserve/check/free");
        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_FreeMemoryThrows_StillReleases_AndOthersStillTried()
    {
        bool throwerReleased = false, okReleased = false, okFreed = false;
        AudioEngineBridge.VramBackendCandidate throwing = new(
            "throws", IsAudioLabOwned: false,
            Reserve: () => { },
            Release: () => throwerReleased = true,
            IsIdleNow: () => true,
            FreeMemoryAsync: () => throw new InvalidOperationException("boom"));
        AudioEngineBridge.VramBackendCandidate ok = new(
            "idle-ok", IsAudioLabOwned: false,
            Reserve: () => { },
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
            "idle-but-nothing-cached", IsAudioLabOwned: false,
            Reserve: () => { }, Release: () => { }, IsIdleNow: () => true,
            FreeMemoryAsync: () => Task.FromResult(false));

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync([candidate], _ => { }, CancellationToken.None);

        Assert.Empty(freed);
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
