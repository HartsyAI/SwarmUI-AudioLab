using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioServices.Music;
using Hartsy.Extensions.AudioLab.AudioServices.Stt;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using Hartsy.Extensions.AudioLab.AudioServices.Vc;
using HartsyInference.Core.Backends;
using HartsyInference.Cpu;
using HartsyInference.Cuda;
using HartsyInference.Vulkan;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>In-process audio inference for AudioLab: references the HartsyInference NuGet directly and runs
/// the pipelines itself, owning its own lazily-constructed compute device. Replaces the old reflection
/// bridge to the backend extension; the static surface mirrors it so call sites swap directly.</summary>
public static class AudioEngine
{
    #region Dispatch table

    /// <summary>Provider-id → handler. A handler may serve several ids that share a pipeline.</summary>
    private static readonly Dictionary<string, IAudioHandler> _handlers = BuildHandlers();

    private static Dictionary<string, IAudioHandler> BuildHandlers()
    {
        Dictionary<string, IAudioHandler> map = new(StringComparer.OrdinalIgnoreCase);

        // STT — one generic SttHandler per model, each a descriptor (see SttModels).
        SttHandler whisper = new(SttModels.Whisper);
        map["whisper_stt"] = whisper;
        // Distil shares the Whisper pipeline but needs its own resolver (its model ids omit "distil").
        map["distilwhisper_stt"] = new SttHandler(SttModels.DistilWhisper);
        map["moonshine_stt"] = new SttHandler(SttModels.Moonshine);
        map["kyutaistt_stt"] = new SttHandler(SttModels.Kyutai);
        // Whisper Streaming — same Whisper weights via the engine's LocalAgreement-2 streaming pipeline.
        map["whisperstreaming_stt"] = new SttHandler(SttModels.WhisperStreaming);

        // TTS — same pattern. Token-based TTS joins as the engine's text front-ends / tokenizer assets land.
        map["vibevoice_tts"] = new TtsHandler(TtsModels.VibeVoice);
        map["kokoro_tts"] = new TtsHandler(TtsModels.Kokoro);
        map["bark_tts"] = new TtsHandler(TtsModels.Bark);
        map["dia_tts"] = new TtsHandler(TtsModels.Dia);
        // Llama-3 family — wired as if the Llama-3 tokenizer asset is present (engine RequireLlama throws a
        // clear message until it ships). See TtsModels TODO(llama-asset).
        map["orpheus_tts"] = new TtsHandler(TtsModels.Orpheus);
        map["csm_tts"] = new TtsHandler(TtsModels.Csm);
        map["neutts_tts"] = new TtsHandler(NeuTtsModel.Descriptor);
        map["fishspeech_tts"] = new TtsHandler(FishSpeechModel.Descriptor);
        map["cosyvoice_tts"] = new TtsHandler(CosyVoiceModel.Descriptor);
        // F5-TTS — zero-shot voice clone (DiT + Vocos 24 kHz); reference clip + its transcript required.
        map["f5_tts"] = new TtsHandler(F5TtsModel.Descriptor);

        // Newly-wired engine TTS. Real synth where the engine recipe is verified (Qwen3 custom/design, Chatterbox
        // load); the rest dispatch but throw a precise gate naming the missing engine/front-end piece (see each
        // descriptor + the engine-work list). Provider ids match AudioProviders/*Provider.cs.
        map["qwen3_tts"] = new TtsHandler(Qwen3TtsModel.Descriptor);
        map["chatterbox_tts"] = new TtsHandler(ChatterboxModel.Descriptor);
        map["kyutaitts_tts"] = new TtsHandler(KyutaiTtsModel.Descriptor);
        map["piper_tts"] = new TtsHandler(PiperModel.Descriptor);
        map["melotts_tts"] = new TtsHandler(MeloTtsModel.Descriptor);
        map["sparktts_tts"] = new TtsHandler(SparkTtsModel.Descriptor);
        map["pockettts_tts"] = new TtsHandler(PocketTtsModel.Descriptor);
        map["styletts2_tts"] = new TtsHandler(StyleTts2Model.Descriptor);
        map["zonos_tts"] = new TtsHandler(ZonosModel.Descriptor);
        // GPT-SoVITS is a clone-category provider, but it's text+reference→speech, so it rides the TTS handler
        // (gated until the BERT/reference front-end lands).
        map["gptsovits_clone"] = new TtsHandler(GptSoVitsModel.Descriptor);

        // Music — MusicGen/AudioGen HF-auto-download. YuE is ready (MusicModels.Yue) but gated until the
        // engine package ships YueTokenizer; re-enable this line + the #if false in MusicModels then.
        map["musicgen_music"] = new MusicHandler("musicgen_music", MusicModels.MusicGen);
        map["audiogen_sfx"] = new MusicHandler("audiogen_sfx", MusicModels.AudioGen);
        map["acestep_music"] = new MusicHandler("acestep_music", MusicModels.AceStep);
        map["yue_music"] = new MusicHandler("yue_music", MusicModels.Yue);
        // HeartMuLa-oss-3B — CSM-shaped music LM + flow-matching HeartCodec, 48 kHz (HF auto-download).
        map["heartlib_music"] = new MusicHandler("heartlib_music", MusicModels.HeartMula);

        // Voice conversion — RVC re-voices a source clip (HuBERT content + YIN F0 + trained voice model).
        map["rvc_clone"] = new VcHandler("rvc_clone", VcModels.Rvc);
        // OpenVoice V2 tone-color transfer (source + target voice → re-voiced source).
        map["openvoice_clone"] = new VcHandler("openvoice_clone", OpenVoiceModel.Descriptor);

        // Audio processing — Demucs stem separation (used by the DAW's "Separate Stems" → returns a stems map).
        map["demucs_fx"] = new Fx.DemucsHandler();
        // Resemble-Enhance — denoise + LCFM enhancer + UnivNet vocoder (input clip → enhanced clip, 44.1 kHz).
        map["resemble_enhance_fx"] = new Fx.ResembleEnhanceHandler();

        return map;
    }

    #endregion

    #region Compute device

    private static IBackend _backend;
    private static readonly object _backendLock = new();
    private static bool _initFailed;
    private static string _initError;

    /// <summary>Serializes inference across requests — one shared device per process.</summary>
    private static readonly SemaphoreSlim _genLock = new(1, 1);

    /// <summary>Always true (the engine is compiled in); device readiness is <see cref="EngineReady"/>.</summary>
    public static bool Available => true;

    /// <summary>Whether the compute device is constructed (or constructable).</summary>
    public static bool EngineReady() => GetBackend() is not null;

    /// <summary>Lazily constructs the shared compute device; null (failure cached) if none could init.</summary>
    private static IBackend GetBackend()
    {
        if (_backend is not null)
        {
            return _backend;
        }
        lock (_backendLock)
        {
            if (_backend is not null)
            {
                return _backend;
            }
            if (_initFailed)
            {
                return null;
            }
            try
            {
                _backend = ConstructBackend();
                return _backend;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _initError = ex.Message;
                Logs.Error($"[AudioLab] Failed to construct a compute backend for audio inference: {ex}");
                return null;
            }
        }
    }

    /// <summary>Auto-detects CUDA → Vulkan → CPU. Kernels resolve from AudioLab's own output dir.</summary>
    private static IBackend ConstructBackend()
    {
        string extDir = Path.GetDirectoryName(typeof(AudioEngine).Assembly.Location) ?? AppContext.BaseDirectory;
        string ptxDir = Path.Combine(extDir, "Ptx");
        string spvDir = Path.Combine(extDir, "Spirv");
        Logs.Debug($"[AudioLab] Audio engine kernels: PTX={ptxDir} (exists={Directory.Exists(ptxDir)}), SPIR-V={spvDir} (exists={Directory.Exists(spvDir)})");

        try
        {
            CudaBackend cuda = new(0, ptxDir);
            Logs.Init($"[AudioLab] Audio engine using CUDA: {cuda.Capabilities.Name} (device={cuda.Device}).");
            return cuda;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] CUDA unavailable for audio ({ex.Message}); trying Vulkan.");
        }
        try
        {
            VulkanBackend vulkan = new(0, spvDir);
            Logs.Init($"[AudioLab] Audio engine using Vulkan: {vulkan.Capabilities.Name} (device={vulkan.Device}).");
            return vulkan;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Vulkan unavailable for audio ({ex.Message}); falling back to CPU.");
        }
        CpuBackend cpu = new();
        Logs.Init("[AudioLab] Audio engine using CPU backend (no GPU available).");
        return cpu;
    }

    #endregion

    #region Public API

    /// <summary>Whether the named provider can be serviced right now.</summary>
    public static bool IsProviderSupported(string providerId)
        => providerId is not null && _handlers.ContainsKey(providerId);

    /// <summary>Whether the engine downloads/manages this provider's weights itself (HF auto-download).</summary>
    public static bool ProviderManagesOwnWeights(string providerId)
        => providerId is not null && _handlers.TryGetValue(providerId, out IAudioHandler h) && h.ManagesOwnWeights;

    /// <summary>Runs an audio request, returning the JObject shape AudioLab's generation path parses
    /// (<c>success</c> + <c>audio_data</c>/<c>text</c>/<c>output_format</c>/<c>duration</c>, or <c>error</c>).</summary>
    public static async Task<JObject> ProcessAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        if (!_handlers.TryGetValue(providerId ?? "", out IAudioHandler handler))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        IBackend backend = GetBackend();
        if (backend is null)
        {
            return AudioIo.Error($"No compute backend could be initialized for audio inference{(_initError is null ? "" : $": {_initError}")}.");
        }
        await _genLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            EvictOthersUnderMemoryPressure(providerId);
            return await handler.ProcessAsync(backend, args, cancel).ConfigureAwait(false);
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
        finally
        {
            // Post-generation device hygiene: drop leftover GPU activations and return pool-reserved-but-free
            // blocks to the driver. Cached (promoted) weights stay resident, so warm latency is unaffected —
            // without this, finished generations left multi-GB of dead activations + pool reservations on the
            // card (observed: Dia idling at 10.3GB VRAM).
            try
            {
                backend.FreeActivations();
                backend.TrimMemoryPool();
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Post-generation GPU cleanup failed: {ex.Message}");
            }
            _genLock.Release();
        }
    }

    /// <summary>Provider that ran most recently — switches trigger the memory-pressure eviction check.</summary>
    private static string _lastProviderId;

    /// <summary>Free-host-RAM floor (KiB) below which switching providers evicts every OTHER provider's resident
    /// pipelines first. Runners otherwise accumulate across providers (each holds multi-GB F32 weight copies)
    /// until the kernel OOM-kills the process — observed at 21.8 GB RSS on a 32 GB box, and again at 11.5 GB
    /// with a desktop session (IDE/browser) sharing the machine: the box, not just this process, must stay
    /// healthy, so the floor is generous. Override via HARTSY_AUDIO_EVICT_BELOW_GB.</summary>
    private static readonly long EvictBelowAvailableKb =
        (long.TryParse(Environment.GetEnvironmentVariable("HARTSY_AUDIO_EVICT_BELOW_GB"), out long gb) && gb > 0 ? gb : 14L) * 1024 * 1024;

    /// <summary>When switching to a different provider with low free host RAM, drops every other provider's
    /// resident pipelines (runner Dispose also releases their auto-promoted GPU weights). Same-provider repeat
    /// requests never evict, so warm generation stays warm.</summary>
    private static void EvictOthersUnderMemoryPressure(string providerId)
    {
        if (string.Equals(_lastProviderId, providerId, StringComparison.Ordinal))
        {
            return;
        }
        long availableKb = ReadAvailableMemoryKb();
        // VRAM pressure counts too: a prior provider's promoted weights can hold most of the card
        // (Dia leaves ~10GB resident on a 12GB GPU) and the next model then OOMs at load.
        long freeVramBytes = _backend?.FreeMemoryBytes() ?? 0;
        bool hostLow = availableKb > 0 && availableKb < EvictBelowAvailableKb;
        bool vramLow = freeVramBytes > 0 && freeVramBytes < 3L * 1024 * 1024 * 1024;
        if (hostLow || vramLow)
        {
            Logs.Info($"[AudioLab] Memory pressure (host {availableKb / 1024 / 1024.0:0.0} GB free, VRAM {freeVramBytes / 1024.0 / 1024 / 1024:0.0} GB free) — unloading other providers' resident audio models before loading '{providerId}'.");
            foreach (KeyValuePair<string, IAudioHandler> kv in _handlers)
            {
                if (!string.Equals(kv.Key, providerId, StringComparison.Ordinal))
                {
                    try { kv.Value.UnloadAll(); }
                    catch (Exception ex) { Logs.Warning($"[AudioLab] UnloadAll failed for '{kv.Key}': {ex.Message}"); }
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // Disposing the runners drops host references, but their auto-promoted GPU weights sit in the
            // backend's device weight-cache and are only freed lazily (per-tensor finalizer queue drained on
            // the compute thread) — so without this the card stays ~full across a provider switch and the next
            // model OOMs on load (observed: whisper variants left 1.1GB free, then ACE-Step died requesting
            // 0.3MB). FreeAllDeviceMemory synchronously clears the weight/cast/activation caches and trims the
            // pool. Safe here: the incoming provider isn't loaded yet, so nothing live is cached.
            try { _backend?.FreeAllDeviceMemory(); }
            catch (Exception ex) { Logs.Warning($"[AudioLab] FreeAllDeviceMemory on eviction failed: {ex.Message}"); }
        }
        _lastProviderId = providerId;
    }

    /// <summary>MemAvailable from /proc/meminfo in KiB, or 0 when unavailable (non-Linux → no eviction).</summary>
    private static long ReadAvailableMemoryKb()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return long.TryParse(parts[1], out long kb) ? kb : 0;
                }
            }
        }
        catch (Exception) { }
        return 0;
    }

    /// <summary>Ensures the provider/model weights are present (drives AudioLab's Install button).</summary>
    public static async Task<JObject> EnsureWeightsAsync(string providerId, string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        if (!_handlers.TryGetValue(providerId ?? "", out IAudioHandler handler))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        try
        {
            // The prefetch load below materializes a full pipeline — clear other providers' residents first
            // if RAM is tight (this exact path OOM-killed the process during a NeuTTS install).
            EvictOthersUnderMemoryPressure(providerId);
            await handler.EnsureWeightsAsync(modelId, onProgress ?? (_ => { }), cancel).ConfigureAwait(false);
            return new JObject { ["success"] = true };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return AudioIo.Cancelled();
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Weight prefetch for '{providerId}'/'{modelId}' failed: {ex}");
            return AudioIo.Error(ex.Message);
        }
    }

    /// <summary>Drops a provider/model's resident pipeline to free memory.</summary>
    public static void Unload(string providerId, string modelId)
    {
        if (_handlers.TryGetValue(providerId ?? "", out IAudioHandler handler))
        {
            try { handler.Unload(modelId); }
            catch (Exception ex) { Logs.Debug($"[AudioLab] Unload('{providerId}','{modelId}') threw: {ex.Message}"); }
        }
    }

    /// <summary>The provider-private on-disk locations a model occupies (for delete / missing-weights checks).
    /// Empty when the provider isn't engine-backed. Paths may not exist yet — callers filter by existence.</summary>
    public static IReadOnlyList<string> GetWeightLocations(string providerId, string modelId)
    {
        if (!_handlers.TryGetValue(providerId ?? "", out IAudioHandler handler))
        {
            return [];
        }
        try { return handler.GetWeightLocations(modelId) ?? []; }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] GetWeightLocations('{providerId}','{modelId}') threw: {ex.Message}");
            return [];
        }
    }

    /// <summary>True if at least one of the model's weight locations exists on disk and is non-empty. Used by
    /// the reconcile pass to flag "installed but weights missing". A provider with no known locations (e.g. an
    /// API provider, or one whose handler can't introspect) is treated as present to avoid false negatives.</summary>
    public static bool WeightsPresent(string providerId, string modelId)
    {
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
                        .Any(f => new FileInfo(f).Length > 1_000_000))
                {
                    return true;
                }
            }
            catch (Exception ex) { Logs.Debug($"[AudioLab] WeightsPresent probe of '{path}' threw: {ex.Message}"); }
        }
        return false;
    }

    #endregion
}
