using System.Collections.Concurrent;
using HartsyInference.Audio.Models.Denoise;
using HartsyInference.Engine;
using HartsyInference.Engine.Audio.Wake;
using HartsyInference.Engine.Services;
using HartsyInference.Voice;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>What voice a new <c>AudioLabVoiceSession</c> should use against the currently loaded
/// <see cref="VoiceModelSet"/> (fixed at load, one Kokoro voice for every session sharing it -- see
/// <c>VoiceAgentSession</c>'s own <c>RequireSameModels</c> check). Pure decision, independent of
/// <see cref="VoiceModelSet"/> itself, so it is unit-testable without a live engine.</summary>
internal readonly record struct VoiceSelection(string VoiceToUse, bool NeedsRebuild, string Notice);

internal static class VoiceSelectionPolicy
{
    /// <summary>No set loaded yet, or it already matches: use the requested voice, no rebuild. A mismatch with
    /// nothing else using the set: rebuild for the new voice. A mismatch while calls are active: keep the loaded
    /// voice and say so, rather than tear down a set calls are using.</summary>
    public static VoiceSelection Decide(string loadedVoice, string requestedVoice, int activeSessions)
    {
        if (loadedVoice is null || loadedVoice == requestedVoice)
        {
            return new VoiceSelection(requestedVoice, NeedsRebuild: false, Notice: null);
        }
        if (activeSessions == 0)
        {
            return new VoiceSelection(requestedVoice, NeedsRebuild: true, Notice: null);
        }
        return new VoiceSelection(loadedVoice, NeedsRebuild: false,
            Notice: $"Voice '{requestedVoice}' is unavailable while another call is using '{loadedVoice}'; continuing with '{loadedVoice}'.");
    }
}

/// <summary>What <see cref="VoiceEngineModels.AcquireAsync"/> hands back: the shared model set, this session's own
/// options (model/device fields copied from the set so <c>VoiceAgentSession</c>'s <c>RequireSameModels</c> check
/// always passes), an optional notice for the client, and the specific lifetime handle to release through later.</summary>
internal sealed record VoiceModelLease(VoiceModelSet Set, VoiceAgentOptions SessionOptions, string Notice, LazyIdleResource<VoiceModelSet> Resource);

/// <summary>Owns AudioLab's one <see cref="VoiceModelSet"/>: created lazily on the first <see cref="AcquireAsync"/>,
/// disposed five minutes after the last session releases it, rebuilt for a different Kokoro voice once idle, and
/// force-dropped -- after ending every active session -- when <see cref="AudioEngineBridge.EngineReleased"/> fires.
///
/// <para>A static facade (<see cref="Shared"/>) over one instance, same shape as
/// <see cref="LazyIdleResource{T}"/> it wraps: the state machine that is actually unit-tested lives there, with
/// fakes; this class is the thin, engine-specific wiring around it (device resolution, VAD/RNNoise install, warm-up,
/// the voice-rebuild policy above) that cannot run without a real engine and so is exercised by the extension
/// compiling and working, not by a unit test. The two pieces of this class's own logic that ARE independently
/// tested, generically, are the acquire/teardown mutual exclusion itself (see <see cref="AcquireTeardownGate"/>)
/// and the stale-lease check <see cref="RegisterSession"/> runs inside it.</para></summary>
internal sealed class VoiceEngineModels
{
    public static readonly VoiceEngineModels Shared = new();

    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);

    private readonly AcquireTeardownGate _gate = new();
    private readonly ConcurrentDictionary<VoiceAgentSession, LazyIdleResource<VoiceModelSet>> _activeSessions = new();
    private LazyIdleResource<VoiceModelSet> _resource;
    private string _loadedVoice;
    private int _hooked;

    private VoiceEngineModels()
    {
    }

    /// <summary>Voice calls registered and not yet released.</summary>
    public int ActiveSessionCount => _activeSessions.Count;

    /// <summary>Subscribes to <see cref="AudioEngineBridge.EngineReleased"/> exactly once, lazily -- not at
    /// extension load, so a Swarm process that never opens the Voice Agent tab never touches it.</summary>
    private void EnsureHooked()
    {
        if (Interlocked.Exchange(ref _hooked, 1) == 0)
        {
            AudioEngineBridge.EngineReleased += OnEngineReleased;
        }
    }

    /// <summary>Gets (creating or rebuilding as needed) the model set for <paramref name="start"/>'s requested
    /// voice, and warms it with <paramref name="text"/> the first time it is actually created. Pairs with
    /// <see cref="ReleaseSessionAsync"/>, which must be called exactly once for every session this returns a lease
    /// for.
    ///
    /// <para>The whole decision-and-lease-fetch runs inside <see cref="AcquireTeardownGate.AcquireAsync{T}"/>,
    /// not just the bookkeeping around it, so a concurrent <see cref="OnEngineReleased"/> teardown either
    /// finishes first (this call then sees the rebuilt/fresh state) or waits for this call to finish first. That
    /// alone still leaves a gap between this method returning and the caller reaching <see cref="RegisterSession"/>
    /// with the session it builds from the lease -- see that method, and <see cref="AcquireTeardownGate"/>'s own
    /// remarks, for how that gap is actually closed.</para></summary>
    public Task<VoiceModelLease> AcquireAsync(VoiceSessionStartRequest start, RemoteTextService text, CancellationToken cancel)
    {
        EnsureHooked();
        string requestedVoice = string.IsNullOrWhiteSpace(start.Voice) ? "af_heart" : start.Voice;
        return _gate.AcquireAsync(async () =>
        {
            VoiceSelection selection = VoiceSelectionPolicy.Decide(_loadedVoice, requestedVoice, _resource?.ActiveCount ?? 0);
            if (_resource is null || selection.NeedsRebuild)
            {
                if (_resource is not null)
                {
                    await _resource.ForceDisposeAsync().ConfigureAwait(false);
                }
                string capturedVoice = selection.VoiceToUse;
                _resource = new LazyIdleResource<VoiceModelSet>(ct => CreateSetAsync(capturedVoice, text, ct), DisposeSetAsync, IdleDelay);
                _loadedVoice = capturedVoice;
            }
            LazyIdleResource<VoiceModelSet> resource = _resource;
            VoiceModelSet set = await resource.GetOrCreateAsync(cancel).ConfigureAwait(false);
            VoiceAgentOptions sessionOptions = set.Options with
            {
                SystemPrompt = start.SystemPrompt ?? VoiceAgentOptions.DefaultSystemPrompt,
                BargeInEnabled = start.BargeIn,
                OutboundSampleRate = 24000, // Kokoro's own rate: no outbound resample (see VoiceSessionEndpoints).
            };
            return new VoiceModelLease(set, sessionOptions, selection.Notice, resource);
        }, cancel);
    }

    /// <summary>Tracks <paramref name="session"/> as active against <paramref name="lease"/>'s specific resource
    /// instance, so an engine release can end it, and so <see cref="ReleaseSessionAsync"/> releases the same
    /// instance it was acquired from even if a voice change has since rebuilt the current resource.
    ///
    /// <para><see cref="AcquireAsync"/> releases <see cref="_gate"/> as soon as it hands the lease back, before
    /// the caller has built a <see cref="VoiceAgentSession"/> from it -- this call is what the caller must reach
    /// next, and running it inside the same gate (rather than a bare dictionary write) is what makes the two
    /// calls together closed against a teardown landing in between. It is not enough to just take the gate here
    /// though: <see cref="LazyIdleResource{T}.ForceDisposeAsync"/> resets the resource to empty rather than
    /// poisoning it, so <paramref name="lease"/>'s own <see cref="VoiceModelLease.Resource"/> reference is
    /// unchanged by a teardown that ran in the gap -- only what it currently holds changes. The check below
    /// compares the specific <see cref="VoiceModelSet"/> instance <paramref name="lease"/> was handed against
    /// <see cref="LazyIdleResource{T}.Current"/> for that same reason: a teardown that force-disposed it in the
    /// gap leaves <c>Current</c> null (or, if another acquire has since rebuilt it, pointing at a different
    /// instance entirely), never the one <paramref name="lease"/> carries. Throws rather than silently skipping
    /// the registration, so the caller -- which has already built the session by this point, but has not yet
    /// called <see cref="VoiceAgentSession.StartAsync"/> or touched any audio path -- treats this exactly like
    /// any other failure to start the call.</para></summary>
    public Task RegisterSession(VoiceAgentSession session, VoiceModelLease lease, CancellationToken cancel) => _gate.AcquireAsync(() =>
    {
        if (!ReferenceEquals(lease.Set, lease.Resource.Current))
        {
            throw new InvalidOperationException(
                "The voice model set backing this call was reset by an engine release before the call could start.");
        }
        _activeSessions[session] = lease.Resource;
        return Task.CompletedTask;
    }, cancel);

    /// <summary>Releases the hold <see cref="AcquireAsync"/> took for <paramref name="session"/>. Call once the
    /// session has ended (or failed to start), after <see cref="RegisterSession"/>.</summary>
    public async Task ReleaseSessionAsync(VoiceAgentSession session)
    {
        if (_activeSessions.TryRemove(session, out LazyIdleResource<VoiceModelSet> resource))
        {
            await resource.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>An engine release (free memory, backend switch) revokes this set's speech leases exactly like it
    /// revokes the resident TTS/STT pins; unlike those, <c>VoiceGpuWorker</c> would quietly reopen them on the next
    /// job rather than staying evicted. End every active session first -- clients see an Error event and the
    /// session end, same as any other mid-call failure -- then drop the set itself, bypassing the idle wait.</summary>
    private void OnEngineReleased()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EndActiveSessionsAndDisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab][Voice] Ending active voice sessions after an engine release failed: {ex.Message}");
            }
        });
    }

    /// <summary>Runs entirely inside <see cref="AcquireTeardownGate.TeardownAsync"/>: an <see cref="AcquireAsync"/>
    /// already in flight finishes (and is reflected in the snapshot below) before this starts, and no new one can
    /// start until this -- snapshot, end every session, force-dispose -- is done.</summary>
    private Task EndActiveSessionsAndDisposeAsync() => _gate.TeardownAsync(async () =>
    {
        VoiceAgentSession[] sessions = [.. _activeSessions.Keys];
        if (sessions.Length > 0)
        {
            Logs.Info($"[AudioLab][Voice] The audio engine was released; ending {sessions.Length} active voice session(s) so they cannot re-claim its VRAM.");
            await Task.WhenAll(sessions.Select(EndSessionSafelyAsync)).ConfigureAwait(false);
        }
        if (_resource is not null)
        {
            await _resource.ForceDisposeAsync().ConfigureAwait(false);
            _loadedVoice = null;
        }
    });

    private static async Task EndSessionSafelyAsync(VoiceAgentSession session)
    {
        try
        {
            await session.EndAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab][Voice] Ending a session for an engine release threw: {ex.Message}");
        }
    }

    private static async Task<VoiceModelSet> CreateSetAsync(string voice, ITextService warmText, CancellationToken cancel)
    {
        InferenceEngine engine = ResolveConcreteEngine();
        string audioDevice = engine.ComputeBackend.Device.ToString();
        string wakeRoot = WakeWordService.ModelRoot();
        // Shared with the wake-word feature: its F32 RNNoise weights (if already installed) and its Silero VAD
        // are reused here rather than fetched a second time under a separate root. LoadFrontEnd fails closed if
        // the VAD is missing -- the wake tab's own installer may never have run -- so ensure it ourselves too.
        if (!WakeWordService.VadInstalled)
        {
            Logs.Info("[AudioLab][Voice] Installing the Silero VAD the voice session needs for endpointing...");
            await WakeWordService.InstallVadAsync(_ => Task.CompletedTask, cancel).ConfigureAwait(false);
        }
        await RnnoiseInstaller.EnsureAsync(wakeRoot, cancel, RnnoisePrecision.Int8).ConfigureAwait(false);
        VoiceAgentOptions options = new()
        {
            // Inert locally: RemoteTextService ignores both the resolved ModelSpec and TextRequest.Device, so
            // this never needs to name a real local model or device -- LLMAssistant picks its own.
            LlmModel = "remote",
            LlmDevice = audioDevice,
            AudioDevice = audioDevice,
            SttModel = "whisper:openai/whisper-small.en",
            TtsModel = $"kokoro:{voice}",
            Denoise = true,
        };
        VoiceModelSet set = await VoiceModelSet.LoadAsync(engine, options, wakeRoot, cancel).ConfigureAwait(false);
        try
        {
            // A tool-less warm-up: this PR's sessions are always built with an empty ToolRegistry (LLMAssistant
            // owns tool dispatch), so there is no tool-aware grammar path to warm here.
            await set.WarmAsync(warmText, tools: null, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WarmAsync's LLM round is a real loopback call to LLMAssistant; the audio half (Whisper/Kokoro/VAD)
            // has already finished warming by the time Task.WhenAll surfaces this (it waits for both regardless
            // of which one throws), so the set is still fully usable -- just without that one round having been
            // exercised ahead of the first real caller.
            Logs.Warning($"[AudioLab][Voice] Warm-up's language-model round failed (is LLMAssistant installed and reachable?); "
                + $"continuing with the audio side already warm: {ex.Message}");
        }
        return set;
    }

    private static ValueTask DisposeSetAsync(VoiceModelSet set) => set.DisposeAsync();

    private static InferenceEngine ResolveConcreteEngine()
    {
        if (AudioEngineBridge.Engine is InferenceEngine concrete)
        {
            return concrete;
        }
        // VoiceModelSet.LoadAsync is hard-coded to the concrete InferenceEngine class rather than
        // IInferenceEngine (it reads .ComputeBackend, which the interface does not expose at all), but
        // AudioEngineBridge exposes only the interface. AudioEngineBridge.Engine has only ever constructed
        // `new InferenceEngine(...)`, so this cast is safe today; this message is here so a future change to
        // either side fails loudly instead of silently misbehaving.
        throw new InvalidOperationException(
            $"AudioEngineBridge.Engine returned a {AudioEngineBridge.Engine.GetType().Name}, not the concrete "
            + "HartsyInference.Engine.InferenceEngine that HartsyInference.Voice.VoiceModelSet.LoadAsync requires.");
    }
}
