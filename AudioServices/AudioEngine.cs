using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioServices.Stt;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using HartsyInference.Core.Backends;
using HartsyInference.Cpu;
using HartsyInference.Cuda;
using HartsyInference.Vulkan;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// In-process audio inference engine for AudioLab. Replaces the old reflection bridge to the separate
/// HartsyInference backend extension: AudioLab now references the <c>HartsyInference</c> NuGet directly
/// (see the csproj) and runs TTS/STT/voice/music pipelines itself, owning its own compute device.
///
/// <para><b>Device ownership:</b> a single process-wide <see cref="IBackend"/> (CUDA → Vulkan → CPU,
/// auto-detected) is constructed lazily on first use and shared across all audio requests. CUDA PTX and
/// Vulkan SPIR-V kernels are auto-copied next to AudioLab's own DLL by the engine's NuGet build targets,
/// so they resolve from this assembly's directory. A global lock serializes inference so concurrent audio
/// requests never collide on the shared device.</para>
///
/// <para>The static surface (<see cref="Available"/>, <see cref="EngineReady"/>,
/// <see cref="IsProviderSupported"/>, <see cref="ProviderManagesOwnWeights"/>, <see cref="ProcessAsync"/>)
/// mirrors the former bridge so call sites swap to it directly.</para>
/// </summary>
public static class AudioEngine
{
    /// <summary>Provider-id → handler dispatch table. A handler may serve several ids that share a pipeline.
    /// Grows as the engine gains the frontends each family needs (text→phoneme G2P for TTS; F0 extraction
    /// for voice-conversion). Until then those providers report <see cref="IsProviderSupported"/> = false.</summary>
    private static readonly Dictionary<string, IAudioHandler> _handlers = BuildHandlers();

    private static Dictionary<string, IAudioHandler> BuildHandlers()
    {
        Dictionary<string, IAudioHandler> map = new(StringComparer.OrdinalIgnoreCase);

        // ── STT — works end-to-end today (self-contained pipelines, no missing frontend). One generic
        //    SttHandler drives every model; each is just a descriptor (see SttModels). ──
        SttHandler whisper = new(SttModels.Whisper);
        map["whisper_stt"] = whisper;
        map["distilwhisper_stt"] = whisper; // same pipeline, different HF repo resolved per model id
        map["moonshine_stt"] = new SttHandler(SttModels.Moonshine);

        // ── TTS — same generic-handler pattern (TtsHandler + per-model descriptor). VibeVoice runs today
        //    (built-in tokenizer, raw text + voice reference). Token-based TTS (Dia, Orpheus, CSM, …) join
        //    as the engine's AudioTextFrontend ships and the Llama-3 / BERT tokenizer assets land. ──
        map["vibevoice_tts"] = new TtsHandler(TtsModels.VibeVoice);

        return map;
    }

    // ───────────────────────────── compute device (lazy, shared) ─────────────────────────────

    private static IBackend _backend;
    private static readonly object _backendLock = new();
    private static bool _initFailed;
    private static string _initError;

    /// <summary>Serializes inference across audio requests — one shared device/pipeline-cache per process.</summary>
    private static readonly SemaphoreSlim _genLock = new(1, 1);

    /// <summary>The engine is compiled into AudioLab, so it is always present (unlike the old optional
    /// reflection bridge). Readiness of the actual compute device is reported by <see cref="EngineReady"/>.</summary>
    public static bool Available => true;

    /// <summary>Whether the compute device is constructed (or constructable) — i.e. inference can run.</summary>
    public static bool EngineReady() => GetBackend() is not null;

    /// <summary>Lazily constructs the shared compute device. Returns null (and caches the failure) if no
    /// backend could be initialized.</summary>
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

    /// <summary>Auto-detects a compute backend: CUDA, then Vulkan, then CPU. Kernels resolve from AudioLab's
    /// own output directory (where the engine NuGet targets copy them).</summary>
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

    // ───────────────────────────── dispatch surface ─────────────────────────────

    /// <summary>Whether the named provider can be serviced by the in-process engine right now.</summary>
    public static bool IsProviderSupported(string providerId)
        => providerId is not null && _handlers.ContainsKey(providerId);

    /// <summary>Whether the engine downloads/manages this provider's weights itself (HuggingFace auto-download),
    /// so no local checkpoint needs resolving before routing here.</summary>
    public static bool ProviderManagesOwnWeights(string providerId)
        => providerId is not null && _handlers.TryGetValue(providerId, out IAudioHandler h) && h.ManagesOwnWeights;

    /// <summary>Runs an audio request. Returns the JObject shape AudioLab's generation path parses
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
            _genLock.Release();
        }
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
}
