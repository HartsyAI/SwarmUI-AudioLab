using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Streaming;
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
                    string device = DeviceSelector();
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
    /// parses (<c>success</c> + <c>audio_data</c>/<c>text</c>/<c>stems</c>, or <c>error</c>).</summary>
    public static async Task<JObject> ProcessAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        if (!_bindings.TryGetValue(providerId ?? "", out AudioEngineBinding binding))
        {
            return AudioIo.Error($"Provider '{providerId}' is not supported by the in-process audio engine yet.");
        }
        try
        {
            ModelSpec spec = BuildSpec(providerId, binding, args);
            switch (binding.Service)
            {
                case AudioEngineService.Speech:
                    return Audio(await Engine.Speech.SynthesizeAsync(spec, AudioEngineRequests.Speech(args), cancel).ConfigureAwait(false));
                case AudioEngineService.Transcribe:
                {
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

    /// <summary>Shapes an Engine audio result into AudioLab's success JObject. The Engine already returns encoded
    /// container bytes, so this is just base64.</summary>
    private static JObject Audio(AudioResult result)
        => AudioIo.AudioResult(Convert.ToBase64String(result.Data), result.Format, result.DurationSeconds);

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
    }

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
                        .Any(f => new FileInfo(f).Length > 1_000_000))
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
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARTSYINFERENCE_MODELS")))
            {
                return;
            }
            // AudioConfiguration.ModelRoot is "{Models}/audio"; the Engine appends "audio" itself.
            string audioRoot = Path.GetFullPath(AudioConfiguration.ModelRoot);
            string modelsRoot = Path.GetDirectoryName(audioRoot);
            if (!string.IsNullOrEmpty(modelsRoot))
            {
                Environment.SetEnvironmentVariable("HARTSYINFERENCE_MODELS", modelsRoot);
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
