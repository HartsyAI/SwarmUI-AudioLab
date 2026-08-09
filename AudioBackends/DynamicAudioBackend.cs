using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Hartsy.Extensions.AudioLab.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioBackends;

/// <summary>A single routing backend for all AudioLab engines.
/// Engines are installed on-demand via the backend UI — only installed engines
/// get their models registered into the model browser. Model prefix matching
/// routes generation requests to the correct provider's Python engine.</summary>
public class DynamicAudioBackend : AbstractT2IBackend
{
    #region Static Initialization

    /// <summary>Static constructor to register our model provider with ModelsAPI.
    /// Mirrors DynamicAPIBackend static constructor pattern.</summary>
    static DynamicAudioBackend()
    {
        ModelsAPI.ExtraModelProviders["audiolab"] = GetAudioModels;
    }

    /// <summary>Static method to provide audio models from all DynamicAudioBackend instances.</summary>
    private static Dictionary<string, JObject> GetAudioModels(string subtype)
    {
        IEnumerable<DynamicAudioBackend> audioBackends = Program.Backends.RunningBackendsOfType<DynamicAudioBackend>()
            .Where(b => b.RemoteModels != null);
        if (subtype is "Stable-Diffusion" || string.IsNullOrEmpty(subtype))
        {
            Dictionary<string, JObject> result = [];
            foreach (DynamicAudioBackend backend in audioBackends)
            {
                if (backend.RemoteModels.TryGetValue("Stable-Diffusion", out Dictionary<string, JObject> models))
                {
                    foreach (KeyValuePair<string, JObject> kvp in models)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                }
            }
            Logs.Verbose($"[AudioLab] Returned {result.Count} audio models for subtype: {subtype}");
            return result;
        }
        return [];
    }

    #endregion

    #region Settings and Fields

    /// <summary>Settings for the dynamic audio backend.</summary>
    public class DynamicAudioSettings : AutoConfiguration
    {
        [ConfigComment("Audio model storage path. Models are cached here instead of ~/.cache/huggingface/.")]
        public string AudioModelRoot = "Models/audio";

        [ConfigComment("Enable debug logging for audio processing.")]
        public bool DebugMode = false;

        [ConfigComment("When a model's weights are missing at generation time (e.g. you deleted them to free space), automatically re-download them.\nWhen disabled, generation refuses with a clear message instead of downloading.\nNote: engine-managed (HuggingFace-cache) models may still auto-download on first use regardless of this setting.")]
        public bool AutoRedownloadMissingWeights = true;
    }

    /// <summary>Maps AudioCategory enum to category-level feature flag names.</summary>
    private static readonly Dictionary<AudioCategory, string> CategoryFlags = new()
    {
        [AudioCategory.TTS] = "audiolab_tts",
        [AudioCategory.STT] = "audiolab_stt",
        [AudioCategory.AudioGeneration] = "audiolab_audiogen",
        [AudioCategory.VoiceConversion] = "audiolab_clone",
        [AudioCategory.AudioProcessing] = "audiolab_audioproc",
    };

    /// <summary>Runtime state for initialized providers, keyed by provider ID. Concurrent: reached from Init,
    /// install/uninstall, reconcile, the background redownloads and every generation — the same unsynchronized
    /// access that previously corrupted RegisteredAudioModels and aborted the process.</summary>
    private readonly ConcurrentDictionary<string, AudioProviderMetadata> _providers = new(StringComparer.Ordinal);

    /// <summary>Supported feature flags, populated in Init(). Concurrent for the same reason as
    /// <see cref="_providers"/>: SupportedFeatures is enumerated by the API thread while installs rebuild it.</summary>
    private readonly ConcurrentDictionary<string, byte> _supportedFeatureSet = new(StringComparer.Ordinal);

    /// <summary>Current settings accessor.</summary>
    public DynamicAudioSettings Settings => SettingsRaw as DynamicAudioSettings;

    /// <summary>Feature flags from all enabled providers.</summary>
    public override IEnumerable<string> SupportedFeatures => _supportedFeatureSet.Keys;

    /// <summary>Dictionary of remote models this backend provides, by type.</summary>
    public Dictionary<string, Dictionary<string, JObject>> RemoteModels { get; set; } = [];

    /// <summary>Collection of all registered models, keyed by model name.</summary>
    private Dictionary<string, T2IModel> RegisteredAudioModels { get; set; } = [];

    /// <summary>Guards <see cref="RemoteModels"/> and <see cref="RegisteredAudioModels"/>. Both are plain
    /// Dictionaries reached concurrently from engine install, restart-all, and the background redownloads;
    /// unsynchronized writes corrupted the Dictionary and aborted the process.</summary>
    private readonly object _modelsLock = new();

    /// <summary>Set of installed engine provider IDs, persisted to JSON config. Mutated by install/uninstall
    /// and read by reconcile, delete and the API thread, so every access goes through <see cref="_modelsLock"/>
    /// and every enumeration takes a snapshot — iterating it live while an install added to it threw.</summary>
    private HashSet<string> InstalledEngines { get; set; } = [];

    /// <summary>Installed provider IDs whose weights are missing on disk, per the last reconcile pass.
    /// These are surfaced in the UI as "weights missing" — never auto-uninstalled. Concurrent because it's
    /// read on the API thread and written from reconcile, generation, and the background redownload.</summary>
    private readonly ConcurrentDictionary<string, byte> _weightsMissing = new(StringComparer.Ordinal);

    /// <summary>Provider IDs with an auto-redownload currently in flight — guards the startup (reconcile) and
    /// on-demand (generation) redownload paths from racing/duplicating a fetch for the same engine.</summary>
    private readonly ConcurrentDictionary<string, byte> _redownloading = new(StringComparer.Ordinal);

    /// <summary>Path to the installed engines config file.</summary>
    private static string InstalledEnginesConfigPath => Path.Combine(Program.DataDir, "AudioLabInstalledEngines.json");

    /// <summary>VRAM headroom kept free on top of the incoming model's own estimate, covering activations,
    /// the codec/vocoder stage and pool fragmentation. Measured: a 90s ACE-Step XL run peaks roughly 2GB above
    /// the checkpoint itself.</summary>
    private const long VramSafetyMarginBytes = 2L * 1024 * 1024 * 1024;

    #endregion

    #region Initialization

    /// <summary>Initializes the backend with default settings and LOADING status.</summary>
    public DynamicAudioBackend()
    {
        SettingsRaw = new DynamicAudioSettings();
        Status = BackendStatus.LOADING;
    }

    /// <summary>Initializes the backend — loads installed engines config,
    /// registers models for installed engines, and starts Python servers.</summary>
    public override async Task Init()
    {
        Status = BackendStatus.LOADING;
        Models = new ConcurrentDictionary<string, List<string>>();
        _supportedFeatureSet.Clear();
        _providers.Clear();
        lock (_modelsLock)
        {
            RegisteredAudioModels.Clear();
            RemoteModels.Clear();
        }
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;

        if (!string.IsNullOrEmpty(Settings.AudioModelRoot))
        {
            AudioConfiguration.ModelRoot = Settings.AudioModelRoot;
        }

        LoadInstalledEnginesConfig();

        try
        {
            foreach (string providerId in InstalledEnginesSnapshot())
            {
                AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
                if (definition == null)
                {
                    Logs.Warning($"[AudioLab] Installed provider '{providerId}' not found in registry, skipping.");
                    continue;
                }

                // Legacy Docker/Python engines aren't ported to the in-process C# engine, so they can't run
                // in this build — skip them rather than register a provider that would fail on use.
                if (definition.RequiresDocker)
                {
                    Logs.Warning($"[AudioLab] {definition.Name} is a legacy Docker-based engine, not available in the in-process build — skipping.");
                    continue;
                }

                AudioProviderMetadata meta = new()
                {
                    Definition = definition,
                    IsEnabled = true
                };
                _providers[providerId] = meta;

                RegisterModelsForProvider(definition);

                if (CategoryFlags.TryGetValue(definition.Category, out string categoryFlag))
                {
                    _supportedFeatureSet.TryAdd(categoryFlag, 0);
                }
                if (definition.Category != AudioCategory.STT)
                {
                    _supportedFeatureSet.TryAdd("audiolab_output", 0);
                }
                foreach (string flag in definition.FeatureFlags)
                {
                    _supportedFeatureSet.TryAdd(flag, 0);
                }

                if (Settings.DebugMode)
                {
                    Logs.Debug($"[AudioLab] Loaded installed provider: {definition.Name} ({providerId})");
                }
            }

            if (_providers.Count > 0)
            {
                UpdateRemoteModels();
            }
            ReconcileWeights();
            HealMissingWeightsInBackground();
            Program.ModelRefreshEvent += ReRegisterModelsAfterRefresh;

            // Nothing to start — local inference is delegated to HartsyInference.Engine (via
            // AudioEngineBridge) and cloud providers to their API handlers. The backend is ready as soon
            // as installed engines' models are registered.

            Status = BackendStatus.RUNNING;
            if (_providers.Count > 0)
            {
                Logs.Info($"[AudioLab] Audio backend initialized with {_providers.Count} installed engine(s), " +
                          $"{RegisteredAudioModels.Count} model(s): {string.Join(", ", _providers.Keys)}");
            }
            else
            {
                Logs.Info("[AudioLab] Audio backend initialized. No engines installed yet — use the backend settings to install engines.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Audio backend initialization failed: {ex}");
            Status = BackendStatus.ERRORED;
        }
    }

    #endregion

    #region Model Registration

    /// <summary>Registers models for a specific provider into MainSDModels.
    /// Mirrors DynamicAPIBackend.RegisterModelsForProvider().</summary>
    private void RegisterModelsForProvider(AudioProviderDefinition provider)
    {
        Dictionary<string, T2IModel> models = AudioModelFactory.CreateAllModels(provider);
        List<string> modelNames = [];
        foreach (KeyValuePair<string, T2IModel> kvp in models)
        {
            string name = kvp.Key;
            T2IModel model = kvp.Value;
            model.Handler = Program.MainSDModels;
            modelNames.Add(name);
            if (!Program.MainSDModels.Models.ContainsKey(name))
            {
                Program.MainSDModels.Models[name] = model;
                Logs.Debug($"[AudioLab] Added model to MainSDModels: {name}");
            }
            lock (_modelsLock)
            {
                RegisteredAudioModels[name] = model;
            }
        }
        if (Models.TryGetValue("Stable-Diffusion", out List<string> existingModels))
        {
            existingModels.AddRange(modelNames);
        }
        else
        {
            Models.TryAdd("Stable-Diffusion", modelNames);
        }
    }

    /// <summary>Publishes registered models to RemoteModels for ExtraModelProviders.
    /// Mirrors DynamicAPIBackend.UpdateRemoteModels().</summary>
    private void UpdateRemoteModels()
    {
        int published;
        lock (_modelsLock)
        {
            if (!RegisteredAudioModels.Any())
            {
                Logs.Warning("[AudioLab] No registered audio models to publish");
                return;
            }
            Dictionary<string, JObject> remoteSD = RemoteModels.GetOrCreate("Stable-Diffusion", () => []);
            remoteSD.Clear();
            foreach (KeyValuePair<string, T2IModel> kvp in RegisteredAudioModels)
            {
                remoteSD[kvp.Key] = CreateModelMetadata(kvp.Value, kvp.Key);
            }
            ReRegisterModelsAfterRefresh();
            published = remoteSD.Count;
        }
        Logs.Verbose($"[AudioLab] Published {published} audio models to RemoteModels");
    }

    /// <summary>Re-registers audio models into MainSDModels.Models after a filesystem refresh wipes them.
    /// Mirrors DynamicAPIBackend.ReRegisterModelsAfterRefresh().</summary>
    private void ReRegisterModelsAfterRefresh()
    {
        if (Status is not BackendStatus.RUNNING and not BackendStatus.LOADING)
        {
            return;
        }
        int added = 0;
        // Also reached directly via Program.ModelRefreshEvent, not just from UpdateRemoteModels.
        lock (_modelsLock)
        {
            foreach (KeyValuePair<string, T2IModel> kvp in RegisteredAudioModels)
            {
                if (!Program.MainSDModels.Models.ContainsKey(kvp.Key))
                {
                    Program.MainSDModels.Models[kvp.Key] = kvp.Value;
                    added++;
                }
            }
        }
        if (added > 0)
        {
            Logs.Verbose($"[AudioLab] Re-registered {added} audio models into MainSDModels after refresh");
        }
        ReconcileWeights();
    }

    /// <summary>Creates metadata JObject for a model, for RemoteModels publishing.</summary>
    private JObject CreateModelMetadata(T2IModel model, string modelName)
    {
        return new JObject
        {
            ["name"] = modelName,
            ["title"] = model.Title ?? modelName,
            ["description"] = model.Description ?? "AudioLab model",
            ["preview_image"] = model.PreviewImage ?? "",
            ["loaded"] = true,
            ["architecture"] = model.ModelClass?.ID ?? "audiolab",
            ["class"] = model.ModelClass?.Name ?? "AudioLab",
            ["compat_class"] = model.ModelClass?.CompatClass.ID ?? "audiolab",
            ["standard_width"] = 0,
            ["standard_height"] = 0,
            ["is_supported_model_format"] = true,
            ["is_audio_model"] = true,
            ["local"] = false,
            ["api_source"] = "audiolab",
            ["license"] = model.Metadata?.License ?? ""
        };
    }

    #endregion

    #region Generation

    /// <summary>Generate with live output. Yields AudioFile objects via takeOutput.
    /// Routes to streaming or normal path based on StreamAudio param.
    /// Mirrors DynamicAPIBackend.GenerateLive().</summary>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        string modelName = user_input.Get(T2IParamTypes.Model)?.Name ?? "";
        string providerId = GetProviderIdFromModel(modelName);

        if (providerId == null || !_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            Logs.Error($"[AudioLab] No provider found for model: {modelName}");
            return;
        }

        AudioProviderDefinition provider = meta.Definition;
        AudioModelDefinition modelDef = GetModelDefinition(modelName, provider);

        // Missing-weights handling: when auto-redownload is OFF, refuse up front. When ON (default), actually
        // fetch the requested model's weights now (await) and only proceed if they land — the old "fall through
        // and hope the engine self-heals" only worked for HF-cache models, not AudioLab-managed weights.
        // ManagesOwnWeights providers (HeartMuLa etc.) download lazily from the HF cache inside their LoadAsync,
        // so on-disk WeightsPresent is always false pre-first-gen — fall through and let the loader fetch them.
        if (!provider.IsApiProvider && !AudioEngineBridge.ProviderManagesOwnWeights(provider.Id) && !AudioEngineBridge.WeightsPresent(provider.Id, modelDef?.Id))
        {
            _weightsMissing[provider.Id] = 0;
            if (!Settings.AutoRedownloadMissingWeights)
            {
                Logs.Warning($"[AudioLab] '{provider.Name}' weights missing and auto-redownload disabled — refusing generation.");
                takeOutput(new JObject
                {
                    ["error"] = $"{provider.Name} weights are missing on disk and 'Auto Redownload Missing Weights' is disabled. "
                        + "Reinstall it from the Audio backend settings, or enable auto-redownload."
                });
                return;
            }
            Logs.Info($"[AudioLab] '{provider.Name}' weights missing — auto-redownloading '{modelDef?.Id ?? "default"}' before generating…");
            bool restored = await TryAutoRedownloadWeights(provider.Id, modelDef?.Id, msg => { Logs.Info($"[AudioLab] {msg}"); return Task.CompletedTask; });
            if (!restored || !AudioEngineBridge.WeightsPresent(provider.Id, modelDef?.Id))
            {
                takeOutput(new JObject
                {
                    ["error"] = $"{provider.Name} weights are missing and could not be re-downloaded automatically. Check the server logs, then reinstall it from the Audio backend settings."
                });
                return;
            }
            _weightsMissing.TryRemove(provider.Id, out _);
        }
        else
        {
            _weightsMissing.TryRemove(provider.Id, out _);
        }

        if (provider.Category == AudioCategory.TTS
            && user_input.TryGet(AudioLabParams.StreamChunkSize, out string chunkMode) && chunkMode != "off")
        {
            string text = user_input.Get(T2IParamTypes.Prompt, "");
            List<string> chunks = SplitIntoChunks(text, chunkMode);
            if (chunks != null)
            {
                await GenerateLiveStreaming(user_input, batchId, takeOutput, meta, provider, modelDef, chunks);
                return;
            }
        }

        await GenerateLiveNormal(user_input, batchId, takeOutput, meta, provider, modelDef);
    }

    /// <summary>Normal (non-streaming) generation path — original behavior.</summary>
    private async Task GenerateLiveNormal(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef)
    {
        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["step"] = 0,
                ["total_steps"] = 1
            }
        });

        Dictionary<string, object> args = BuildEngineArgs(user_input, provider, modelDef);

        try
        {
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.SourceSession?.User, user_input.InterruptToken);

            if (result["cancelled"]?.Value<bool>() == true)
            {
                Logs.Info($"[AudioLab] Generation cancelled for {provider.Name}");
                return;
            }

            if (result["success"]?.Value<bool>() == true)
            {
                string audioBase64 = result["audio_data"]?.ToString();
                if (!string.IsNullOrEmpty(audioBase64))
                {
                    byte[] audioBytes = Convert.FromBase64String(audioBase64);
                    MediaType mediaType = (result["output_format"]?.ToString()) switch
                    {
                        "mp3" => MediaType.AudioMp3,
                        "flac" => MediaType.AudioFlac,
                        "ogg" => MediaType.AudioOgg,
                        _ => MediaType.AudioWav,
                    };
                    AudioFile audio = new(audioBytes, mediaType);
                    takeOutput(audio);
                }

                // For STT, output the transcription text and a placeholder audio
                if (provider.Category == AudioCategory.STT)
                {
                    string transcription = result["text"]?.ToString() ?? "";
                    Logs.Info($"[AudioLab] STT transcription via {provider.Name}: {transcription}");
                    takeOutput(new JObject
                    {
                        ["gen_progress"] = new JObject
                        {
                            ["current_status"] = $"Transcription: {transcription}"
                        }
                    });
                    // STT produces no audio, but the pipeline requires at least one output.
                    // Generate a minimal silent WAV so the generation isn't treated as a failure.
                    if (string.IsNullOrEmpty(audioBase64))
                    {
                        byte[] silentWav = GenerateSilentWav(sampleRate: 16000, durationMs: 100);
                        takeOutput(new AudioFile(silentWav, MediaType.AudioWav));
                    }
                }

                meta.LastUsed = DateTime.UtcNow;

                if (provider.Category == AudioCategory.TTS)
                {
                    double duration = result["duration"]?.Value<double>() ?? 0;
                    Logs.Info($"[AudioLab] TTS generated {duration:F2}s of audio via {provider.Name}");
                }
            }
            else
            {
                string error = result["error"]?.ToString() ?? "Unknown error";
                Logs.Error($"[AudioLab] Provider {provider.Name} failed: {error}");
                meta.LastError = error;
                throw new SwarmReadableErrorException($"[AudioLab] {provider.Name}: {error}");
            }
        }
        catch (OperationCanceledException) when (user_input.InterruptToken.IsCancellationRequested)
        {
            Logs.Info($"[AudioLab] Generation cancelled for {provider.Name}");
        }
        catch (Exception ex) when (ex is not SwarmReadableErrorException)
        {
            // Record then rethrow — swallowing here made every non-streaming failure look like an empty success.
            Logs.Error($"[AudioLab] Error processing with {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
            throw;
        }
    }

    /// <summary>Streaming generation path — generates each text chunk separately,
    /// sends intermediate audio chunks for immediate playback, then concatenates
    /// all PCM data into a final WAV file as the real output.</summary>
    private async Task GenerateLiveStreaming(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef, List<string> chunks)
    {
        Logs.Info($"[AudioLab] Streaming TTS: {chunks.Count} chunks via {provider.Name}");

        user_input.Set(T2IParamTypes.DoNotSaveIntermediates, true);

        List<byte[]> pcmChunks = [];
        int sampleRate = 24000;
        int channels = 1;
        int bitsPerSample = 16;
        bool formatRead = false;
        double totalDuration = 0;
        int consecutiveFailures = 0;
        string firstError = null;

        for (int i = 0; i < chunks.Count; i++)
        {
            if (user_input.InterruptToken.IsCancellationRequested)
            {
                Logs.Info($"[AudioLab] Streaming cancelled after {i}/{chunks.Count} chunks for {provider.Name}");
                break;
            }

            double overallPercent = (double)i / chunks.Count * 100;
            takeOutput(new JObject
            {
                ["gen_progress"] = new JObject
                {
                    ["batch_index"] = batchId,
                    ["overall_percent"] = overallPercent,
                    ["current_status"] = $"Generating chunk {i + 1}/{chunks.Count}..."
                }
            });

            T2IParamInput chunkInput = user_input.Clone();
            chunkInput.Set(T2IParamTypes.Prompt, chunks[i]);
            Dictionary<string, object> args = BuildEngineArgs(chunkInput, provider, modelDef);

            try
            {
                JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.SourceSession?.User, user_input.InterruptToken);

                if (result["cancelled"]?.Value<bool>() == true)
                {
                    Logs.Info($"[AudioLab] Streaming chunk cancelled for {provider.Name}");
                    break;
                }

                if (result["success"]?.Value<bool>() == true)
                {
                    string audioBase64 = result["audio_data"]?.ToString();
                    if (!string.IsNullOrEmpty(audioBase64))
                    {
                        byte[] audioBytes = Convert.FromBase64String(audioBase64);

                        if (!formatRead)
                        {
                            (sampleRate, channels, bitsPerSample) = ReadWavFormat(audioBytes);
                            formatRead = true;
                        }

                        pcmChunks.Add(StripWavHeader(audioBytes));

                        AudioFile chunkAudio = new(audioBytes, MediaType.AudioWav);
                        takeOutput(new T2IEngine.ImageOutput { File = chunkAudio, IsReal = false });

                        double chunkDuration = result["duration"]?.Value<double>() ?? 0;
                        totalDuration += chunkDuration;
                        Logs.Debug($"[AudioLab] Streamed chunk {i + 1}/{chunks.Count}: {chunkDuration:F2}s");
                    }
                    consecutiveFailures = 0;
                }
                else
                {
                    string error = result["error"]?.ToString() ?? "Unknown error";
                    firstError ??= error;
                    Logs.Warning($"[AudioLab] Chunk {i + 1} failed: {error}");
                    // Abort early on missing dependencies — all subsequent chunks will fail identically
                    if (error.Contains("No module named") || error.Contains("ModuleNotFoundError"))
                    {
                        Logs.Error($"[AudioLab] Missing Python dependency for {provider.Name}: {error}. Install provider dependencies via the AudioLab UI before generating audio.");
                        meta.LastError = $"Missing dependency: {error}. Install via AudioLab UI.";
                        break;
                    }
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining chunks.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Chunk {i + 1} error: {ex.Message}");
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining chunks.");
                    break;
                }
            }
        }

        if (pcmChunks.Count > 0)
        {
            // Streaming TTS always saves as WAV — chunks are PCM-concatenated in C#.
            // Output format param applies to single-shot generation only.
            byte[] finalWav = BuildWavFromPcm(pcmChunks, sampleRate, channels, bitsPerSample);
            AudioFile finalAudio = new(finalWav, MediaType.AudioWav);
            takeOutput(finalAudio);  // Real output — saved to disk

            meta.LastUsed = DateTime.UtcNow;
            Logs.Info($"[AudioLab] Streaming TTS complete: {totalDuration:F2}s total via {provider.Name}");
        }
        else
        {
            string errorMsg = firstError ?? meta.LastError ?? "Streaming generation produced no audio";
            Logs.Error($"[AudioLab] Streaming TTS produced no audio via {provider.Name}: {errorMsg}");
            meta.LastError ??= errorMsg;
            throw new SwarmReadableErrorException($"[AudioLab] {provider.Name}: {errorMsg}");
        }

        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["overall_percent"] = 100.0,
                ["current_status"] = "Complete"
            }
        });
    }

    /// <summary>Fallback Generate() — returns empty since GenerateLive() handles output.</summary>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        return [];
    }

    #endregion

    #region Model Loading and Shutdown

    /// <summary>Loads a model by matching its name against provider prefixes.
    /// <para>Swarm calls this only when the resident model actually changes, so it is the switch hook: before
    /// accepting the new model we make sure the card has room for it, evicting the outgoing pipelines when it
    /// does not. Without this the engine's own resident-pipeline cache accumulates across families (measured:
    /// 1.9GB → 14.1GB over five different models) and the next multi-GB checkpoint OOMs on load.</para></summary>
    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        string providerId = GetProviderIdFromModel(model.Name);
        if (providerId != null && _providers.ContainsKey(providerId))
        {
            EnsureVramHeadroom(providerId, model.Name);
            CurrentModelName = model.Name;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>Drops resident audio pipelines when the incoming model would not fit in free VRAM.
    /// <para>The engine has its own pressure check, but it fires on a fixed low-VRAM floor rather than on the
    /// incoming model's size — so a 10GB checkpoint arriving with 10GB free looks fine to it and then OOMs.
    /// Here we know the model we are about to load and its declared VRAM need, so we can decide correctly.
    /// A same-model reload never evicts, keeping repeat generations warm.</para></summary>
    private void EnsureVramHeadroom(string providerId, string modelName)
    {
        // Only a same-model reload is safe to skip. A null CurrentModelName used to skip too, which left the
        // very first load on a fresh backend with no headroom check at all — the exact case where another
        // extension's image/video model may already own most of the card.
        if (CurrentModelName == modelName)
        {
            return;
        }
        if (!_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            return;
        }
        AudioModelDefinition modelDef = GetModelDefinition(modelName, meta.Definition);
        long needBytes = ParseEstimatedBytes(modelDef?.EstimatedVram);
        if (needBytes <= 0)
        {
            // No declared estimate (API providers, CPU-only models): nothing to reason about, leave it resident.
            return;
        }
        long freeBytes = FreeVramBytes();
        if (freeBytes <= 0)
        {
            return;
        }
        long required = needBytes + VramSafetyMarginBytes;
        if (freeBytes >= required)
        {
            return;
        }
        Logs.Info($"[AudioLab] Switching to '{modelName}' needs ~{required / 1073741824.0:0.0}GB but only "
            + $"{freeBytes / 1073741824.0:0.0}GB VRAM is free — releasing resident audio models first.");
        AudioEngineBridge.Unload(providerId, modelDef?.Id);
    }

    /// <summary>Free VRAM on the busiest CUDA device, or -1 when it cannot be determined (no NVIDIA GPU,
    /// nvidia-smi absent). The engine runs on one device, so the card with the least free memory is the one
    /// carrying the audio models.
    /// <para>KNOWN LIMITATION: this is a heuristic, not a real device query. Audio can be pinned to a specific
    /// card via <c>HARTSY_AUDIO_CUDA_DEVICE</c>, but the engine exposes no way to ask which device a pipeline
    /// actually landed on, so on a multi-GPU box the min-free card may not be the audio card and the headroom
    /// check can free memory on the wrong reasoning. Fixing it properly needs engine-side device reporting.</para></summary>
    private static long FreeVramBytes()
    {
        try
        {
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is null || gpus.Length == 0)
            {
                return -1;
            }
            return gpus.Min(g => g.FreeMemory.InBytes);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Could not read free VRAM: {ex.Message}");
            return -1;
        }
    }

    /// <summary>Parses the leading size out of an <see cref="AudioModelDefinition.EstimatedVram"/> string —
    /// the field is human-facing prose ("~12GB", "~16GB (fp16)", "~12GB (lazy load)", "CPU only"), so we take
    /// the first number with a GB/MB unit and ignore the rest. Returns 0 when there is no parseable size.</summary>
    private static long ParseEstimatedBytes(string estimate)
    {
        if (string.IsNullOrWhiteSpace(estimate))
        {
            return 0;
        }
        Match m = Regex.Match(estimate, @"(\d+(?:\.\d+)?)\s*(GB|MB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            // Returning 0 disables the headroom check for this model, which is right for the genuine
            // no-GPU forms but would silently hide a typo in a new provider's value.
            if (!estimate.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                && !estimate.Contains("None", StringComparison.OrdinalIgnoreCase))
            {
                Logs.Warning($"[AudioLab] Unparseable EstimatedVram '{estimate}' — VRAM headroom checking is disabled for that model. Use a form like \"~4GB\".");
            }
            return 0;
        }
        double multiplier = m.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1073741824.0 : 1048576.0;
        return (long)(value * multiplier);
    }

    /// <summary>Hands the engine's resident audio models back on request.
    /// <para>Without this override the base <see cref="AbstractBackend.FreeMemory"/> returns false and does
    /// nothing, so Swarm's "free memory" API and its memory-pressure paths could never reclaim audio VRAM —
    /// multi-GB pipelines stayed resident until the process exited.</para></summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        AudioEngineBridge.FreeMemory();
        if (systemRam)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        CurrentModelName = null;
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Shuts down the backend, removes models from registry, and cleans up.
    /// Mirrors DynamicAPIBackend.Shutdown().</summary>
    public override async Task Shutdown()
    {
        Logs.Info("[AudioLab] Shutting down audio backend");
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;
        lock (_modelsLock)
        {
            foreach (string modelName in RegisteredAudioModels.Keys)
            {
                if (Program.MainSDModels.Models.ContainsKey(modelName))
                {
                    Logs.Verbose($"[AudioLab] Removing audio model from global registry: {modelName}");
                    Program.MainSDModels.Models.Remove(modelName, out _);
                }
            }
            RegisteredAudioModels.Clear();
            RemoteModels.Clear();
        }
        _providers.Clear();
        _supportedFeatureSet.Clear();
        // No processes to stop — but the Engine may hold multi-GB of resident audio models plus device memory,
        // so hand those back. The engine object survives and reloads on the next request.
        AudioEngineBridge.FreeMemory();
        Status = BackendStatus.DISABLED;
    }

    /// <summary>Gets all currently enabled provider metadata (for API status endpoints).</summary>
    public IReadOnlyDictionary<string, AudioProviderMetadata> GetProviders() => _providers;

    #endregion

    #region Engine Installation and Management

    /// <summary>Installs an engine: fetches its weights via the in-process C# engine,
    /// registers models, and persists the installed state. When <paramref name="modelId"/> is given, only
    /// that model's file set downloads (multi-GB variants are distinct checkpoints — never pull them all).</summary>
    public async Task<bool> InstallAndRegisterEngine(string providerId, Func<string, Task> onProgress = null, CancellationToken cancel = default, string modelId = null)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        if (definition == null)
        {
            Logs.Error($"[AudioLab] Provider '{providerId}' not found in registry.");
            return false;
        }

        await AudioWeights.Report(onProgress, $"Installing {definition.Name}...");

        try
        {
            // Engine-backed providers run in-process via the HartsyInference C# engine — no venv, no pip,
            // no Python server. This is the migration path; it takes precedence over the Python provisioning
            // below whenever the engine boundary advertises support for the provider.
            bool engineBacked = !definition.IsApiProvider && !definition.RequiresDocker
                && AudioEngineBridge.IsProviderSupported(definition.Id);

            if (engineBacked)
            {
                if (AudioEngineBridge.ProviderManagesOwnWeights(definition.Id))
                {
                    // HF-auto-download providers (Whisper, Kokoro, ...). Prefetch now — per distinct weight set —
                    // so the download AND any load error surface here with progress, not mid-generation. A failure
                    // aborts install (we don't register a provider whose weights can't be fetched). Handlers that
                    // must bind a compute device to download (music) no-op here and fetch on first generation.
                    await AudioWeights.Report(onProgress, $"Downloading weights for {definition.Name} (in-process C# engine)...");
                    HashSet<string> fetched = new(StringComparer.Ordinal);
                    // With a model_id, prefetch only that variant — each can be its own multi-GB weight set
                    // (Whisper alone has 7); a bare provider install prefetches the full registered set.
                    IEnumerable<AudioModelDefinition> modelsToFetch = string.IsNullOrEmpty(modelId)
                        ? definition.Models
                        : definition.Models.Where(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
                    foreach (AudioModelDefinition modelDef in modelsToFetch)
                    {
                        cancel.ThrowIfCancellationRequested();
                        // Dedup models that share one weight set (e.g. multiple voices of one TTS repo).
                        IReadOnlyList<string> locs = AudioEngineBridge.GetWeightLocations(definition.Id, modelDef.Id);
                        string dedupKey = locs.Count > 0 ? string.Join("|", locs) : modelDef.Id;
                        if (!fetched.Add(dedupKey))
                        {
                            continue;
                        }
                        JObject result = await AudioEngineBridge.EnsureWeightsAsync(definition.Id, modelDef.Id, onProgress, cancel);
                        if (result?["success"]?.Value<bool>() != true)
                        {
                            string err = result?["error"]?.ToString() ?? "unknown error";
                            await AudioWeights.Report(onProgress, $"Failed to fetch weights for {definition.Name}: {err}");
                            Logs.Error($"[AudioLab] Weight prefetch failed for '{definition.Id}'/'{modelDef.Id}': {err}");
                            return false;
                        }
                        // Release the prefetched pipeline so installing many models doesn't pin RAM/VRAM.
                        AudioEngineBridge.Unload(definition.Id, modelDef.Id);
                    }
                }
                else
                {
                    // Checkpoint providers (music): fetch the .safetensors now. With a model id, only that
                    // variant's files; a bare provider install pulls the DEFAULT model's set instead of every
                    // registered variant (each is a distinct multi-GB checkpoint).
                    string installModel = modelId;
                    if (string.IsNullOrEmpty(installModel))
                    {
                        installModel = definition.Models.FirstOrDefault(m => AudioWeightsRegistry.SpecsFor(definition.Id, m.Id).Length > 0)?.Id;
                    }
                    await AudioWeights.Report(onProgress, $"Downloading weights for {definition.Name}{(installModel is null ? "" : $" ({installModel})")} (in-process C# engine)...");
                    bool registered = await AudioWeights.EnsureProviderWeightsAsync(definition, onProgress, cancel, installModel);
                    if (!registered)
                    {
                        await AudioWeights.Report(onProgress, $"No auto-download is registered for {definition.Name} yet — place its .safetensors in {AudioWeights.WeightsDirectory(definition)} to enable it.");
                    }
                }
            }
            else if (definition.IsApiProvider)
            {
                // API providers use external cloud APIs — no venv, no deps, no model downloads needed.
                // The lightweight "api" group server starts lazily on first request via EnsureGroupRunningAsync.
                await AudioWeights.Report(onProgress, $"Registering {definition.Name} (cloud API — no local setup needed)...");
            }
            else
            {
                // Not an API provider and not yet supported by the C# engine: with Python removed there is
                // no way to run it. Refuse cleanly, naming the specific blocker, rather than registering a model
                // that can't generate.
                string reason = AudioServices.AudioUnsupportedReasons.Message(definition.Id, definition.Name);
                Logs.Error($"[AudioLab] {reason}");
                await AudioWeights.Report(onProgress, reason);
                return false;
            }

            await AudioWeights.Report(onProgress, $"Registering models for {definition.Name}...");
            AudioProviderMetadata meta = new()
            {
                Definition = definition,
                IsEnabled = true,
                DependenciesInstalled = true
            };
            _providers[providerId] = meta;
            RegisterModelsForProvider(definition);

            if (CategoryFlags.TryGetValue(definition.Category, out string categoryFlag))
            {
                _supportedFeatureSet.TryAdd(categoryFlag, 0);
            }
            foreach (string flag in definition.FeatureFlags)
            {
                _supportedFeatureSet.TryAdd(flag, 0);
            }

            UpdateRemoteModels();

            lock (_modelsLock)
            {
                InstalledEngines.Add(providerId);
            }
            SaveInstalledEnginesConfig();

            Program.ModelRefreshEvent?.Invoke();

            await AudioWeights.Report(onProgress, $"{definition.Name} installed successfully!");
            Logs.Info($"[AudioLab] Engine '{definition.Name}' installed and registered.");
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Logs.Info($"[AudioLab] Install of '{providerId}' cancelled.");
            await AudioWeights.Report(onProgress, "Installation cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to install engine '{providerId}': {ex}");
            await AudioWeights.Report(onProgress, $"Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Uninstalls an engine: removes models from registry and persists the change. When
    /// <paramref name="deleteWeights"/> is set, also deletes the provider's on-disk weights — but only
    /// locations not still needed by another installed provider (shared side-model caches are retained).</summary>
    public void UnregisterEngine(string providerId, bool deleteWeights = false)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        string providerName = definition?.Name ?? providerId;

        if (definition != null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                string modelName = $"Audio Models/{definition.ModelPrefix}/{modelDef.Id}";
                if (Program.MainSDModels.Models.ContainsKey(modelName))
                {
                    Program.MainSDModels.Models.Remove(modelName, out _);
                    Logs.Debug($"[AudioLab] Removed model: {modelName}");
                }
                lock (_modelsLock)
                {
                    RegisteredAudioModels.Remove(modelName);
                }
            }
        }

        if (Models.TryGetValue("Stable-Diffusion", out List<string> modelList) && definition != null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                modelList.Remove($"Audio Models/{definition.ModelPrefix}/{modelDef.Id}");
            }
        }

        _providers.TryRemove(providerId, out _);
        RebuildFeatureFlags();
        UpdateRemoteModels();

        lock (_modelsLock)
        {
            InstalledEngines.Remove(providerId);
        }
        SaveInstalledEnginesConfig();

        // Delete on-disk weights AFTER removing from InstalledEngines, so the "still needed by another
        // installed provider" guard below correctly excludes this provider.
        if (deleteWeights)
        {
            DeleteProviderWeights(providerId, definition);
        }

        Program.ModelRefreshEvent?.Invoke();
        Logs.Info($"[AudioLab] Engine '{providerName}' unregistered{(deleteWeights ? " (weights deleted)" : "")}.");
    }

    /// <summary>All provider-private weight locations (absolute, deduped) across a provider's models.</summary>
    private static HashSet<string> WeightLocationsForProvider(string providerId, AudioProviderDefinition definition)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (definition is null)
        {
            return set;
        }
        foreach (AudioModelDefinition modelDef in definition.Models)
        {
            foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, modelDef.Id))
            {
                if (!string.IsNullOrEmpty(loc))
                {
                    try { set.Add(Path.GetFullPath(loc)); } catch { /* unparseable path — skip */ }
                }
            }
        }
        return set;
    }

    /// <summary>Deletes a provider's weight files/dirs, skipping any location still referenced by another
    /// currently-installed provider. Best-effort: logs and continues on per-path failure, never throws.</summary>
    private void DeleteProviderWeights(string providerId, AudioProviderDefinition definition)
    {
        HashSet<string> mine = WeightLocationsForProvider(providerId, definition);
        if (mine.Count == 0)
        {
            Logs.Info($"[AudioLab] No deletable weight locations known for '{providerId}' — nothing removed.");
            return;
        }
        // Release any resident pipeline holding these files before deleting.
        if (definition is not null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                AudioEngineBridge.Unload(providerId, modelDef.Id);
            }
        }
        // Union of locations still needed by other installed providers (providerId already removed above).
        HashSet<string> stillNeeded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string otherId in InstalledEnginesSnapshot())
        {
            foreach (string loc in WeightLocationsForProvider(otherId, AudioProviderRegistry.GetById(otherId)))
            {
                stillNeeded.Add(loc);
            }
        }
        foreach (string path in mine)
        {
            if (stillNeeded.Contains(path))
            {
                Logs.Info($"[AudioLab] Retained shared weights (used by another installed engine): {path}");
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logs.Info($"[AudioLab] Deleted weight file: {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    Logs.Info($"[AudioLab] Deleted weight directory: {path}");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not delete weights at '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>Deletes a single model's weights while leaving the engine installed and the model registered
    /// in the browser (its row flips back to "install"; generation would auto-redownload as usual). Skips any
    /// location still referenced by a sibling model of the same provider or by another installed provider —
    /// so shared files (e.g. ACE-Step's shared config / silence latent) survive. Best-effort, never throws.</summary>
    public void DeleteModelWeights(string providerId, string modelId)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        if (definition is null)
        {
            return;
        }
        HashSet<string> mine = new(StringComparer.OrdinalIgnoreCase);
        foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, modelId))
        {
            if (!string.IsNullOrEmpty(loc))
            {
                try { mine.Add(Path.GetFullPath(loc)); } catch { /* unparseable path — skip */ }
            }
        }
        if (mine.Count == 0)
        {
            Logs.Info($"[AudioLab] No deletable weight locations known for '{providerId}/{modelId}' — nothing removed.");
            return;
        }
        // Release any resident pipeline holding this model's files before deleting.
        AudioEngineBridge.Unload(providerId, modelId);

        // Locations still needed by a sibling model of THIS provider, or by any other installed provider.
        HashSet<string> stillNeeded = new(StringComparer.OrdinalIgnoreCase);
        foreach (AudioModelDefinition sibling in definition.Models)
        {
            if (string.Equals(sibling.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, sibling.Id))
            {
                if (!string.IsNullOrEmpty(loc))
                {
                    try { stillNeeded.Add(Path.GetFullPath(loc)); } catch { /* skip */ }
                }
            }
        }
        foreach (string otherId in InstalledEnginesSnapshot())
        {
            if (string.Equals(otherId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string loc in WeightLocationsForProvider(otherId, AudioProviderRegistry.GetById(otherId)))
            {
                stillNeeded.Add(loc);
            }
        }
        foreach (string path in mine)
        {
            if (stillNeeded.Contains(path))
            {
                Logs.Info($"[AudioLab] Retained shared weights (still used by another model/engine): {path}");
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logs.Info($"[AudioLab] Deleted weight file: {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    Logs.Info($"[AudioLab] Deleted weight directory: {path}");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not delete weights at '{path}': {ex.Message}");
            }
        }
        ReconcileWeights();
        Program.ModelRefreshEvent?.Invoke();
        Logs.Info($"[AudioLab] Removed weights for '{definition.Name}' model '{modelId}'.");
    }

    /// <summary>Returns the set of currently installed engine IDs.</summary>
    public IReadOnlySet<string> GetInstalledEngineIds()
    {
        lock (_modelsLock)
        {
            return new HashSet<string>(InstalledEngines, StringComparer.Ordinal);
        }
    }

    /// <summary>A point-in-time copy, so callers can iterate without racing an install.</summary>
    private HashSet<string> InstalledEnginesSnapshot()
    {
        lock (_modelsLock)
        {
            return new HashSet<string>(InstalledEngines, StringComparer.Ordinal);
        }
    }

    /// <summary>Returns the installed provider IDs flagged "weights missing" by the last reconcile pass.</summary>
    public IReadOnlySet<string> GetWeightsMissingEngineIds() => _weightsMissing.Keys.ToHashSet();

    /// <summary>Recomputes which installed providers have no weights on disk. Records state for the UI —
    /// NEVER auto-uninstalls (a missing file is recoverable: the user may have freed space, unmounted a drive,
    /// etc.). When AutoRedownloadMissingWeights is ON, kicks off a background re-download for each flagged
    /// engine so a restart heals itself. API providers are treated as present.</summary>
    private void ReconcileWeights()
    {
        _weightsMissing.Clear();
        foreach (string providerId in InstalledEnginesSnapshot())
        {
            AudioProviderDefinition def = AudioProviderRegistry.GetById(providerId);
            if (def is null || def.IsApiProvider || def.Models.Count == 0)
            {
                continue;
            }
            // Missing only if EVERY model's weights are absent — a partial set is still usable.
            bool anyPresent = false;
            foreach (AudioModelDefinition modelDef in def.Models)
            {
                // Self-managed models fetch their own weights at first load — never "missing"
                if (modelDef.SelfManaged || AudioEngineBridge.WeightsPresent(providerId, modelDef.Id))
                {
                    anyPresent = true;
                    break;
                }
            }
            if (!anyPresent)
            {
                _weightsMissing[providerId] = 0;
                Logs.Warning($"[AudioLab] Installed engine '{def.Name}' has no weights on disk — flagged for repair (not auto-uninstalled).");
            }
        }
    }

    /// <summary>With auto-redownload ON, fetches each currently-flagged engine's default weight set in the
    /// background so a restart heals itself. Called at startup only — NOT from <see cref="ReconcileWeights"/>,
    /// which also runs right after a deliberate delete (we must not re-download what the user just removed).</summary>
    private void HealMissingWeightsInBackground()
    {
        if (!Settings.AutoRedownloadMissingWeights)
        {
            return;
        }
        foreach (string providerId in _weightsMissing.Keys.ToList())
        {
            string pid = providerId;
            _ = Task.Run(async () =>
            {
                Logs.Info($"[AudioLab] Auto-redownloading missing weights for '{pid}' in the background…");
                bool ok = await TryAutoRedownloadWeights(pid, null, msg => { Logs.Info($"[AudioLab] {msg}"); return Task.CompletedTask; });
                Logs.Info(ok
                    ? $"[AudioLab] Background auto-redownload complete for '{pid}'."
                    : $"[AudioLab] Background auto-redownload failed for '{pid}' — left flagged for manual repair.");
            });
        }
    }

    /// <summary>How long a caller will wait on someone else's in-flight download before giving up. Sized for the
    /// largest realistic audio checkpoint (YuE Stage-1 is ~12.5 GB) on a slow link.</summary>
    private static readonly TimeSpan RedownloadWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>Re-downloads a provider's missing weights via the installer, guarded by <see cref="_redownloading"/>
    /// so the startup and on-demand paths never fetch the same engine twice. Clears the weights-missing flag on
    /// success. Returns true if weights are present afterwards. <paramref name="modelId"/> null = default set.</summary>
    private async Task<bool> TryAutoRedownloadWeights(string providerId, string modelId, Func<string, Task> onProgress)
    {
        if (!_redownloading.TryAdd(providerId, 0))
        {
            // Another path is already fetching this engine — wait for it instead of starting a duplicate.
            // Bounded: a wedged download used to park this loop forever, taking the caller with it.
            DateTime waitDeadline = DateTime.UtcNow + RedownloadWaitLimit;
            while (_redownloading.ContainsKey(providerId))
            {
                if (DateTime.UtcNow > waitDeadline)
                {
                    Logs.Error($"[AudioLab] Timed out after {RedownloadWaitLimit.TotalMinutes:0} min waiting on an in-flight download of '{providerId}'. It may be stalled — check the server logs and retry.");
                    return false;
                }
                await Task.Delay(500, Program.GlobalProgramCancel);
            }
            return string.IsNullOrEmpty(modelId)
                ? !_weightsMissing.ContainsKey(providerId)
                : AudioEngineBridge.WeightsPresent(providerId, modelId);
        }
        try
        {
            bool ok = await InstallAndRegisterEngine(providerId, onProgress, Program.GlobalProgramCancel, modelId);
            AudioProviderDefinition def = AudioProviderRegistry.GetById(providerId);
            bool anyPresent = def is not null && def.Models.Any(m => AudioEngineBridge.WeightsPresent(providerId, m.Id));
            if (anyPresent)
            {
                _weightsMissing.TryRemove(providerId, out _);
            }
            return ok && anyPresent;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Auto-redownload of '{providerId}' failed: {ex.Message}");
            return false;
        }
        finally
        {
            _redownloading.TryRemove(providerId, out _);
        }
    }

    /// <summary>Rebuilds the supported feature flags from currently active providers.</summary>
    private void RebuildFeatureFlags()
    {
        _supportedFeatureSet.Clear();
        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            if (CategoryFlags.TryGetValue(meta.Definition.Category, out string categoryFlag))
            {
                _supportedFeatureSet.TryAdd(categoryFlag, 0);
            }
            if (meta.Definition.Category != AudioCategory.STT)
            {
                _supportedFeatureSet.TryAdd("audiolab_output", 0);
            }
            foreach (string flag in meta.Definition.FeatureFlags)
            {
                _supportedFeatureSet.TryAdd(flag, 0);
            }
        }
    }

    #endregion

    #region Configuration Persistence

    /// <summary>Loads the installed engines set from the JSON config file.</summary>
    private void LoadInstalledEnginesConfig()
    {
        lock (_modelsLock)
        {
            InstalledEngines.Clear();
        }
        try
        {
            if (File.Exists(InstalledEnginesConfigPath))
            {
                string json = File.ReadAllText(InstalledEnginesConfigPath);
                JObject config = JObject.Parse(json);
                JArray installed = config["installed"] as JArray;
                if (installed != null)
                {
                    foreach (JToken token in installed)
                    {
                        string id = token.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            lock (_modelsLock)
                            {
                                InstalledEngines.Add(id);
                            }
                        }
                    }
                }
                Logs.Debug($"[AudioLab] Loaded {InstalledEnginesSnapshot().Count} installed engine(s) from config.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to load installed engines config: {ex.Message}");
        }
    }

    /// <summary>Saves the installed engines set to the JSON config file.</summary>
    private void SaveInstalledEnginesConfig()
    {
        try
        {
            JObject config = new()
            {
                ["installed"] = new JArray(InstalledEnginesSnapshot().OrderBy(id => id).ToArray())
            };
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledEnginesConfigPath));
            File.WriteAllText(InstalledEnginesConfigPath, config.ToString());
            Logs.Debug($"[AudioLab] Saved {InstalledEnginesSnapshot().Count} installed engine(s) to config.");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to save installed engines config: {ex.Message}");
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Determines the provider ID from a model name by matching prefixes.</summary>
    private string GetProviderIdFromModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;

        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            string prefix = $"Audio Models/{meta.Definition.ModelPrefix}/";
            if (modelName.StartsWith(prefix))
            {
                return meta.Definition.Id;
            }
        }
        return null;
    }

    /// <summary>Gets the AudioModelDefinition for a model by extracting the model ID from the full name.</summary>
    private static AudioModelDefinition GetModelDefinition(string modelName, AudioProviderDefinition provider)
    {
        string modelId = modelName.Split('/').LastOrDefault() ?? "";
        return provider.Models.FirstOrDefault(m => m.Id == modelId);
    }

    /// <summary>Gets base64-encoded audio data from an AudioFile T2I parameter.</summary>
    private static string GetBase64Audio(T2IParamInput input, T2IRegisteredParam<AudioFile> param)
    {
        return input.TryGet(param, out AudioFile audio) && audio != null
            ? Convert.ToBase64String(audio.RawData) : "";
    }

    /// <summary>Builds engine kwargs from T2I parameters.
    /// Combines category-level args, model EngineConfig, and provider-specific params.</summary>
    private static Dictionary<string, object> BuildEngineArgs(T2IParamInput input, AudioProviderDefinition provider, AudioModelDefinition modelDef)
    {
        Dictionary<string, object> args = [];

        // Model-variant hint (consumed by the C# engine routing gate to resolve the exact checkpoint;
        // ignored by the Python path and by the engine's process methods). Prefixed to avoid colliding
        // with real engine kwargs.
        if (modelDef is not null)
        {
            args["__model_id"] = modelDef.Id;
        }

        // 1. Category-level args (shared across all providers in a category)
        switch (provider.Category)
        {
            case AudioCategory.TTS:
                args["text"] = input.Get(T2IParamTypes.Prompt, "Hello world");
                args["volume"] = input.TryGet(AudioLabParams.Volume, out double vol) ? vol : 0.8;
                // Shared sampling params (tts_sampling flag)
                if (input.TryGet(AudioLabParams.Temperature, out double sharedTemp))
                    args["temperature"] = sharedTemp;
                if (input.TryGet(AudioLabParams.TopP, out double sharedTopP))
                    args["top_p"] = sharedTopP;
                if (input.TryGet(AudioLabParams.RepetitionPenalty, out double sharedRepPen))
                    args["repetition_penalty"] = sharedRepPen;
                if (input.TryGet(AudioLabParams.TopK, out int sharedTopK))
                    args["top_k"] = sharedTopK;
                if (input.TryGet(AudioLabParams.MinP, out double sharedMinP))
                    args["min_p"] = sharedMinP;
                // Use SwarmUI's built-in CFG Scale
                if (input.TryGet(T2IParamTypes.CFGScale, out double sharedCfg))
                    args["cfg_scale"] = sharedCfg;
                // Shared voice reference (tts_voice_ref flag)
                string sharedRef = GetBase64Audio(input, AudioLabParams.ReferenceAudio);
                if (!string.IsNullOrEmpty(sharedRef))
                    args["reference_audio"] = sharedRef;
                if (input.TryGet(AudioLabParams.ReferenceText, out string sharedRefText) && !string.IsNullOrEmpty(sharedRefText))
                    args["ref_text"] = sharedRefText;
                // Seed for reproducibility (pipelines that accept one). SwarmUI resolves -1 to a concrete value upstream.
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long ttsSeed) ? ttsSeed : -1L;
                break;

            case AudioCategory.STT:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.AudioInput);
                args["language"] = input.TryGet(AudioLabParams.Language, out string sttLang) ? sttLang : "en";
                // AssemblyAI reads `language_code` and takes the same bare ISO-639-1 code AudioLabParams.Language
                // offers. Google/Azure also read `language_code` but require a BCP-47 region subtag ("en-US"),
                // which this param can't express yet — forwarding a bare "en" there would be worse than their
                // own defaults, so they keep them until the Language param gains region-qualified values.
                if (provider.Id == "assemblyai_stt" && (string)args["language"] != "auto")
                {
                    args["language_code"] = args["language"];
                }
                // "default" is a single-model sentinel, not a real API model name — leaving model_id unset
                // there keeps each handler's own correct default (e.g. Deepgram nova-3). Local providers
                // carry their real name via EngineConfig["model_name"] instead.
                if (provider.IsApiProvider && modelDef is not null && modelDef.Id != "default")
                {
                    args["model_id"] = modelDef.Id;
                }
                break;

            case AudioCategory.AudioGeneration:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                // Prefer the CORE Swarm audio param (what Swarm users + the HartsyInference ACE-Step path use);
                // fall back to AudioLab's own "Max Duration" for existing AudioLab-UI workflows, then 30s.
                args["duration"] = input.TryGet(T2IParamTypes.Text2AudioDuration, out double coreDur) ? coreDur
                    : input.TryGet(AudioLabParams.Duration, out double genDur) ? genDur : 30.0;
                // Shared AudioCraft sampling (audiocraft_sampling flag)
                if (input.TryGet(AudioLabParams.GuidanceScale, out double genGuidance))
                    args["cfg_coef"] = genGuidance;
                if (input.TryGet(AudioLabParams.AudioCraftTemperature, out double genTemp))
                    args["temperature"] = genTemp;
                if (input.TryGet(AudioLabParams.AudioCraftTopK, out int genTopK))
                    args["top_k"] = genTopK;
                if (input.TryGet(AudioLabParams.AudioCraftTopP, out double genTopP))
                    args["top_p"] = genTopP;
                break;

            case AudioCategory.VoiceConversion:
                args["source_audio"] = GetBase64Audio(input, AudioLabParams.SourceAudio);
                args["target_voice"] = GetBase64Audio(input, AudioLabParams.TargetVoice);
                break;

            case AudioCategory.AudioProcessing:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.FXInput);
                break;

            default:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                break;
        }

        // 1b. Output format args (shared across all audio-producing categories)
        if (provider.Category != AudioCategory.STT)
        {
            args["output_format"] = input.TryGet(AudioLabParams.AudioOutputFormat, out string fmt) ? fmt : "wav_16";
            args["output_quality"] = input.TryGet(AudioLabParams.AudioQuality, out string qual) ? qual : "high";
        }

        // 2. Merge model's EngineConfig (model_name, model_size, mode, etc.)
        if (modelDef?.EngineConfig != null)
        {
            foreach (KeyValuePair<string, object> kvp in modelDef.EngineConfig)
            {
                args[kvp.Key] = kvp.Value;
            }
        }

        // 3. Provider-specific params (only the active provider's params are populated)
        switch (provider.Id)
        {
            case "chatterbox_tts":
                args["exaggeration"] = input.TryGet(AudioLabParams.Exaggeration, out double exag) ? exag : 0.5;
                args["cfg_weight"] = input.TryGet(AudioLabParams.CFGWeight, out double cfgw) ? cfgw : 0.5;
                break;

            case "kokoro_tts":
                args["voice"] = input.TryGet(AudioLabParams.KokoroVoice, out string kv) ? kv : "af_heart";
                args["speed"] = input.TryGet(AudioLabParams.KokoroSpeed, out double ks) ? ks : 1.0;
                break;

            case "piper_tts":
                args["voice"] = input.TryGet(AudioLabParams.PiperVoice, out string pv) ? pv : "en_US-amy-medium";
                args["speed"] = input.TryGet(AudioLabParams.PiperSpeed, out double ps) ? ps : 1.0;
                break;

            case "orpheus_tts":
                args["voice"] = input.TryGet(AudioLabParams.OrpheusVoice, out string ov) ? ov : "tara";
                break;

            case "csm_tts":
                args["speaker_id"] = input.TryGet(AudioLabParams.Speaker, out string sp) ? sp : "0";
                break;

            case "bark_tts":
                args["voice"] = input.TryGet(AudioLabParams.BarkVoice, out string bv) ? bv : "v2/en_speaker_6";
                args["text_temp"] = input.TryGet(AudioLabParams.TextTemp, out double tt) ? tt : 0.7;
                args["waveform_temp"] = input.TryGet(AudioLabParams.WaveformTemp, out double wt) ? wt : 0.7;
                break;

            case "vibevoice_tts":
                args["diffusion_steps"] = input.TryGet(AudioLabParams.DiffusionSteps, out int diffSteps) ? diffSteps : 10;
                args["cfg_scale"] = input.TryGet(AudioLabParams.VibeVoiceCFG, out double vvCfg) ? vvCfg : 1.3;
                break;

            case "dia_tts":
                args["cfg_filter_top_k"] = input.TryGet(AudioLabParams.CFGFilterTopK, out int cfgTopK) ? cfgTopK : 35;
                break;

            case "f5_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.NFEStep, out int nfeStep) ? nfeStep : 32;
                args["speed"] = input.TryGet(AudioLabParams.F5Speed, out double f5spd) ? f5spd : 1.0;
                args["cfg_scale"] = input.TryGet(AudioLabParams.F5CFG, out double f5Cfg) ? f5Cfg : 2.0;
                break;

            case "zipvoice_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.ZipVoiceSteps, out int zvSteps) ? zvSteps : 16;
                args["speed"] = input.TryGet(AudioLabParams.ZipVoiceSpeed, out double zvSpd) ? zvSpd : 1.0;
                args["cfg_scale"] = input.TryGet(AudioLabParams.ZipVoiceCFG, out double zvCfg) ? zvCfg : 1.0;
                break;

            case "zonos_tts":
                if (input.TryGet(AudioLabParams.ZonosLanguage, out string zl))
                    args["language"] = zl;
                if (input.TryGet(AudioLabParams.ZonosEmotion, out string ze))
                    args["emotion"] = ze;
                args["speaking_rate"] = input.TryGet(AudioLabParams.SpeakingRate, out double sr) ? sr : 15.0;
                break;

            case "fishspeech_tts":
                args["max_new_tokens"] = input.TryGet(AudioLabParams.FishSpeechMaxTokens, out int fsMaxTok) ? fsMaxTok : 1024;
                args["chunk_length"] = input.TryGet(AudioLabParams.FishSpeechChunkLength, out int fsChunk) ? fsChunk : 200;
                args["normalize"] = input.TryGet(AudioLabParams.FishSpeechNormalize, out string fsNorm) ? fsNorm == "true" : true;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long fsSeed) ? fsSeed : -1L;
                break;

            case "qwen3_tts":
                args["qwen3_language"] = input.TryGet(AudioLabParams.Qwen3Language, out string q3Lang) ? q3Lang : "Auto";
                args["qwen3_speaker"] = input.TryGet(AudioLabParams.Qwen3Speaker, out string q3Spk) ? q3Spk : "Ryan";
                if (input.TryGet(AudioLabParams.Qwen3Instruct, out string q3Inst) && !string.IsNullOrEmpty(q3Inst))
                    args["qwen3_instruct"] = q3Inst;
                break;

            case "cosyvoice_tts":
                if (input.TryGet(AudioLabParams.CosyVoiceVoice, out string cvv))
                    args["voice"] = cvv;
                break;

            case "pockettts_tts":
                if (input.TryGet(AudioLabParams.PocketTTSVoice, out string pttv))
                    args["voice"] = pttv;
                break;

            case "kyutaitts_tts":
                if (input.TryGet(AudioLabParams.KyutaiTTSVoice, out string ktv))
                    args["voice"] = ktv;
                break;

            case "suno_music":
            case "udio_music":
                // Both cloud handlers read `text`, not the category block's `prompt`.
                args["text"] = input.Get(T2IParamTypes.Prompt, "");
                if (input.TryGet(AudioLabParams.MusicStyle, out string musicStyle) && !string.IsNullOrEmpty(musicStyle))
                {
                    args["style"] = musicStyle;
                }
                if (input.TryGet(AudioLabParams.Instrumental, out string musicInst))
                {
                    args["instrumental"] = musicInst;
                }
                break;

            case "acestep_music":
                // ACE-Step semantics: the main Prompt is the style/genre, the dedicated Lyrics param is the lyrics.
                // The engine's music handler maps genre→style and prompt→lyrics, so route them accordingly here
                // (overriding the category-level args["prompt"] = main prompt set above).
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.Lyrics, out string ly) ? ly : "[Instrumental]";
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long aceSeed) ? aceSeed : -1L;
                args["infer_step"] = input.TryGet(AudioLabParams.InferStep, out int infStep) ? infStep : 0;   // 0 = model default
                // turbo* variants are distilled for no-CFG sampling, so the 7.0 default actively degrades them.
                // An explicit user value always wins; only the fallback is variant-aware.
                bool aceIsTurbo = modelDef?.Id?.StartsWith("turbo", StringComparison.OrdinalIgnoreCase) == true
                    || modelDef?.Id?.StartsWith("xl-turbo", StringComparison.OrdinalIgnoreCase) == true;
                args["guidance_scale"] = input.TryGet(AudioLabParams.ACEGuidanceScale, out double aceGuide) ? aceGuide : (aceIsTurbo ? 1.0 : 7.0);
                args["instrumental"] = input.TryGet(AudioLabParams.Instrumental, out string aceInst) ? aceInst : "false";
                // Prefer the CORE Swarm audio params (what Swarm users expect + the HartsyInference ACE-Step path
                // reads); fall back to AudioLab's own params for existing AudioLab-UI workflows.
                args["bpm"] = input.TryGet(T2IParamTypes.Text2AudioBPM, out long coreBpm) ? (int)coreBpm
                    : input.TryGet(AudioLabParams.BPM, out int aceBpm) ? aceBpm : 120;
                string keyScale = input.TryGet(T2IParamTypes.Text2AudioKeyScale, out string coreKey) && !string.IsNullOrEmpty(coreKey) ? coreKey
                    : input.TryGet(AudioLabParams.KeyScale, out string aceKey) ? aceKey : null;
                if (!string.IsNullOrEmpty(keyScale)) args["key_scale"] = keyScale;
                args["time_signature"] = input.TryGet(T2IParamTypes.Text2AudioTimeSignature, out string coreTs) && !string.IsNullOrEmpty(coreTs) ? coreTs
                    : input.TryGet(AudioLabParams.TimeSignature, out string aceTs) ? aceTs : "4";
                args["vocal_language"] = input.TryGet(T2IParamTypes.Text2AudioLanguage, out string coreLang) && !string.IsNullOrEmpty(coreLang) ? coreLang
                    : input.TryGet(AudioLabParams.VocalLanguage, out string aceVl) ? aceVl : "en";
                // 0 = let the model decide; the shift1/shift3 checkpoints are trained at those exact values,
                // so name them explicitly rather than relying on the engine to infer from the checkpoint.
                double aceShiftDefault = modelDef?.Id switch
                {
                    "turbo-shift1" => 1.0,
                    "turbo-shift3" => 3.0,
                    _ => 0.0,
                };
                args["shift"] = input.TryGet(AudioLabParams.ACEShift, out double aceShift) ? aceShift : aceShiftDefault;
                args["infer_method"] = input.TryGet(AudioLabParams.InferMethod, out string aceIm) ? aceIm : "ode";
                args["use_adg"] = input.TryGet(AudioLabParams.UseADG, out string aceAdg) ? aceAdg : "false";
                args["cfg_interval_start"] = input.TryGet(AudioLabParams.CFGIntervalStart, out double aceCfgS) ? aceCfgS : 0.0;
                args["cfg_interval_end"] = input.TryGet(AudioLabParams.CFGIntervalEnd, out double aceCfgE) ? aceCfgE : 1.0;
                // LM planner params (acestep_lm_params) — TODO: integrate with SwarmUI AbstractLLMBackend
                args["lm_model"] = input.TryGet(AudioLabParams.ACELMModel, out string aceLm) ? aceLm : "none";
                args["thinking"] = input.TryGet(AudioLabParams.Thinking, out string aceThink) ? aceThink : "true";
                args["lm_temperature"] = input.TryGet(AudioLabParams.LMTemperature, out double aceLmTemp) ? aceLmTemp : 0.85;
                args["lm_cfg_scale"] = input.TryGet(AudioLabParams.LMCFGScale, out double aceLmCfg) ? aceLmCfg : 2.0;
                args["lm_top_k"] = input.TryGet(AudioLabParams.LMTopK, out int aceLmTopK) ? aceLmTopK : 0;
                args["lm_top_p"] = input.TryGet(AudioLabParams.LMTopP, out double aceLmTopP) ? aceLmTopP : 0.9;
                if (input.TryGet(AudioLabParams.LMNegativePrompt, out string aceLmNeg) && !string.IsNullOrEmpty(aceLmNeg))
                    args["lm_negative_prompt"] = aceLmNeg;
                args["use_cot_metas"] = input.TryGet(AudioLabParams.UseCotMetas, out string aceCotM) ? aceCotM : "true";
                args["use_cot_caption"] = input.TryGet(AudioLabParams.UseCotCaption, out string aceCotC) ? aceCotC : "true";
                args["use_cot_language"] = input.TryGet(AudioLabParams.UseCotLanguage, out string aceCotL) ? aceCotL : "true";
                // Task params (acestep_task_params)
                args["task_type"] = input.TryGet(AudioLabParams.ACETaskType, out string aceTask) ? aceTask : "text2music";
                string aceSrcAudio = GetBase64Audio(input, AudioLabParams.ACESourceAudio);
                if (!string.IsNullOrEmpty(aceSrcAudio))
                    args["src_audio"] = aceSrcAudio;
                string aceRefAudio = GetBase64Audio(input, AudioLabParams.ACEReferenceAudio);
                if (!string.IsNullOrEmpty(aceRefAudio))
                    args["reference_audio"] = aceRefAudio;
                args["repaint_start"] = input.TryGet(AudioLabParams.RepaintStart, out double aceRpS) ? aceRpS : 0.0;
                args["repaint_end"] = input.TryGet(AudioLabParams.RepaintEnd, out double aceRpE) ? aceRpE : -1.0;
                args["cover_strength"] = input.TryGet(AudioLabParams.CoverStrength, out double aceCovStr) ? aceCovStr : 1.0;
                args["cover_noise_strength"] = input.TryGet(AudioLabParams.CoverNoiseStrength, out double aceCovNs) ? aceCovNs : 0.0;
                break;


            case "yue_music":
                // YuE semantics (mirror ACE-Step): main Prompt = genre/style tags → genre; the dedicated
                // Lyrics param = lyrics → prompt. EncodeStage1Prompt(genre, prompt) consumes them in that order.
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.YuELyrics, out string yueLy) ? yueLy : "";
                args["max_new_tokens"] = input.TryGet(AudioLabParams.YuEMaxTokens, out int yueTokens) ? yueTokens : 3000;
                args["quantization"] = input.TryGet(AudioLabParams.YuEQuantization, out string yueQuant) ? yueQuant : "fp16";
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long yueSeed) ? yueSeed : -1L;
                args["stage2_batch_size"] = input.TryGet(AudioLabParams.YuEStage2BatchSize, out int yueS2Bs) ? yueS2Bs : 4;
                args["temperature"] = input.TryGet(AudioLabParams.YuETemperature, out double yueTemp) ? yueTemp : 0.9;
                args["top_p"] = input.TryGet(AudioLabParams.YuETopP, out double yueTopP) ? yueTopP : 0.93;
                args["repetition_penalty"] = input.TryGet(AudioLabParams.YuERepetitionPenalty, out double yueRepPen) ? yueRepPen : 1.2;
                args["run_n_segments"] = input.TryGet(AudioLabParams.YuESegments, out int yueSegs) ? yueSegs : 2;
                break;

            case "heartlib_music":
                // HeartMuLa semantics (mirror ACE-Step): main Prompt = vocal-style tags → genre; the dedicated
                // Lyrics param = lyrics → prompt. MusicHandler maps genre→HeartMulaTags, prompt→HeartMulaLyrics.
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.HeartLibLyrics, out string hlLy) ? hlLy : "";
                args["cfg_scale"] = input.TryGet(AudioLabParams.HeartLibCFGScale, out double hlCfg) ? hlCfg : 1.5;
                args["temperature"] = input.TryGet(AudioLabParams.HeartLibTemperature, out double hlTemp) ? hlTemp : 1.0;
                args["topk"] = input.TryGet(AudioLabParams.HeartLibTopK, out int hlTopK) ? hlTopK : 50;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long hlSeed) ? hlSeed : -1L;
                break;

            case "whisper_stt":
                args["task"] = input.TryGet(AudioLabParams.WhisperTask, out string whisperTask) ? whisperTask : "transcribe";
                break;

            case "rvc_clone":
                args["pitch_shift"] = input.TryGet(AudioLabParams.PitchShift, out int pitchShift) ? pitchShift : 0;
                args["f0method"] = input.TryGet(AudioLabParams.F0Method, out string f0m) ? f0m : "rmvpe";
                args["index_rate"] = input.TryGet(AudioLabParams.IndexRate, out double idxRate) ? idxRate : 0.5;
                args["rms_mix_rate"] = input.TryGet(AudioLabParams.RMSMixRate, out double rmsMix) ? rmsMix : 1.0;
                args["protect"] = input.TryGet(AudioLabParams.Protect, out double prot) ? prot : 0.33;
                break;

            case "gptsovits_clone":
                args["text"] = input.Get(T2IParamTypes.Prompt, "");
                if (input.TryGet(AudioLabParams.ClonePromptText, out string gpt) && !string.IsNullOrEmpty(gpt))
                    args["prompt_text"] = gpt;
                args["language"] = input.TryGet(AudioLabParams.CloneLanguage, out string gl) ? gl : "en";
                break;

            case "demucs_fx":
                args["overlap"] = input.TryGet(AudioLabParams.Overlap, out double overlap) ? overlap : 0.25;
                args["shifts"] = input.TryGet(AudioLabParams.Shifts, out int shifts) ? shifts : 1;
                break;

            case "resemble_enhance_fx":
                args["nfe"] = input.TryGet(AudioLabParams.EnhanceNFE, out int nfe) ? nfe : 64;
                args["solver"] = input.TryGet(AudioLabParams.EnhanceSolver, out string solver) ? solver : "midpoint";
                args["lambd"] = input.TryGet(AudioLabParams.EnhanceLambda, out double lambd) ? lambd : 0.1;
                args["tau"] = input.TryGet(AudioLabParams.EnhanceTau, out double tau) ? tau : 0.5;
                break;
        }

        return args;
    }

    #endregion

    #region Chunk Splitting and WAV Helpers

    /// <summary>Common abbreviations whose trailing period should NOT be treated as a sentence boundary.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc",
        "Inc", "Ltd", "Corp", "Ave", "Blvd", "Dept", "Est", "Fig", "Gen",
        "Gov", "No", "Sgt", "Vol"
    };

    /// <summary>Check whether a word ends with clause/sentence punctuation (ignoring abbreviations).</summary>
    private static bool EndsWithBreakPunctuation(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        char last = word[^1];
        if (last == '!' || last == '?' || last == ';' || last == ':' || last == '\u2014') return true;
        if (last == '.' || last == ',')
        {
            // Check for abbreviation — strip trailing period and see if base is a known abbr
            string baseName = word.TrimEnd('.', ',');
            if (Abbreviations.Contains(baseName)) return false;
            // Single-letter initials like "U." or "A." — not a break
            if (baseName.Length == 1 && char.IsUpper(baseName[0])) return false;
            return true;
        }
        return false;
    }

    /// <summary>Check whether a word ends with a sentence-terminal punctuation mark (. ! ?)
    /// while respecting abbreviations. Used by sentence-level splitting.</summary>
    private static bool EndsWithSentencePunctuation(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        char last = word[^1];
        if (last == '!' || last == '?') return true;
        if (last == '.')
        {
            string baseName = word.TrimEnd('.');
            if (Abbreviations.Contains(baseName)) return false;
            if (baseName.Length == 1 && char.IsUpper(baseName[0])) return false;
            return true;
        }
        return false;
    }

    /// <summary>Splits text into chunks using the given semantic mode.
    /// Returns null if fewer than 2 chunks (caller should use the normal non-streaming path).</summary>
    private static List<string> SplitIntoChunks(string text, string mode)
    {
        if (string.IsNullOrWhiteSpace(text) || mode == "off") return null;

        List<string> chunks = mode switch
        {
            "word" => SplitPerWord(text),
            "phrase" => SplitByPhrases(text),
            "sentence" => SplitBySentences(text),
            "paragraph" => SplitByParagraphs(text),
            _ => null
        };

        // Only stream if we got 2+ chunks
        return chunks is { Count: >= 2 } ? chunks : null;
    }

    /// <summary>Each word becomes its own chunk.</summary>
    private static List<string> SplitPerWord(string text)
    {
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 ? new List<string>(words) : null;
    }

    /// <summary>Splits into short phrases of ~5 words, snapping to nearby punctuation.</summary>
    private static List<string> SplitByPhrases(string text)
    {
        const int wordsPerChunk = 5;
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;

        List<string> chunks = [];
        int pos = 0;

        while (pos < words.Length)
        {
            int remaining = words.Length - pos;
            if (remaining <= wordsPerChunk + 2)
            {
                chunks.Add(string.Join(' ', words, pos, remaining));
                break;
            }

            int target = pos + wordsPerChunk;
            int bestBreak = -1;
            for (int probe = Math.Max(pos + 1, target - 2); probe <= Math.Min(words.Length - 1, target + 2); probe++)
            {
                if (EndsWithBreakPunctuation(words[probe]))
                {
                    bestBreak = probe + 1;
                    break;
                }
            }

            int end = bestBreak > 0 ? bestBreak : target;
            end = Math.Min(end, words.Length);
            chunks.Add(string.Join(' ', words, pos, end - pos));
            pos = end;
        }

        // Merge tiny trailing chunk
        if (chunks.Count >= 2)
        {
            string lastChunk = chunks[^1];
            int lastWordCount = lastChunk.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount < 2)
            {
                chunks[^2] = chunks[^2] + " " + lastChunk;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }

    /// <summary>Splits on sentence boundaries (. ! ?) while respecting abbreviations.</summary>
    private static List<string> SplitBySentences(string text)
    {
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;

        List<string> chunks = [];
        int sentenceStart = 0;

        for (int i = 0; i < words.Length; i++)
        {
            if (EndsWithSentencePunctuation(words[i]) || i == words.Length - 1)
            {
                int count = i - sentenceStart + 1;
                chunks.Add(string.Join(' ', words, sentenceStart, count));
                sentenceStart = i + 1;
            }
        }

        // Merge a very short trailing chunk (1-2 words) into the previous sentence
        if (chunks.Count >= 2)
        {
            string lastChunk = chunks[^1];
            int lastWordCount = lastChunk.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount <= 2)
            {
                chunks[^2] = chunks[^2] + " " + lastChunk;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }

    /// <summary>Splits on paragraph boundaries (double newlines).</summary>
    private static List<string> SplitByParagraphs(string text)
    {
        // Split on double newlines (handles \r\n and \n)
        string[] paragraphs = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        List<string> chunks = [];
        foreach (string p in paragraphs)
        {
            string trimmed = p.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                chunks.Add(trimmed);
            }
        }

        // If text has no paragraph breaks, fall back to sentence splitting
        if (chunks.Count < 2)
        {
            return SplitBySentences(text);
        }

        return chunks;
    }

    /// <summary>Reads WAV format info (sample rate, channels, bits per sample) from a WAV byte array.</summary>
    private static (int sampleRate, int channels, int bitsPerSample) ReadWavFormat(byte[] wav)
    {
        // Find "fmt " chunk
        for (int i = 0; i < wav.Length - 24; i++)
        {
            if (wav[i] == 'f' && wav[i + 1] == 'm' && wav[i + 2] == 't' && wav[i + 3] == ' ')
            {
                int channels = BitConverter.ToInt16(wav, i + 10);
                int sampleRate = BitConverter.ToInt32(wav, i + 12);
                int bitsPerSample = BitConverter.ToInt16(wav, i + 22);
                return (sampleRate, channels, bitsPerSample);
            }
        }
        // Defaults for typical TTS output
        return (24000, 1, 16);
    }

    /// <summary>Strips the WAV header and returns only raw PCM data bytes.</summary>
    private static byte[] StripWavHeader(byte[] wav)
    {
        // Find "data" chunk
        for (int i = 0; i < wav.Length - 8; i++)
        {
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
            {
                int dataSize = BitConverter.ToInt32(wav, i + 4);
                int dataStart = i + 8;
                int actualSize = Math.Min(dataSize, wav.Length - dataStart);
                byte[] pcm = new byte[actualSize];
                Buffer.BlockCopy(wav, dataStart, pcm, 0, actualSize);
                return pcm;
            }
        }
        // If no data chunk found, skip standard 44-byte header
        if (wav.Length > 44)
        {
            byte[] pcm = new byte[wav.Length - 44];
            Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length);
            return pcm;
        }
        return wav;
    }

    /// <summary>Builds a complete WAV file from concatenated PCM data.</summary>
    private static byte[] BuildWavFromPcm(List<byte[]> pcmChunks, int sampleRate, int channels, int bitsPerSample)
    {
        int totalPcmBytes = 0;
        foreach (byte[] chunk in pcmChunks)
        {
            totalPcmBytes += chunk.Length;
        }

        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + totalPcmBytes);  // File size - 8
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);                  // Chunk size
        bw.Write((short)1);            // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(totalPcmBytes);
        foreach (byte[] chunk in pcmChunks)
        {
            bw.Write(chunk);
        }

        return ms.ToArray();
    }

    /// <summary>Generates a minimal silent WAV file (all zeros) for use as a placeholder output.</summary>
    private static byte[] GenerateSilentWav(int sampleRate = 16000, int durationMs = 100)
    {
        int numSamples = sampleRate * durationMs / 1000;
        byte[] silence = new byte[numSamples * 2]; // 16-bit mono = 2 bytes per sample
        return BuildWavFromPcm([silence], sampleRate, channels: 1, bitsPerSample: 16);
    }

    #endregion
}
