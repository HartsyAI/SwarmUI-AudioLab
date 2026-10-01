using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Independent review traced a real leak: <c>RequestKeepResident(false)</c> called
/// <c>ClearResidencyPins()</c> without taking <c>_residencyLock</c>, so it could run concurrently with
/// <c>MaybeKeepResidentAsync</c>'s in-flight <c>await Engine.Speech.OpenSynthesizerAsync(...)</c> (a model
/// load, not fast) — the clear would null the old pin first, then the open would complete and
/// unconditionally overwrite it with the brand-new lease, which nothing would ever dispose again (the
/// top-of-method <c>!_keepResident</c> guard short-circuits every later call once the setting is off).
///
/// <para>These tests drive <see cref="AudioEngineBridge.OpenResidentPinCoreAsync{TLease}"/> and
/// <see cref="AudioEngineBridge.ClearResidentPinCore{TLease}"/> directly with a fake lease and plain local
/// state (both methods take the gate, the pin accessors and the open/dispose steps as parameters
/// specifically so a test can do this without a live <c>Engine</c> or this class's real private fields) and
/// assert the actual end state — no pin left pointing at a lease nobody will ever dispose — not just that
/// nothing throws.</para></summary>
public class ResidencyPinRaceTests
{
    private sealed class FakeLease
    {
        public bool Disposed;
        public string Name;
    }

    [Fact]
    public async Task OpenResidentPinCoreAsync_SettingTurnsOffWhileOpening_DisposesTheNewLeaseInsteadOfStoringIt()
    {
        // The minimal version of the race: no second caller at all, just the setting flipping between the
        // open starting and finishing -- proves the post-await recheck alone closes the gap.
        SemaphoreSlim gate = new(1, 1);
        (FakeLease Lease, string Key) pinned = (null, null);
        bool keepResident = true;
        FakeLease opened = new() { Name = "kokoro:af_heart" };

        bool stored = await AudioEngineBridge.OpenResidentPinCoreAsync(
            gate,
            () => keepResident,
            "kokoro:af_heart",
            () => pinned,
            (lease, key) => pinned = (lease, key),
            async ct =>
            {
                await Task.Yield();
                keepResident = false; // RequestKeepResident(false) landing mid-open
                return opened;
            },
            lease => lease.Disposed = true,
            CancellationToken.None);

        Assert.False(stored);
        Assert.True(opened.Disposed);
        Assert.Null(pinned.Lease);
        Assert.Null(pinned.Key);
    }

    [Fact]
    public async Task OpenAndClear_GenuinelyConcurrent_TheGateSerializesThemAndNoLeaseSurvivesUndisposed()
    {
        // The full race: a second, actually-concurrent caller (ClearResidentPinCore, what
        // RequestKeepResident(false) runs) arrives while the open is in flight and blocked on the SAME gate
        // -- not simulated by flipping a flag inline, but driven with real Task.Run concurrency and explicit
        // signals so the ordering is deterministic without relying on timing.
        SemaphoreSlim gate = new(1, 1);
        (FakeLease Lease, string Key) pinned = (null, null);
        (FakeLease, string) GetPinned() => pinned;
        void SetPinned(FakeLease lease, string key) => pinned = (lease, key);
        bool keepResident = true;
        FakeLease opened = new() { Name = "kokoro:af_heart" };
        TaskCompletionSource openStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> openTask = AudioEngineBridge.OpenResidentPinCoreAsync(
            gate, () => keepResident, "kokoro:af_heart", GetPinned, SetPinned,
            async ct =>
            {
                openStarted.SetResult(); // tells the test "I hold the gate and am mid-open"
                await releaseOpen.Task;
                return opened;
            },
            lease => lease.Disposed = true, CancellationToken.None);

        await openStarted.Task; // the open now holds `gate`

        // RequestKeepResident(false): flips the setting, then clears -- which must block on `gate` rather
        // than racing the open, since both now take the same one.
        keepResident = false;
        Task clearTask = Task.Run(() => AudioEngineBridge.ClearResidentPinCore(gate, GetPinned, SetPinned, (Action<FakeLease>)(l => l.Disposed = true)));

        // Sanity check that this test is actually exercising contention (clearTask genuinely blocked on the
        // gate the open holds), not accidentally racing itself with no overlap at all.
        Assert.False(clearTask.Wait(TimeSpan.FromMilliseconds(50)));

        releaseOpen.SetResult(); // let the open finish; it will recheck keepResidentNow() (now false) and discard it
        bool stored = await openTask;
        await clearTask;

        Assert.False(stored);
        Assert.True(opened.Disposed);
        Assert.Null(pinned.Lease);
        Assert.Null(pinned.Key);
    }

    [Fact]
    public async Task OpenResidentPinCoreAsync_SettingStaysOn_StoresAndDisposesThePreviousLease()
    {
        // Sanity check the non-racing path still works: a genuine switch to a different model disposes the
        // old pin and stores the new one -- this is what the race-fix tests above are measured against.
        SemaphoreSlim gate = new(1, 1);
        FakeLease previous = new() { Name = "whisper:small.en" };
        (FakeLease Lease, string Key) pinned = (previous, "whisper:small.en");
        FakeLease opened = new() { Name = "kokoro:af_heart" };

        bool stored = await AudioEngineBridge.OpenResidentPinCoreAsync(
            gate, () => true, "kokoro:af_heart",
            () => pinned, (lease, key) => pinned = (lease, key),
            _ => Task.FromResult(opened), lease => lease.Disposed = true,
            CancellationToken.None);

        Assert.True(stored);
        Assert.True(previous.Disposed);
        Assert.False(opened.Disposed);
        Assert.Same(opened, pinned.Lease);
        Assert.Equal("kokoro:af_heart", pinned.Key);
    }

    [Fact]
    public async Task OpenResidentPinCoreAsync_AlreadyPinnedWithTheSameKey_IsANoOp()
    {
        SemaphoreSlim gate = new(1, 1);
        FakeLease existing = new() { Name = "kokoro:af_heart" };
        (FakeLease Lease, string Key) pinned = (existing, "kokoro:af_heart");
        bool openCalled = false;

        bool stored = await AudioEngineBridge.OpenResidentPinCoreAsync(
            gate, () => true, "kokoro:af_heart",
            () => pinned, (lease, key) => pinned = (lease, key),
            _ => { openCalled = true; return Task.FromResult(new FakeLease()); }, lease => lease.Disposed = true,
            CancellationToken.None);

        Assert.False(stored);
        Assert.False(openCalled);
        Assert.Same(existing, pinned.Lease);
        Assert.False(existing.Disposed);
    }

    [Fact]
    public void ClearResidentPinCore_WithAPinned_DisposesItAndClearsTheFields()
    {
        SemaphoreSlim gate = new(1, 1);
        FakeLease existing = new();
        (FakeLease Lease, string Key) pinned = (existing, "kokoro:af_heart");

        AudioEngineBridge.ClearResidentPinCore(gate, () => pinned, (lease, key) => pinned = (lease, key), lease => lease.Disposed = true);

        Assert.True(existing.Disposed);
        Assert.Null(pinned.Lease);
        Assert.Null(pinned.Key);
    }

    [Fact]
    public void ClearResidentPinCore_WithNothingPinned_IsSafe()
    {
        SemaphoreSlim gate = new(1, 1);
        (FakeLease Lease, string Key) pinned = (null, null);
        AudioEngineBridge.ClearResidentPinCore(gate, () => pinned, (lease, key) => pinned = (lease, key), lease => lease.Disposed = true);
        Assert.Null(pinned.Lease);
    }
}
