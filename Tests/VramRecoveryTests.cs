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
/// already uses, see ResidencyPinRaceTests).</summary>
public class VramRecoveryTests
{
    private static OutOfVramException Oom() => new(64 * 1024 * 1024, 152 * 1024 * 1024, 24082L * 1024 * 1024);

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task RunWithVramRecoveryAsync_OnOutOfVram_EvictsOwnModels_ThenAsksOtherBackends_ThenDelays()
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
            evictOwnModels: () => order.Add("own"),
            freeOtherBackends: _ =>
            {
                order.Add("other");
                return Task.FromResult<IReadOnlyList<string>>(["comfy-backend #1"]);
            },
            delay: (_, _) => { order.Add("delay"); return Task.CompletedTask; },
            log: _ => { },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(["own", "other", "delay"], order);
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
            Operation, true, () => { }, _ => Task.FromResult<IReadOnlyList<string>>([]),
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
            Operation, true, () => { }, _ => Task.FromResult<IReadOnlyList<string>>([]), NoDelay, _ => { }, CancellationToken.None));

        // The original attempt plus exactly one retry -- never a second retry on the second failure.
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_SettingOff_PropagatesImmediately_TodaysBehavior()
    {
        int calls = 0;
        bool evictCalled = false;
        bool freeCalled = false;
        Task<int> Operation()
        {
            calls++;
            throw Oom();
        }

        await Assert.ThrowsAsync<OutOfVramException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, enabled: false,
            evictOwnModels: () => evictCalled = true,
            freeOtherBackends: _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); },
            delay: NoDelay, log: _ => { }, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.False(evictCalled);
        Assert.False(freeCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_NoFailure_NeverTouchesTheRecoveryPath()
    {
        bool evictCalled = false;
        bool freeCalled = false;

        int result = await AudioEngineBridge.RunWithVramRecoveryAsync(
            () => Task.FromResult(7), enabled: true,
            evictOwnModels: () => evictCalled = true,
            freeOtherBackends: _ => { freeCalled = true; return Task.FromResult<IReadOnlyList<string>>([]); },
            delay: NoDelay, log: _ => { }, CancellationToken.None);

        Assert.Equal(7, result);
        Assert.False(evictCalled);
        Assert.False(freeCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_NonVramException_PropagatesWithoutRecovery()
    {
        bool evictCalled = false;
        Task<int> Operation() => throw new InvalidOperationException("not a VRAM problem");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, () => evictCalled = true, _ => Task.FromResult<IReadOnlyList<string>>([]), NoDelay, _ => { }, CancellationToken.None));

        Assert.False(evictCalled);
    }

    [Fact]
    public async Task RunWithVramRecoveryAsync_AlreadyCancelled_SkipsRecoveryEntirely()
    {
        // No point evicting AudioLab's models (or anyone else's) for a request that is about to be cancelled
        // anyway -- the cancellation must win over the OOM recovery path, not be raced by it.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        bool evictCalled = false;
        Task<int> Operation() => throw Oom();

        // ThrowIfCancellationRequested's OperationCanceledException turns the returned Task Canceled, and
        // awaiting a canceled Task surfaces TaskCanceledException (a subclass) to the caller -- ThrowsAny
        // is the deliberate choice here, not ThrowsAsync's exact-type match.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioEngineBridge.RunWithVramRecoveryAsync(
            Operation, true, () => evictCalled = true, _ => Task.FromResult<IReadOnlyList<string>>([]), NoDelay, _ => { }, cts.Token));

        Assert.False(evictCalled);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_OnlyFreesIdleBackendsThatAreNotAudioLabsOwn()
    {
        bool busyFreeCalled = false, ownFreeCalled = false, idleFreeCalled = false;
        AudioEngineBridge.VramBackendCandidate[] candidates =
        [
            new("busy-comfy #1", IsAudioLabOwned: false, IsIdle: false, FreeMemoryAsync: () => { busyFreeCalled = true; return Task.FromResult(true); }),
            new("own-audio #2", IsAudioLabOwned: true, IsIdle: true, FreeMemoryAsync: () => { ownFreeCalled = true; return Task.FromResult(true); }),
            new("idle-comfy #3", IsAudioLabOwned: false, IsIdle: true, FreeMemoryAsync: () => { idleFreeCalled = true; return Task.FromResult(true); }),
        ];

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(candidates, _ => { }, CancellationToken.None);

        Assert.False(busyFreeCalled, "a mid-generation backend must never be asked to free memory");
        Assert.False(ownFreeCalled, "AudioLab's own backend is FreeMemory's job, not this one's");
        Assert.True(idleFreeCalled);
        Assert.Equal(["idle-comfy #3"], freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_FreeMemoryReturningFalse_IsNotCountedAsFreed()
    {
        AudioEngineBridge.VramBackendCandidate[] candidates =
        [
            new("idle-but-nothing-cached", false, true, () => Task.FromResult(false)),
        ];

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(candidates, _ => { }, CancellationToken.None);

        Assert.Empty(freed);
    }

    [Fact]
    public async Task FreeIdleOtherBackendsCoreAsync_OneCandidateThrows_OthersAreStillTried()
    {
        AudioEngineBridge.VramBackendCandidate[] candidates =
        [
            new("throws", false, true, () => throw new InvalidOperationException("boom")),
            new("idle-ok", false, true, () => Task.FromResult(true)),
        ];

        IReadOnlyList<string> freed = await AudioEngineBridge.FreeIdleOtherBackendsCoreAsync(candidates, _ => { }, CancellationToken.None);

        Assert.Equal(["idle-ok"], freed);
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
