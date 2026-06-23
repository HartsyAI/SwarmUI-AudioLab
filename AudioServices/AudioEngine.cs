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
        map["distilwhisper_stt"] = whisper; // same pipeline, repo resolved per model id
        map["moonshine_stt"] = new SttHandler(SttModels.Moonshine);
        map["kyutaistt_stt"] = new SttHandler(SttModels.Kyutai);

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

        // Music — MusicGen/AudioGen HF-auto-download. YuE is ready (MusicModels.Yue) but gated until the
        // engine package ships YueTokenizer; re-enable this line + the #if false in MusicModels then.
        map["musicgen_music"] = new MusicHandler("musicgen_music", MusicModels.MusicGen);
        map["audiogen_sfx"] = new MusicHandler("audiogen_sfx", MusicModels.AudioGen);
        map["acestep_music"] = new MusicHandler("acestep_music", MusicModels.AceStep);
        map["yue_music"] = new MusicHandler("yue_music", MusicModels.Yue);

        // Voice conversion — RVC re-voices a source clip (HuBERT content + YIN F0 + trained voice model).
        map["rvc_clone"] = new VcHandler("rvc_clone", VcModels.Rvc);
        // OpenVoice V2 tone-color transfer (source + target voice → re-voiced source).
        map["openvoice_clone"] = new VcHandler("openvoice_clone", OpenVoiceModel.Descriptor);

        // Audio processing — Demucs stem separation (used by the DAW's "Separate Stems" → returns a stems map).
        map["demucs_fx"] = new Fx.DemucsHandler();

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
