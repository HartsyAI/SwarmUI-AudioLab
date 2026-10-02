using HartsyInference.Core.Configuration;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioBackends;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Streaming;
using HartsyInference.Core.Exceptions;
using HartsyInference.Core.MemoryManagement;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>What Engine service an AudioLab provider is served by.</summary>
public enum AudioEngineService
{
    /// <summary>Text-to-speech (<see cref="ISpeechService"/>).</summary>
    Speech,
    /// <summary>Speech-to-text (<see cref="ITranscribeService"/>).</summary>
    Transcribe,
    /// <summary>Text-to-music (<see cref="IMusicService"/>).</summary>
    Music,
    /// <summary>Voice conversion (<see cref="IVoiceConversionService"/>).</summary>
    VoiceConversion,
    /// <summary>Stem separation (<see cref="IFxService.SeparateAsync"/>).</summary>
    Separate,
    /// <summary>Speech enhancement (<see cref="IFxService.EnhanceAsync"/>).</summary>
    Enhance,
}

/// <summary>One AudioLab provider's binding to the Engine: which typed service runs it, which Engine catalog id
/// it resolves to, and whether the Engine downloads its weights itself (HuggingFace cache) or expects a
/// user-placed checkpoint under <c>{Models}/audio/…</c>.</summary>
/// <param name="EngineId">Engine audio-catalog id — the part before ':' in an Engine model token.</param>
/// <param name="Service">The Engine service that runs this provider.</param>
/// <param name="ManagesOwnWeights">True when the Engine HF-auto-downloads the weights on first load.</param>
public sealed record AudioEngineBinding(string EngineId, AudioEngineService Service, bool ManagesOwnWeights);

/// <summary>AudioLab's thin adapter over <see cref="IInferenceEngine"/>: owns one engine instance, maps a provider
/// id to an Engine model token, converts AudioLab's request args to the Engine's typed audio requests, and shapes
/// the typed results back into the JObject AudioLab's generation path parses.
///
/// <para>It contains NO inference logic: model lifecycle, weight loading, the generation lock, memory-pressure
/// eviction, decoding and encoding all live in <c>HartsyInference.Engine</c> now.</para></summary>
public static class AudioEngineBridge
{
    /// <summary>Provider id → Engine binding. A provider missing here is not runnable in-process (see
    /// <see cref="AudioUnsupportedReasons"/>); cloud API providers never reach this class at all.</summary>
    private static readonly Dictionary<string, AudioEngineBinding> _bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Speech-to-text.
        ["whisper_stt"] = new AudioEngineBinding("whisper", AudioEngineService.Transcribe, true),
        ["distilwhisper_stt"] = new AudioEngineBinding("distilwhisper", AudioEngineService.Transcribe, true),
        ["moonshine_stt"] = new AudioEngineBinding("moonshine", AudioEngineService.Transcribe, true),
        ["moonshinestreaming_stt"] = new AudioEngineBinding("moonshinestreaming", AudioEngineService.Transcribe, true),
        ["kyutaistt_stt"] = new AudioEngineBinding("kyutaistt", AudioEngineService.Transcribe, true),
        ["whisperstreaming_stt"] = new AudioEngineBinding("whisperstreaming", AudioEngineService.Transcribe, true),
        // Music, not speech: it writes a lead sheet. The transcribe service is still what runs it.
        ["sheetsage2_transcribe"] = new AudioEngineBinding("sheetsage2", AudioEngineService.Transcribe, true),
        // Text-to-speech.
        ["vibevoice_tts"] = new AudioEngineBinding("vibevoice", AudioEngineService.Speech, true),
        ["kokoro_tts"] = new AudioEngineBinding("kokoro", AudioEngineService.Speech, true),
        ["bark_tts"] = new AudioEngineBinding("bark", AudioEngineService.Speech, true),
        ["dia_tts"] = new AudioEngineBinding("dia", AudioEngineService.Speech, true),
        ["orpheus_tts"] = new AudioEngineBinding("orpheus", AudioEngineService.Speech, true),
        ["csm_tts"] = new AudioEngineBinding("csm", AudioEngineService.Speech, true),
        ["neutts_tts"] = new AudioEngineBinding("neutts", AudioEngineService.Speech, true),
        ["fishspeech_tts"] = new AudioEngineBinding("fishspeech", AudioEngineService.Speech, true),
        ["cosyvoice_tts"] = new AudioEngineBinding("cosyvoice", AudioEngineService.Speech, true),
        ["f5_tts"] = new AudioEngineBinding("f5", AudioEngineService.Speech, true),
        ["zipvoice_tts"] = new AudioEngineBinding("zipvoice", AudioEngineService.Speech, true),
        ["qwen3_tts"] = new AudioEngineBinding("qwen3tts", AudioEngineService.Speech, true),
        ["chatterbox_tts"] = new AudioEngineBinding("chatterbox", AudioEngineService.Speech, true),
        ["kyutaitts_tts"] = new AudioEngineBinding("kyutaitts", AudioEngineService.Speech, true),
        ["piper_tts"] = new AudioEngineBinding("piper", AudioEngineService.Speech, true),
        ["melotts_tts"] = new AudioEngineBinding("melotts", AudioEngineService.Speech, true),
        ["sparktts_tts"] = new AudioEngineBinding("sparktts", AudioEngineService.Speech, true),
        ["pockettts_tts"] = new AudioEngineBinding("pockettts", AudioEngineService.Speech, true),
        ["styletts2_tts"] = new AudioEngineBinding("styletts2", AudioEngineService.Speech, true),
        ["zonos_tts"] = new AudioEngineBinding("zonos", AudioEngineService.Speech, true),
        // GPT-SoVITS is a clone-category provider, but it is text+reference→speech, so the speech service runs it.
        ["gptsovits_clone"] = new AudioEngineBinding("gptsovits", AudioEngineService.Speech, true),
        // Music / SFX.
        ["musicgen_music"] = new AudioEngineBinding("musicgen", AudioEngineService.Music, true),
        ["audiogen_sfx"] = new AudioEngineBinding("audiogen", AudioEngineService.Music, true),
        ["acestep_music"] = new AudioEngineBinding("acestep", AudioEngineService.Music, false),
        ["yue_music"] = new AudioEngineBinding("yue", AudioEngineService.Music, false),
        ["yue2_music"] = new AudioEngineBinding("yue2", AudioEngineService.Music, true),
        ["heartlib_music"] = new AudioEngineBinding("heartmula", AudioEngineService.Music, true),
        ["stableaudio_music"] = new AudioEngineBinding("stableaudio", AudioEngineService.Music, true),
        // Self-downloading: the engine fetches the diffusers-format subfolders on first generation.
        ["minimax_music3"] = new AudioEngineBinding("minimaxmusic3", AudioEngineService.Music, true),
        // Voice conversion.
        ["rvc_clone"] = new AudioEngineBinding("rvc", AudioEngineService.VoiceConversion, false),
        ["openvoice_clone"] = new AudioEngineBinding("openvoice", AudioEngineService.VoiceConversion, true),
        // Effects.
        // FxCatalog.EnsureDemucsPathAsync auto-downloads both variants from Meta's public CDN on first load.
        ["demucs_fx"] = new AudioEngineBinding("demucs", AudioEngineService.Separate, true),
        ["resemble_enhance_fx"] = new AudioEngineBinding("resemble-enhance", AudioEngineService.Enhance, true),
    };

    /// <summary>Engine-managed providers whose weights the Engine fetches from a HuggingFace repo that the
    /// provider's <c>SourceUrl</c> does not name (SourceUrl is a human "learn more" link, usually GitHub).
    /// Without this the repo can't be resolved, <see cref="GetWeightLocations"/> returns nothing, and
    /// <see cref="WeightsPresent"/> reports every model installed regardless of what's on disk.
    /// Values must match the repo the matching engine descriptor downloads from.</summary>
    private static readonly Dictionary<string, string> _engineWeightRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chatterbox_tts"] = "ResembleAI/chatterbox",
        ["piper_tts"] = "rhasspy/piper-voices",
        ["pockettts_tts"] = "kyutai/pocket-tts-without-voice-cloning",
        ["gptsovits_clone"] = "lj1995/GPT-SoVITS",
        ["openvoice_clone"] = "myshell-ai/OpenVoiceV2",
        ["resemble_enhance_fx"] = "ResembleAI/resemble-enhance",
        ["yue2_music"] = "Comfy-Org/YuE2",
        ["sheetsage2_transcribe"] = "Comfy-Org/YuE2",
    };

    /// <summary>Engine-managed providers cached outside their own AudioLab category, mapped to the Engine's
    /// cache category and the one file whose presence answers "installed". SheetSage2 is an STT-category
    /// provider whose weights ride inside the YuE2 repo under "music", and YuE2's own files being there says
    /// nothing about whether the encoder was fetched.</summary>
    private static readonly Dictionary<string, (string Category, string File)> _engineWeightFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sheetsage2_transcribe"] = ("music", "audio_encoders/sheetsage2_bf16.safetensors"),
    };

    /// <summary>Engine-managed providers that don't use the HuggingFace cache at all, mapped to the
    /// (category, folder) the Engine writes them to. Demucs comes from Meta's CDN via FxCatalog.</summary>
    private static readonly Dictionary<string, (string Category, string Folder)> _engineWeightFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["demucs_fx"] = ("fx", "demucs"),
    };

    private static IInferenceEngine _engine;
    private static readonly object _engineLock = new();

    /// <summary>Always true — the Engine is compiled in. Device readiness is <see cref="EngineReady"/>.</summary>
    public static bool Available => true;

    /// <summary>The shared Engine instance, constructed on first use. The Engine picks and owns the compute
    /// backend itself ("auto" = CUDA → Vulkan → CPU) and constructs it lazily on the first generation.</summary>
    public static IInferenceEngine Engine
    {
        get
        {
            if (_engine is not null)
            {
                return _engine;
            }
            lock (_engineLock)
            {
                if (_engine is null)
                {
                    AlignModelsRoot();
                    string device = ResolveDeviceLoudly(DeviceSelector());
                    VramPolicy policy = VramPolicySelector();
                    _engine = new InferenceEngine(device, new EngineOptions { VramPolicy = policy });
                    _builtDevice = device;
                    _builtVramMode = _requestedVramMode;
                    Logs.Init($"[AudioLab] HartsyInference engine created for device '{device}' "
                        + $"(backend: {_engine.BackendDescription}, VRAM mode: {_requestedVramMode ?? "Auto"}).");
                }
                return _engine;
            }
        }
    }

    /// <summary>Whether the Engine can be reached. The Engine builds its device lazily inside the first
    /// generation, so this can only report that the engine object exists; a device failure surfaces as a
    /// generation error naming the backend problem.</summary>
    public static bool EngineReady() => Engine is not null;

    /// <summary>Whether the named provider is runnable in-process.</summary>
    public static bool IsProviderSupported(string providerId)
        => providerId is not null && _bindings.ContainsKey(providerId);

    /// <summary>Whether the Engine downloads/manages this provider's weights itself (HF auto-download).</summary>
    public static bool ProviderManagesOwnWeights(string providerId)
        => providerId is not null && _bindings.TryGetValue(providerId, out AudioEngineBinding binding) && binding.ManagesOwnWeights;

    /// <summary>Whether this provider has a real Engine-native streaming implementation
    /// (<c>IStreamingTtsRunner</c>, incremental audio as generation happens) rather than AudioLab's own
    /// text-chunk-and-regenerate-each-piece loop. Read off the provider's own <c>tts_streaming</c> feature flag —
    /// the same flag that gates the streaming-specific UI, so adding native streaming for a model always means
    /// adding the flag on its <see cref="AudioProviderDefinition"/> too (see <c>KyutaiTTSProvider.cs</c>) and
    /// there is exactly one place that says a model streams natively, not two that can drift apart.</summary>
    public static bool SupportsNativeStreaming(string providerId)
    {
        if (string.IsNullOrEmpty(providerId) || !_bindings.TryGetValue(providerId, out AudioEngineBinding binding) || binding.Service != AudioEngineService.Speech)
        {
            return false;
        }
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        return provider is not null && provider.FeatureFlags.Contains("tts_streaming", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs an audio request on the Engine, returning the JObject shape AudioLab's generation path
    /// parses (<c>success</c> + <c>audio_data</c>/<c>text</c>/<c>stems</c>, or <c>error</c>).
    ///
    /// <para>Wrapped in <see cref="RunWithVramRecoveryAsync{T}"/>: SwarmUI's image/video backends and AudioLab
    /// share one card, each with its own Engine instance and no coordination, so a GPU that still holds an
    /// image model resident after a generation can leave too little free for an audio model to load — see the
    /// backend card's <c>Coordinate Vram On Out Of Memory</c> setting.</para></summary>
    public static async Task<JObject> ProcessAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        try
        {
            return await RunWithVramRecoveryAsync(
                () => DispatchAsync(providerId, binding, args, cancel),
                _coordinateVramOnOom,
                FreeMemory,
                FreeIdleOtherBackendsAsync,
                Task.Delay,
                msg => Logs.Warning(msg),
                cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return AudioIo.Cancelled();
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Audio provider '{providerId}' failed: {ex}");
            return AudioIo.Error(ex.Message);
        }
    }

    /// <summary>The actual per-service Engine dispatch -- split out of <see cref="ProcessAsync"/> so
    /// <see cref="RunWithVramRecoveryAsync{T}"/> can run it a second time verbatim after freeing memory,
    /// without duplicating the switch. Unchanged from before VRAM recovery existed.</summary>
    private static async Task<JObject> DispatchAsync(string providerId, AudioEngineBinding binding,
        IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        ModelSpec spec = BuildSpec(providerId, binding, args);
        switch (binding.Service)
        {
            case AudioEngineService.Speech:
                await MaybeKeepResidentAsync(AudioEngineService.Speech, spec, cancel).ConfigureAwait(false);
                return Audio(await Engine.Speech.SynthesizeAsync(spec, AudioEngineRequests.Speech(args), cancel).ConfigureAwait(false));
            case AudioEngineService.Transcribe:
            {
                await MaybeKeepResidentAsync(AudioEngineService.Transcribe, spec, cancel).ConfigureAwait(false);
                TranscriptResult transcript = await Engine.Transcribe
                    .RunAsync(spec, AudioEngineRequests.Transcribe(args), cancel).ConfigureAwait(false);
                return AudioIo.TranscriptionResult(transcript.Text, transcript.Language);
            }
            case AudioEngineService.Music:
                return Audio(await Engine.Music.GenerateAsync(spec, AudioEngineRequests.Music(args), null, cancel).ConfigureAwait(false));
            case AudioEngineService.VoiceConversion:
                return Audio(await Engine.VoiceConversion.ConvertAsync(spec, AudioEngineRequests.VoiceConversion(args), cancel).ConfigureAwait(false));
            case AudioEngineService.Separate:
            {
                StemsResult stems = await Engine.Fx
                    .SeparateAsync(spec, AudioEngineRequests.Separate(args), cancel).ConfigureAwait(false);
                List<(string, string)> encoded = new(stems.Stems.Count);
                foreach (KeyValuePair<string, byte[]> stem in stems.Stems)
                {
                    encoded.Add((stem.Key, Convert.ToBase64String(stem.Value)));
                }
                return AudioIo.StemsResult(encoded);
            }
            case AudioEngineService.Enhance:
                return Audio(await Engine.Fx.EnhanceAsync(spec, AudioEngineRequests.Enhance(args), cancel).ConfigureAwait(false));
            default:
                return AudioIo.Error($"Provider '{providerId}' has no Engine service wired.");
        }
    }

    #region VRAM coordination with other SwarmUI backends (OOM evict-and-retry)

    /// <summary>Whether <see cref="ProcessAsync"/> reacts to <see cref="OutOfVramException"/> by evicting
    /// AudioLab's own idle models, asking other idle SwarmUI backends to free theirs, and retrying once. On by
    /// default; set from <c>DynamicAudioSettings.CoordinateVramOnOutOfMemory</c> via
    /// <see cref="RequestVramCoordination"/>.</summary>
    private static volatile bool _coordinateVramOnOom = true;

    /// <summary>Turns VRAM-OOM recovery on or off. Always takes effect immediately (unlike Device/VramMode,
    /// there is no engine to rebuild) -- called from <c>DynamicAudioBackend.ApplyDeviceSetting</c> the same
    /// place <see cref="RequestKeepResident"/> is.</summary>
    public static void RequestVramCoordination(bool enabled) => _coordinateVramOnOom = enabled;

    /// <summary>How long to wait after freeing memory and before retrying. <see cref="AbstractBackend.FreeMemory"/>'s
    /// own contract: "some backends may take extra time between when this call returns and when memory is
    /// actually freed ... generally give at least one full second." Retrying immediately would race that and
    /// could fail the retry for a reason the wait alone would have avoided.</summary>
    private static readonly TimeSpan VramSettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>Runs <paramref name="operation"/>; on <see cref="OutOfVramException"/>, while
    /// <paramref name="enabled"/>, evicts AudioLab's own models (<paramref name="evictOwnModels"/>), then asks
    /// other idle SwarmUI backends to free theirs (<paramref name="freeOtherBackends"/>), waits
    /// <paramref name="delay"/> for the free to actually land, logs what happened, and retries
    /// <paramref name="operation"/> exactly once -- a second failure of any kind propagates unchanged.
    /// <paramref name="enabled"/> is a plain snapshot, not re-read mid-call: this decides the retry policy for
    /// one request, not a pin that can be raced by a concurrent setting change. A <paramref name="cancel"/>
    /// already signaled when the OOM lands skips recovery entirely -- there is no point evicting models for a
    /// request about to be cancelled anyway.
    ///
    /// <para><c>internal</c> and parameterized over every side effect (same reasoning as
    /// <see cref="OpenResidentPinCoreAsync{TLease}"/>) so a test can drive the eviction order, the retry-once
    /// limit, and "setting off" with fakes -- no live Engine, no real SwarmUI backends, no GPU, no real
    /// delay.</para></summary>
    internal static async Task<T> RunWithVramRecoveryAsync<T>(
        Func<Task<T>> operation,
        bool enabled,
        Action evictOwnModels,
        Func<CancellationToken, Task<IReadOnlyList<string>>> freeOtherBackends,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log,
        CancellationToken cancel)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OutOfVramException ex) when (enabled)
        {
            cancel.ThrowIfCancellationRequested();
            log($"[AudioLab] {ex.Message} Evicting idle audio models and asking other idle backends to free memory; retrying once.");
            evictOwnModels();
            IReadOnlyList<string> freed = await freeOtherBackends(cancel).ConfigureAwait(false);
            log(freed.Count > 0
                ? $"[AudioLab] Freed {freed.Count} other idle backend(s): {string.Join(", ", freed)}."
                : "[AudioLab] No other idle backend had memory to free.");
            await delay(VramSettleDelay, cancel).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
    }

    /// <summary>One backend <see cref="FreeIdleOtherBackendsCoreAsync"/> can consider freeing: a label for
    /// logging, whether it is AudioLab's own (skip -- <see cref="FreeMemory"/> already handles those, and
    /// double-freeing through the generic path would be redundant at best), whether it is currently idle (not
    /// mid-generation, not reserved for a model load -- never true for a backend that might be busy), and the
    /// actual free action. A record rather than a real <see cref="AbstractBackend"/> so a test can supply fakes
    /// instead of a live SwarmUI backend registry.</summary>
    internal readonly record struct VramBackendCandidate(string Name, bool IsAudioLabOwned, bool IsIdle, Func<Task<bool>> FreeMemoryAsync);

    /// <summary>Decides which candidates to free and does it: every one that is idle and not AudioLab's own.
    /// Mirrors what <c>BackendAPI.FreeBackendMemory</c> (the <c>/API/FreeBackendMemory</c> endpoint) does
    /// internally -- iterate running backends, call <see cref="AbstractBackend.FreeMemory"/> -- but adds the
    /// busy check that endpoint does NOT have: it frees every running backend unconditionally, which would risk
    /// interrupting a generation genuinely in flight on another backend. A candidate whose own
    /// <see cref="VramBackendCandidate.FreeMemoryAsync"/> throws is logged and skipped, not fatal to the rest.
    /// <c>internal</c> for the same testability reason as <see cref="RunWithVramRecoveryAsync{T}"/>.</summary>
    /// <returns>The names of the backends that reported they actually freed something.</returns>
    internal static async Task<IReadOnlyList<string>> FreeIdleOtherBackendsCoreAsync(
        IEnumerable<VramBackendCandidate> candidates, Action<string> log, CancellationToken cancel)
    {
        List<string> freed = [];
        foreach (VramBackendCandidate candidate in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (candidate.IsAudioLabOwned || !candidate.IsIdle)
            {
                continue;
            }
            try
            {
                if (await candidate.FreeMemoryAsync().ConfigureAwait(false))
                {
                    freed.Add(candidate.Name);
                }
            }
            catch (Exception ex)
            {
                log($"[AudioLab] FreeMemory on backend '{candidate.Name}' threw: {ex.Message}");
            }
        }
        return freed;
    }

    /// <summary>Whether a backend counts as idle for VRAM recovery: genuinely running (not loading, errored,
    /// disabled or shutting down) and neither reserved for a model load nor claimed by any in-flight request.
    /// Reconstructed independently from the three raw signals rather than read off
    /// <see cref="BackendHandler.BackendData.CheckIsInUseAtAll"/> directly, so this exact "is it safe to touch"
    /// decision has its own unit tests instead of only ever being exercised through a live
    /// <see cref="AbstractBackend"/>.</summary>
    internal static bool IsIdleCandidate(BackendStatus status, bool reserveModelLoad, int usages) =>
        status == BackendStatus.RUNNING && !reserveModelLoad && usages <= 0;

    /// <summary>Real wiring for <see cref="FreeIdleOtherBackendsCoreAsync"/>: every currently-running SwarmUI
    /// backend (<see cref="BackendHandler.RunningBackendsOfType{T}"/>), idle per <see cref="IsIdleCandidate"/>,
    /// with every <see cref="DynamicAudioBackend"/> instance excluded (that is AudioLab's own;
    /// <see cref="FreeMemory"/> already covers it). Freed with <c>systemRam: false</c> -- this is a VRAM
    /// problem, not a host-RAM one, and clearing filename-block history or RAM caches on an unrelated backend
    /// is not this feature's business.</summary>
    private static Task<IReadOnlyList<string>> FreeIdleOtherBackendsAsync(CancellationToken cancel)
    {
        IEnumerable<VramBackendCandidate> candidates = Program.Backends.RunningBackendsOfType<AbstractBackend>()
            .Select(backend =>
            {
                BackendHandler.BackendData data = backend.AbstractBackendData;
                string name = $"{data?.BackType?.Name ?? backend.GetType().Name} #{data?.ID}";
                bool idle = data is not null && IsIdleCandidate(backend.Status, data.ReserveModelLoad, data.Usages);
                return new VramBackendCandidate(name, backend is DynamicAudioBackend, idle, () => backend.FreeMemory(systemRam: false));
            });
        return FreeIdleOtherBackendsCoreAsync(candidates, msg => Logs.Debug(msg), cancel);
    }

    #endregion

    /// <summary>Plans a provider's symbolic score without rendering it.
    ///
    /// <para>YuE2 composes in two passes — an autoregressive model writes an ABC score, then a second pass turns
    /// that score into sound — and the score is the only editable artifact it exposes. Planning alone takes
    /// seconds where the full render takes minutes, so the Score tab asks for one, lets the user edit it, and
    /// feeds it back through <c>yue2_abc</c>.</para></summary>
    public static async Task<JObject> PlanScoreAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
        => await SymbolicAsync(providerId, args, cancel,
            (spec, request, ct) => Engine.Music.PlanScoreAsync(spec, request, ct)).ConfigureAwait(false);

    /// <summary>Reports what the context leaves for audio behind a prompt and score, without running the planner.</summary>
    public static async Task<JObject> ScoreBudgetAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
        => await SymbolicAsync(providerId, args, cancel,
            (spec, request, ct) => Engine.Music.BudgetAsync(spec, request, ct)).ConfigureAwait(false);

    /// <summary>Transcribes a recording into a score, returning both of its renderings from one decode.
    ///
    /// <para>The plain transcribe path returns the chord-annotated ABC as text, which is enough to read but not
    /// to cover: the melody-only rendering is a different serialization of the same events, not the same string
    /// with its chord symbols deleted, so it cannot be recovered client-side. The decode is the entire cost, so
    /// the model is asked once and writes both.</para></summary>
    public static async Task<JObject> TranscribeScoreAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        if (binding.Service != AudioEngineService.Transcribe)
        {
            return AudioIo.Error($"Provider '{providerId}' does not listen to audio — only transcription models read a score off a recording.");
        }
        try
        {
            ModelSpec spec = BuildSpec(providerId, binding, args);
            return AudioIo.ScoreTranscript(await Engine.Transcribe
                .RunScoreAsync(spec, AudioEngineRequests.Transcribe(args), cancel).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return AudioIo.Cancelled();
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Score transcription for '{providerId}' failed: {ex}");
            return AudioIo.Error(ex.Message);
        }
    }

    /// <summary>Shared path for the symbolic music calls, mirroring <see cref="ProcessAsync"/>'s error handling.
    /// A provider that does not plan a score surfaces the Engine's own NotSupportedException as a plain error.</summary>
    private static async Task<JObject> SymbolicAsync(string providerId, IReadOnlyDictionary<string, object> args,
        CancellationToken cancel, Func<ModelSpec, MusicRequest, CancellationToken, Task<ScorePlanResult>> run)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        if (binding.Service != AudioEngineService.Music)
        {
            return AudioIo.Error($"Provider '{providerId}' does not write a score — only music models plan one.");
        }
        try
        {
            ModelSpec spec = BuildSpec(providerId, binding, args);
            return AudioIo.ScorePlan(await run(spec, AudioEngineRequests.Music(args), cancel).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return AudioIo.Cancelled();
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Score planning for '{providerId}' failed: {ex}");
            return AudioIo.Error(ex.Message);
        }
    }

    /// <summary>Shapes an Engine audio result into AudioLab's success JObject. The Engine already returns encoded
    /// container bytes, so this is just base64.</summary>
    private static JObject Audio(AudioResult result)
        => AudioIo.AudioResult(Convert.ToBase64String(result.Data), result.Format, result.DurationSeconds, result.Meta);

    /// <summary>Streams a TTS provider's audio incrementally via the Engine's native streaming path
    /// (<c>ISpeechService.SynthesizeStreamAsync</c>) — one call with the full prompt, chunks arrive as generation
    /// progresses. Callers MUST check <see cref="SupportsNativeStreaming"/> first; this throws for anything else
    /// (unlike <see cref="ProcessAsync"/>, which converts every failure into a JObject error — there is no
    /// per-chunk JObject shape here for a raw <see cref="AudioChunk"/> stream, so the caller owns catching and
    /// reporting exceptions the way <c>DynamicAudioBackend.GenerateLiveNativeStreaming</c> does).</summary>
    public static async IAsyncEnumerable<AudioChunk> ProcessStreamAsync(string providerId,
        IReadOnlyDictionary<string, object> args, [EnumeratorCancellation] CancellationToken cancel)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding) || binding.Service != AudioEngineService.Speech)
        {
            throw new InvalidOperationException($"Provider '{providerId}' has no native streaming Engine binding.");
        }
        ModelSpec spec = BuildSpec(providerId, binding, args);
        await MaybeKeepResidentAsync(AudioEngineService.Speech, spec, cancel).ConfigureAwait(false);
        await foreach (AudioChunk chunk in Engine.Speech.SynthesizeStreamAsync(spec, AudioEngineRequests.Speech(args), cancel).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>Builds the Engine model token for a request: <c>engineId</c> or <c>engineId:variant</c>, where the
    /// variant is AudioLab's model id (<c>__model_id</c>, injected by <c>BuildEngineArgs</c>). For user-placed
    /// checkpoints the resolved local path is attached too, because AudioLab installs them under
    /// <c>{Models}/audio/{category}/{ModelPrefix}</c> ("AceStep", "RVC", …) while the Engine's own convention is
    /// its lowercase family id — on a case-sensitive filesystem those differ, so the path must be explicit.</summary>
    private static ModelSpec BuildSpec(string providerId, AudioEngineBinding binding, IReadOnlyDictionary<string, object> args)
    {
        string modelId = AudioIo.Str(args, "__model_id");
        string token = string.IsNullOrEmpty(modelId) ? binding.EngineId : $"{binding.EngineId}:{modelId}";
        return new ModelSpec
        {
            Requested = token,
            // The audio services key off the token and local path only; Modality is carried for diagnostics, so
            // voice-conversion and effects (which have no Modality of their own) ride under Speech.
            Modality = binding.Service switch
            {
                AudioEngineService.Transcribe => Modality.Transcribe,
                AudioEngineService.Music => Modality.Music,
                _ => Modality.Speech,
            },
            LocalPath = binding.ManagesOwnWeights ? null : ResolvePlacedCheckpoint(providerId, modelId),
        };
    }

    /// <summary>The on-disk checkpoint AudioLab installed (or the user dropped in) for a placed-weights provider,
    /// or null to let the Engine resolve its own default location.</summary>
    private static string ResolvePlacedCheckpoint(string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        if (provider is null || string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        try
        {
            string directory = AudioWeights.WeightsDirectory(provider);
            // Folder checkpoints (YuE) load a whole variant directory.
            if (AudioWeightsRegistry.IsFolderCheckpoint(providerId))
            {
                string folder = Path.Combine(directory, modelId);
                return Directory.Exists(folder) ? folder : null;
            }
            // Registry-backed variants (ACE-Step): the primary spec is the weights file.
            AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor(providerId, modelId);
            if (specs.Length > 0)
            {
                string primary = Path.Combine(directory, specs[0].FileName);
                return File.Exists(primary) ? primary : null;
            }
            // User-placed models with no download registry (Demucs, RVC voices): probe the accepted extensions.
            foreach (string extension in new[] { "", ".safetensors", ".pth", ".pt", ".th" })
            {
                string candidate = Path.Combine(directory, modelId + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Could not resolve a placed checkpoint for '{providerId}/{modelId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Prefetches an HF-auto-download provider's weights ahead of the first generation.
    ///
    /// <para><b>Engine gap:</b> the Engine exposes no public "ensure/prefetch this audio model" API — its audio
    /// catalogs and their repo resolution are internal, and the only way to pull the weights is to load the model,
    /// which is reachable only through a real generation. So this reports the state truthfully instead of
    /// pretending to download: the weights fetch inside the Engine's loader on the first generation. Restore the
    /// install-time download (with progress) once the Engine ships a prefetch entry point.</para></summary>
    public static async Task<JObject> EnsureWeightsAsync(string providerId, string modelId, Func<string, Task> onProgress, CancellationToken cancel)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        cancel.ThrowIfCancellationRequested();
        if (!binding.ManagesOwnWeights)
        {
            onProgress?.Invoke($"Place the '{modelId ?? binding.EngineId}' checkpoint; it loads on first generation.");
            return new JObject { ["success"] = true };
        }
        ModelSpec spec = BuildSpec(providerId, binding, new Dictionary<string, object> { ["__model_id"] = modelId ?? "" });
        try
        {
            Progress<AudioFetchProgress> progress = new(p => onProgress?.Invoke($"[{p.FilesDone}/{p.FileCount}] {p.File}"));
            ModelPrefetchResult result = await Engine.ModelPrefetch.PrefetchAsync(spec, progress, cancel).ConfigureAwait(false);
            onProgress?.Invoke(result.Message);
            if (!result.Supported)
            {
                // Truthful, not fatal: the model still works, its weights just arrive on first generation.
                return new JObject { ["success"] = true, ["prefetched"] = false, ["detail"] = result.Message };
            }
            AudioArtifactIdentity.WriteSidecar(result.PrimaryPath, providerId, modelId, binding.EngineId);
            return new JObject { ["success"] = true, ["prefetched"] = true, ["files"] = result.Files.Count };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Prefetch failed for '{providerId}/{modelId}': {ex.Message}");
            return AudioIo.Error($"Download failed for '{modelId ?? binding.EngineId}': {ex.Message}");
        }
    }

    /// <summary>Raised after <see cref="Unload"/> or <see cref="FreeMemory"/> actually calls
    /// <see cref="IInferenceEngine.FreeMemory"/> -- after <see cref="ClearResidencyPins"/>, on whatever thread
    /// called them. <c>HartsyInference.Voice</c>'s own leases (<c>AudioServices/Voice/VoiceEngineModels</c>) are
    /// revoked by the same <c>FreeMemory</c> call exactly like the resident TTS/STT pins above are, but that
    /// package already re-opens a revoked lease once on its own worker thread -- which would silently re-claim
    /// the VRAM a user just asked to free the moment the next voice turn ran. Subscribing here is what lets that
    /// module end its active sessions and drop its model set instead of letting them quietly resurrect it. A
    /// throwing handler is caught and logged; it never turns a free-memory request into a failure.</summary>
    public static event Action EngineReleased;

    /// <summary>Drops resident audio pipelines to free memory.
    ///
    /// <para><b>Engine gap:</b> the audio services have no per-model <c>Unload</c> (unlike <c>ITextService</c>),
    /// so the only lever is <see cref="IInferenceEngine.FreeMemory"/>, which releases every model this engine
    /// instance holds. That is correct but coarser than the old per-provider eviction; since the instance is
    /// AudioLab-private it only ever drops audio models.</para></summary>
    public static void Unload(string providerId, string modelId)
    {
        if (_engine is null)
        {
            return;
        }
        try
        {
            _engine.FreeMemory();
            Logs.Debug($"[AudioLab] Released resident audio models (requested for '{providerId}/{modelId}').");
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Unload('{providerId}','{modelId}') threw: {ex.Message}");
        }
        finally
        {
            // IInferenceEngine.FreeMemory revokes any open synthesizer/transcriber lease unconditionally (it
            // is one of the engine release paths ISynthesizerLease/ITranscriberLease document as revoking) —
            // there is no selective "free everything except the pin" lever. Forget the pins rather than
            // leave them pointing at revoked leases; the setting re-opens one lazily on the next TTS/STT call
            // via MaybeKeepResidentAsync, instead of racing to reload here and fighting whatever this Unload
            // call was trying to free in the first place.
            ClearResidencyPins();
            RaiseEngineReleased();
        }
    }

    /// <summary>Releases every loaded audio model and its device memory, leaving the engine usable. Used by the
    /// backend's shutdown / free-memory path.</summary>
    public static void FreeMemory()
    {
        if (_engine is null)
        {
            return;
        }
        try
        {
            _engine.FreeMemory();
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Freeing audio engine memory failed: {ex.Message}");
        }
        finally
        {
            ClearResidencyPins();
            RaiseEngineReleased();
        }
    }

    private static void RaiseEngineReleased()
    {
        try
        {
            EngineReleased?.Invoke();
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] An EngineReleased handler threw: {ex.Message}");
        }
    }

    #region Resident TTS/STT leases ("Keep selected TTS/STT resident")

    /// <summary>Whether the backend setting asks AudioLab to keep the last-used TTS and STT models resident
    /// through the engine's memory-pressure eviction, via <see cref="ISpeechService.OpenSynthesizerAsync"/> /
    /// <see cref="ITranscribeService.OpenTranscriberAsync"/>. Off by default (see <see cref="RequestKeepResident"/>).</summary>
    private static bool _keepResident;

    /// <summary>Serializes opening/replacing the two pins — <see cref="ProcessAsync"/>/<see cref="ProcessStreamAsync"/>
    /// calls can race from concurrent requests, and a lease open is not itself atomic with the "is this already
    /// the pinned model" check.</summary>
    private static readonly SemaphoreSlim _residencyLock = new(1, 1);

    private static ISynthesizerLease _pinnedSynth;
    private static string _pinnedSynthKey;
    private static ITranscriberLease _pinnedTranscriber;
    private static string _pinnedTranscriberKey;

    /// <summary>Turns the "keep resident" setting on or off. Disabling it drops whatever is currently pinned
    /// immediately (so the next memory-pressure sweep can evict it again); enabling it only takes effect on the
    /// next TTS/STT call, which is when a spec is next available to pin. Called from
    /// <c>DynamicAudioBackend.Init</c>, the same place <c>RequestDevice</c>/<c>RequestVramMode</c> apply their
    /// settings — see that method for why a settings change here means restarting this backend, not a live
    /// setter.</summary>
    public static void RequestKeepResident(bool enabled)
    {
        _keepResident = enabled;
        if (!enabled)
        {
            ClearResidencyPins();
        }
    }

    /// <summary>Pins <paramref name="spec"/>'s model resident (replacing whichever one of the same kind was
    /// pinned before) when the setting is on; a no-op otherwise. Called before the matching
    /// <c>Engine.Speech</c>/<c>Engine.Transcribe</c> service call in <see cref="ProcessAsync"/> and
    /// <see cref="ProcessStreamAsync"/>, so the model about to run is itself protected from memory-pressure
    /// eviction, not just whatever ran last.
    ///
    /// <para>The lease is held only as a pin — nothing calls <c>ISynthesizerLease.Synthesize</c>/
    /// <c>ITranscriberLease.Transcribe</c> through it. Generation keeps going through the normal
    /// <c>SynthesizeAsync</c>/<c>SynthesizeStreamAsync</c>/<c>RunAsync</c> service path, which alpha.223
    /// documents as working alongside an open lease on the same model; routing the actual audio through the
    /// lease instead would lose <c>SynthesizeStreamAsync</c>'s incremental chunks (the lease's <c>Synthesize</c>
    /// returns one <c>float[]</c>) and would need this class to hold <c>DeviceGate</c> itself, which the service
    /// path already does internally.</para>
    ///
    /// <para>Failure (eg a card too small to hold two models at once) only logs — the generation that is about
    /// to run through the normal service path is not affected by a pin that didn't take.</para></summary>
    private static async Task MaybeKeepResidentAsync(AudioEngineService service, ModelSpec spec, CancellationToken cancel)
    {
        if (!_keepResident || spec is null)
        {
            return;
        }
        string key = spec.Requested ?? spec.LocalPath ?? "";
        try
        {
            if (service == AudioEngineService.Speech)
            {
                bool stored = await OpenResidentPinCoreAsync(
                    _residencyLock, () => _keepResident, key,
                    () => (_pinnedSynth, _pinnedSynthKey),
                    (lease, k) => { _pinnedSynth = lease; _pinnedSynthKey = k; },
                    ct => Engine.Speech.OpenSynthesizerAsync(spec, ct),
                    lease => lease.Dispose(),
                    cancel).ConfigureAwait(false);
                if (stored)
                {
                    Logs.Debug($"[AudioLab] Keeping TTS '{key}' resident.");
                }
            }
            else if (service == AudioEngineService.Transcribe)
            {
                bool stored = await OpenResidentPinCoreAsync(
                    _residencyLock, () => _keepResident, key,
                    () => (_pinnedTranscriber, _pinnedTranscriberKey),
                    (lease, k) => { _pinnedTranscriber = lease; _pinnedTranscriberKey = k; },
                    ct => Engine.Transcribe.OpenTranscriberAsync(spec, ct),
                    lease => lease.Dispose(),
                    cancel).ConfigureAwait(false);
                if (stored)
                {
                    Logs.Debug($"[AudioLab] Keeping STT '{key}' resident.");
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not fatal to the caller's generation, which runs through the normal service path regardless —
            // see the method doc. Most likely cause: not enough VRAM/RAM to hold two models of this kind at
            // once (eg switching between two large STT models with keep-resident on).
            Logs.Warning($"[AudioLab] Could not keep '{key}' resident: {ex.Message}");
        }
    }

    /// <summary>Race-safe core of <see cref="MaybeKeepResidentAsync"/>, generic over the lease type so it
    /// serves both <see cref="ISynthesizerLease"/> and <see cref="ITranscriberLease"/> from one implementation,
    /// and <c>internal</c> (rather than a closure inline above) so a test can drive it with a controllable
    /// <paramref name="openAsync"/> and plain local state instead of this class's real static fields and a
    /// live <see cref="Engine"/>.
    ///
    /// <para>Everything happens under <paramref name="gate"/> — including the <paramref name="openAsync"/>
    /// await, which can be slow (a model load) — so this never races <see cref="ClearResidentPinCore{TLease}"/>
    /// on the same gate: the two fully serialize instead of interleaving their reads/writes of the pin. That
    /// alone stops a just-opened lease from landing in the pin fields after a concurrent clear already ran,
    /// but a second check matters too: <paramref name="keepResidentNow"/> is re-read <b>after</b> the open
    /// completes, still inside the gate, and a lease opened while the setting was going off is disposed
    /// instead of stored — otherwise a request that started before <see cref="RequestKeepResident"/>(false)
    /// but finishes its (possibly long) open after it would resurrect exactly the pin that call was trying to
    /// drop, with nothing left to ever clear it again (<see cref="MaybeKeepResidentAsync"/>'s own top-of-method
    /// guard short-circuits every later call once <c>_keepResident</c> is false).</para>
    ///
    /// <para>Disposal (of a superseded previous pin, or of a lease discarded by the recheck above) happens
    /// <b>after</b> releasing <paramref name="gate"/>, never while holding it: <c>ISynthesizerLease</c>/
    /// <c>ITranscriberLease</c>'s own contract says <c>Dispose</c> waits for a call in flight, bounded by the
    /// engine's 120 s release budget, and holding this gate for up to two minutes would stall every other
    /// TTS/STT request and <see cref="ClearResidentPinCore{TLease}"/> call behind it.</para></summary>
    /// <returns>True if a new lease was opened and stored; false if one matching <paramref name="key"/> was
    /// already pinned, or the setting was/went off and nothing was stored.</returns>
    internal static async Task<bool> OpenResidentPinCoreAsync<TLease>(
        SemaphoreSlim gate,
        Func<bool> keepResidentNow,
        string key,
        Func<(TLease Lease, string Key)> getPinned,
        Action<TLease, string> setPinned,
        Func<CancellationToken, Task<TLease>> openAsync,
        Action<TLease> disposeLease,
        CancellationToken cancel)
        where TLease : class
    {
        // No `return` inside the try below, on purpose: every exit path must still reach the dispose-after-
        // release step past the finally, and a `return` from inside a try/finally runs the finally but then
        // leaves the method immediately afterward, skipping any code written after the whole construct. An
        // earlier version of this returned early from inside the try for the "already pinned" and "setting
        // off" cases, which skipped disposing `opened`/`toDispose` entirely -- caught by this method's own
        // tests, not just reasoned about.
        TLease toDispose = null;
        bool stored = false;
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            (TLease current, string currentKey) = getPinned();
            if (current is null || currentKey != key)
            {
                if (keepResidentNow())
                {
                    TLease opened = await openAsync(cancel).ConfigureAwait(false);
                    if (keepResidentNow())
                    {
                        (TLease previous, _) = getPinned();
                        setPinned(opened, key);
                        toDispose = previous;
                        stored = true;
                    }
                    else
                    {
                        // The setting turned off while the (possibly slow -- a model load) open was in
                        // flight. This lease was never a keeper; dispose it below rather than resurrecting a
                        // pin RequestKeepResident(false) already tried to drop.
                        toDispose = opened;
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
        if (toDispose is not null)
        {
            try { disposeLease(toDispose); }
            catch (Exception ex) { Logs.Debug($"[AudioLab] Disposing a residency lease threw: {ex.Message}"); }
        }
        return stored;
    }

    /// <summary>Disposes and forgets both pins. Safe to call when neither (or the engine itself) exists. Takes
    /// <see cref="_residencyLock"/> around each field swap (the two kinds are independent pins, so clearing
    /// them is two short, separate critical sections rather than one combined one) so this can never race
    /// <see cref="OpenResidentPinCoreAsync{TLease}"/> on the same gate — see that method's doc for why that
    /// race was the actual bug an earlier version of this had.</summary>
    private static void ClearResidencyPins()
    {
        ClearResidentPinCore<ISynthesizerLease>(_residencyLock, () => (_pinnedSynth, _pinnedSynthKey),
            (lease, k) => { _pinnedSynth = lease; _pinnedSynthKey = k; }, lease => lease.Dispose());
        ClearResidentPinCore<ITranscriberLease>(_residencyLock, () => (_pinnedTranscriber, _pinnedTranscriberKey),
            (lease, k) => { _pinnedTranscriber = lease; _pinnedTranscriberKey = k; }, lease => lease.Dispose());
    }

    /// <summary>Race-safe core of <see cref="ClearResidencyPins"/> for one lease kind — see
    /// <see cref="OpenResidentPinCoreAsync{TLease}"/>'s doc for why this takes <paramref name="gate"/>
    /// (synchronously; every caller of this method is itself synchronous) and disposes after releasing it
    /// rather than while holding it. <c>internal</c> for the same testability reason as that method.</summary>
    internal static void ClearResidentPinCore<TLease>(
        SemaphoreSlim gate,
        Func<(TLease Lease, string Key)> getPinned,
        Action<TLease, string> setPinned,
        Action<TLease> disposeLease)
        where TLease : class
    {
        TLease toDispose;
        gate.Wait();
        try
        {
            (toDispose, _) = getPinned();
            setPinned(null, null);
        }
        finally
        {
            gate.Release();
        }
        if (toDispose is not null)
        {
            try { disposeLease(toDispose); }
            catch (Exception ex) { Logs.Debug($"[AudioLab] Disposing a residency lease threw: {ex.Message}"); }
        }
    }

    #endregion

    /// <summary>The provider-private on-disk locations a model occupies (for delete / missing-weights checks).
    /// HF-auto-download models live in the engine's shared model cache, keyed by the repo named in the model
    /// definition's <c>SourceUrl</c>; placed checkpoints live in AudioLab's own weights directory. Empty when the
    /// provider isn't engine-backed. Paths may not exist yet — callers filter by existence.</summary>
    public static IReadOnlyList<string> GetWeightLocations(string providerId, string modelId)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return [];
        }
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        if (provider is null)
        {
            return [];
        }
        try
        {
            if (!binding.ManagesOwnWeights)
            {
                string dir = AudioWeights.WeightsDirectory(provider);
                // Every ACE-Step / YuE variant is a distinct multi-GB checkpoint, so resolve THIS model's own
                // files. Returning the shared directory for all of them marked every sibling variant installed
                // as soon as one was.
                AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor(providerId, modelId);
                if (specs.Length > 0)
                {
                    List<string> paths = [];
                    foreach (AudioWeightsRegistry.DownloadSpec spec in specs)
                    {
                        // Walk the fallback chain: a spec that downloaded from its fallback source is on disk
                        // under the FALLBACK's filename, and is just as present.
                        for (AudioWeightsRegistry.DownloadSpec s = spec; s is not null; s = s.Fallback)
                        {
                            paths.Add(Path.Combine(dir, s.FileName));
                        }
                    }
                    return paths;
                }
                // No registry entry (RVC and other user-placed checkpoints): the whole directory is the answer.
                return [dir];
            }
            if (_engineWeightFolders.TryGetValue(providerId, out (string Category, string Folder) placed))
            {
                return [Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), placed.Category, placed.Folder)];
            }
            AudioModelDefinition model = provider.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                ?? provider.Models.FirstOrDefault();
            // Prefer the explicit map: a provider's SourceUrl is a docs link, not necessarily its weights repo.
            string repo = _engineWeightRepos.TryGetValue(providerId, out string mapped) ? mapped : HuggingFaceRepo(model?.SourceUrl);
            if (repo is null)
            {
                Logs.Debug($"[AudioLab] No weights location known for engine-managed '{providerId}': presence cannot be determined. Add it to _engineWeightRepos/_engineWeightFolders.");
                return [];
            }
            if (_engineWeightFiles.TryGetValue(providerId, out (string Category, string File) inside))
            {
                return [Path.Combine(AudioModelCache.GetRepoDirectory(repo, inside.Category),
                    inside.File.Replace('/', Path.DirectorySeparatorChar))];
            }
            return [AudioModelCache.GetRepoDirectory(repo, AudioWeights.CategorySubfolder(provider.Category))];
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] GetWeightLocations('{providerId}','{modelId}') threw: {ex.Message}");
            return [];
        }
    }

    /// <summary>The <c>owner/name</c> repo id inside a huggingface.co model URL, or null when it isn't one.</summary>
    private static string HuggingFaceRepo(string sourceUrl)
    {
        if (string.IsNullOrEmpty(sourceUrl) || !sourceUrl.Contains("huggingface.co/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string tail = sourceUrl[(sourceUrl.IndexOf("huggingface.co/", StringComparison.OrdinalIgnoreCase) + "huggingface.co/".Length)..].Trim('/');
        string[] parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>True if the model's weights are on disk. Used by the reconcile pass to flag "installed but
    /// weights missing". A provider with no known locations is treated as present to avoid false negatives.
    /// <para>Registry-backed variants require EVERY file in their set (each satisfied by itself or anything in
    /// its fallback chain) — "any one file" would report a variant installed once only its small sidecar
    /// config had downloaded. Everything else keeps the any-location heuristic.</para></summary>
    public static bool WeightsPresent(string providerId, string modelId)
    {
        AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor(providerId, modelId);
        if (specs.Length > 0)
        {
            AudioProviderDefinition registryProvider = AudioProviderRegistry.GetById(providerId);
            if (registryProvider is not null)
            {
                string dir = AudioWeights.WeightsDirectory(registryProvider);
                return specs.All(spec => SpecSatisfied(spec, dir));
            }
        }
        IReadOnlyList<string> locations = GetWeightLocations(providerId, modelId);
        if (locations.Count == 0)
        {
            return true;
        }
        foreach (string path in locations)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    return true;
                }
                // A directory counts only if it actually holds a weight-ish file (an empty dir left by a
                // half-cleaned cache is "missing"). Cheap heuristic: any file over ~1 MB.
                if (Directory.Exists(path)
                    && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Any(f => AudioWeights.WeightFileSize(f) > 1_000_000))
                {
                    return true;
                }
            }
            catch (Exception ex) { Logs.Debug($"[AudioLab] WeightsPresent probe of '{path}' threw: {ex.Message}"); }
        }
        return false;
    }

    /// <summary>True when a spec's file, or any file in its fallback chain, is on disk. A spec that downloaded
    /// from its fallback source is stored under the fallback's name (YuE's xcodec repack vs the canonical .pth)
    /// and is just as usable.</summary>
    private static bool SpecSatisfied(AudioWeightsRegistry.DownloadSpec spec, string dir)
    {
        for (AudioWeightsRegistry.DownloadSpec s = spec; s is not null; s = s.Fallback)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, s.FileName)))
                {
                    return true;
                }
            }
            catch (Exception ex) { Logs.Debug($"[AudioLab] SpecSatisfied probe of '{s.FileName}' threw: {ex.Message}"); }
        }
        return false;
    }

    /// <summary>Points the Engine's models root at the same tree AudioLab installs into, so a placed checkpoint the
    /// Engine resolves on its own (and shared assets like ContentVec/cmudict) lands beside AudioLab's weights.
    /// Only set when the host hasn't already chosen one — the variable is process-wide and shared with any other
    /// HartsyInference-backed extension.</summary>
    private static void AlignModelsRoot()
    {
        try
        {
            if (!string.IsNullOrEmpty(EngineKnobs.ModelsRoot.Value))
            {
                return;
            }
            // AudioConfiguration.ModelRoot is "{Models}/audio"; the Engine appends "audio" itself.
            string audioRoot = Path.GetFullPath(AudioConfiguration.ModelRoot);
            string modelsRoot = Path.GetDirectoryName(audioRoot);
            if (!string.IsNullOrEmpty(modelsRoot))
            {
                // Set through the registry rather than the process environment: it is the engine's declared
                // paths.modelsRoot, and an exported variable would outlive this process and leak into any other.
                KnobStore.Set(EngineKnobs.ModelsRoot, modelsRoot);
                Logs.Debug($"[AudioLab] Engine models root set to '{modelsRoot}'.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Could not align the engine models root: {ex.Message}");
        }
    }

    /// <summary>Device the audio backend asked for, set by <see cref="RequestDevice"/>.</summary>
    private static string _requestedDevice;

    /// <summary>Device <see cref="_engine"/> was actually constructed with, or null while it doesn't exist yet.</summary>
    private static string _builtDevice;

    /// <summary>Asks for audio work to run on <paramref name="device"/> (an engine selector such as
    /// <c>auto</c>, <c>cuda:1</c> or <c>cpu</c>). Returns null when the request is honoured, or the device the
    /// engine was already built with when it is too late to change. The engine is a process-wide singleton
    /// built on first use, so requests before that overwrite each other (a backend restart picking up a new
    /// setting is the case that matters) and the last one standing is what the engine is built with.</summary>
    public static string RequestDevice(string device)
    {
        lock (_engineLock)
        {
            if (_builtDevice is not null && !string.Equals(_builtDevice, device, StringComparison.OrdinalIgnoreCase))
            {
                return _builtDevice;
            }
            _requestedDevice = device;
            return null;
        }
    }

    /// <summary>Turns <c>auto</c> into a concrete device by actually running something on the GPU, and says
    /// out loud what it decided.
    ///
    /// <para>Left to the engine, <c>auto</c> is resolved from whether a CUDA device can be seen — which is a
    /// different question from whether it works. Kernels built for another architecture, a card with no free
    /// memory, a driver and toolkit that disagree: each of those is visible, and each throws on the first real
    /// operation. The engine builds its backend lazily inside the first generation, so that throw surfaces as a
    /// failed request from a user rather than as a line in the startup log.</para>
    ///
    /// <para>So the probe runs here instead: one small matmul compared against the CPU, once, before the
    /// engine exists. A host that ends up on the CPU learns it at boot and learns why.</para></summary>
    private static string ResolveDeviceLoudly(string selector)
    {
        if (!string.Equals(selector, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return selector;
        }
        string resolved = BackendFactory.ResolveProbed("auto");
        if (resolved == "cuda")
        {
            Logs.Init("[AudioLab] Device 'auto' resolved to CUDA; a test matmul on the GPU matched the CPU.");
        }
        else
        {
            // Info, not Debug: a box that quietly runs audio on the CPU when it has a GPU in it is the single
            // most expensive misconfiguration here, and it is invisible from the outside.
            Logs.Info($"[AudioLab] Device 'auto' resolved to CPU — {BackendFactory.CudaProbeFailureReason ?? "no GPU was usable"}");
        }
        return resolved;
    }

    /// <summary>The configured compute device for audio work, or <c>auto</c>. Comes from the audio backend's
    /// Device setting; the AUDIOLAB_DEVICE environment variable overrides it for headless/debug runs.</summary>
    private static string DeviceSelector()
    {
        string env = Environment.GetEnvironmentVariable("AUDIOLAB_DEVICE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }
        return string.IsNullOrWhiteSpace(_requestedDevice) ? "auto" : _requestedDevice;
    }

    /// <summary>VRAM mode the engine was actually built with, or null while it doesn't exist yet.</summary>
    private static string _builtVramMode;

    /// <summary>VRAM mode the audio backend last asked for; null = follow the engine's own default.</summary>
    private static string _requestedVramMode;

    /// <summary>Asks for audio work to run under <paramref name="mode"/> (a <see cref="VramTier"/> name, or null/"Auto"
    /// to leave it to the engine). Returns null when the request is honoured, or the mode the engine was already
    /// built with when it is too late to change.</summary>
    /// <remarks>Same one-shot contract as <see cref="RequestDevice"/>, and for the same reason: audio shares a
    /// process-wide engine built on first use, so this is effectively a global setting whose last writer before that
    /// first generation wins. Changing it afterwards needs a SwarmUI restart.</remarks>
    public static string RequestVramMode(string mode)
    {
        lock (_engineLock)
        {
            string normalized = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
            if (_builtVramMode is not null && !string.Equals(_builtVramMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return _builtVramMode;
            }
            _requestedVramMode = normalized;
            return null;
        }
    }

    /// <summary>The VRAM policy for the engine, or null to inherit the environment. AUDIOLAB_VRAM_MODE overrides the backend setting for headless/debug runs, mirroring AUDIOLAB_DEVICE.</summary>
    private static VramPolicy VramPolicySelector()
    {
        string env = Environment.GetEnvironmentVariable("AUDIOLAB_VRAM_MODE");
        string mode = !string.IsNullOrWhiteSpace(env) ? env.Trim() : _requestedVramMode;
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!Enum.TryParse(mode, ignoreCase: true, out VramTier tier))
        {
            Logs.Warning($"[AudioLab] VRAM mode '{mode}' is not recognized; using Auto. "
                + "Valid: Auto, Performance, Balanced, Aggressive, Maximum.");
            return null;
        }
        return VramPolicy.For(tier);
    }
}
