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
        [ConfigComment("Enable Docker for Linux-only engines (ACE-Step, RVC, GPT-SoVITS, Resemble-Enhance, CosyVoice, RealtimeSTT). Requires Docker with NVIDIA Container Toolkit.")]
        public bool UseDocker = false;

        [ConfigComment("Audio model storage path. Models are cached here instead of ~/.cache/huggingface/.")]
        public string AudioModelRoot = "Models/audio";

        [ConfigComment("Maximum time in seconds to wait for audio generation to complete.\nIncrease for slow models (e.g. VibeVoice) or long music generation.\nDefault: 300 (5 minutes).")]
        public int TimeoutSeconds = 300;

        [ConfigComment("Enable debug logging for audio processing.")]
        public bool DebugMode = false;
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

    /// <summary>Runtime state for initialized providers, keyed by provider ID.</summary>
    private readonly Dictionary<string, AudioProviderMetadata> _providers = [];

    /// <summary>Supported feature flags, populated in Init().</summary>
    private readonly HashSet<string> _supportedFeatureSet = [];

    /// <summary>Current settings accessor.</summary>
    public DynamicAudioSettings Settings => SettingsRaw as DynamicAudioSettings;

    /// <summary>Feature flags from all enabled providers.</summary>
    public override IEnumerable<string> SupportedFeatures => _supportedFeatureSet;

    /// <summary>Dictionary of remote models this backend provides, by type.</summary>
    public Dictionary<string, Dictionary<string, JObject>> RemoteModels { get; set; } = [];

    /// <summary>Collection of all registered models, keyed by model name.</summary>
    private Dictionary<string, T2IModel> RegisteredAudioModels { get; set; } = [];

    /// <summary>Set of installed engine provider IDs, persisted to JSON config.</summary>
    private HashSet<string> InstalledEngines { get; set; } = [];

    /// <summary>Path to the installed engines config file.</summary>
    private static string InstalledEnginesConfigPath => Path.Combine(Program.DataDir, "AudioLabInstalledEngines.json");

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
        RegisteredAudioModels.Clear();
        RemoteModels.Clear();
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;

        AudioConfiguration.UseDocker = Settings.UseDocker;
        AudioConfiguration.TimeoutSeconds = Settings.TimeoutSeconds;
        if (!string.IsNullOrEmpty(Settings.AudioModelRoot))
        {
            AudioConfiguration.ModelRoot = Settings.AudioModelRoot;
        }

        LoadInstalledEnginesConfig();

        try
        {
            foreach (string providerId in InstalledEngines)
            {
                AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
                if (definition == null)
                {
                    Logs.Warning($"[AudioLab] Installed provider '{providerId}' not found in registry, skipping.");
                    continue;
                }

                if (definition.RequiresDocker && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Settings.UseDocker)
                {
                    Logs.Warning($"[AudioLab] {definition.Name} requires Docker on Windows. Enable 'Use Docker' in settings.");
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
                    _supportedFeatureSet.Add(categoryFlag);
                }
                foreach (string flag in definition.FeatureFlags)
                {
                    _supportedFeatureSet.Add(flag);
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
            Program.ModelRefreshEvent += ReRegisterModelsAfterRefresh;

            HashSet<string> activeGroups = [];
            foreach (AudioProviderMetadata providerMeta in _providers.Values)
            {
                if (!providerMeta.Definition.RequiresDocker)
                {
                    activeGroups.Add(providerMeta.Definition.EngineGroup);
                }
            }
            foreach (string group in activeGroups)
            {
                try
                {
                    await AudioServerManager.Instance.EnsureGroupRunningAsync(group);
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[AudioLab] Failed to start server for group '{group}': {ex.Message}");
                }
            }

            if (AudioConfiguration.UseDocker && _providers.Values.Any(p => p.Definition.RequiresDocker))
            {
                await AudioServerManager.Instance.StartDockerAsync();
            }

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
            RegisteredAudioModels[name] = model;
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
        Logs.Verbose($"[AudioLab] Published {remoteSD.Count} audio models to RemoteModels");
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
        foreach (KeyValuePair<string, T2IModel> kvp in RegisteredAudioModels)
        {
            if (!Program.MainSDModels.Models.ContainsKey(kvp.Key))
            {
                Program.MainSDModels.Models[kvp.Key] = kvp.Value;
                added++;
            }
        }
        if (added > 0)
        {
            Logs.Verbose($"[AudioLab] Re-registered {added} audio models into MainSDModels after refresh");
        }
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
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.InterruptToken);

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
                    AudioFile audio = new(audioBytes, MediaType.AudioWav);
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
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error processing with {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
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
                JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.InterruptToken);

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

    /// <summary>Loads a model by matching its name against provider prefixes.</summary>
    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        string providerId = GetProviderIdFromModel(model.Name);
        if (providerId != null && _providers.ContainsKey(providerId))
        {
            CurrentModelName = model.Name;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>Shuts down the backend, removes models from registry, and cleans up.
    /// Mirrors DynamicAPIBackend.Shutdown().</summary>
    public override async Task Shutdown()
    {
        Logs.Info("[AudioLab] Shutting down audio backend");
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;
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
        _providers.Clear();
        _supportedFeatureSet.Clear();
        await AudioServerManager.Instance.ShutdownAsync();
        await AudioServerManager.Instance.StopDockerAsync();
        Status = BackendStatus.DISABLED;
    }

    /// <summary>Gets all currently enabled provider metadata (for API status endpoints).</summary>
    public IReadOnlyDictionary<string, AudioProviderMetadata> GetProviders() => _providers;

    #endregion

    #region Engine Installation and Management

    /// <summary>Installs an engine: creates venv, installs deps, starts server,
    /// registers models, and persists the installed state.</summary>
    public async Task<bool> InstallAndRegisterEngine(string providerId, Action<string> onProgress = null)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        if (definition == null)
        {
            Logs.Error($"[AudioLab] Provider '{providerId}' not found in registry.");
            return false;
        }

        onProgress?.Invoke($"Installing {definition.Name}...");

        try
        {
            if (!definition.RequiresDocker)
            {
                onProgress?.Invoke($"Creating Python environment for group '{definition.EngineGroup}'...");
                string venvPython = await VenvManager.Instance.EnsureVenvAsync(definition.EngineGroup);
                if (venvPython == null)
                {
                    Logs.Error($"[AudioLab] Failed to create venv for group '{definition.EngineGroup}'.");
                    onProgress?.Invoke("Error: Failed to create Python environment.");
                    return false;
                }

                onProgress?.Invoke($"Installing dependencies for {definition.Name}...");
                AudioDependencyInstaller installer = new();
                PythonEnvironmentInfo pythonInfo = await installer.DetectPythonEnvironmentForGroupAsync(definition.EngineGroup);
                if (pythonInfo?.IsValid != true)
                {
                    Logs.Error($"[AudioLab] Python environment not available for group '{definition.EngineGroup}'.");
                    onProgress?.Invoke("Error: Python environment not available.");
                    return false;
                }
                bool depsOk = await installer.InstallProviderDependenciesAsync(pythonInfo, definition);
                if (!depsOk)
                {
                    Logs.Error($"[AudioLab] Dependency installation failed for {definition.Name}.");
                    onProgress?.Invoke("Error: Dependency installation failed.");
                    return false;
                }

                onProgress?.Invoke($"Starting audio server for group '{definition.EngineGroup}'...");
                await AudioServerManager.Instance.EnsureGroupRunningAsync(definition.EngineGroup);
            }
            else
            {
                if (!AudioConfiguration.UseDocker)
                {
                    Logs.Error($"[AudioLab] {definition.Name} requires Docker but Docker is not enabled.");
                    onProgress?.Invoke("Error: Enable 'Use Docker' in backend settings first.");
                    return false;
                }
                onProgress?.Invoke("Starting Docker container...");
                await AudioServerManager.Instance.StartDockerAsync();
            }

            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                if (modelDef.EngineConfig.TryGetValue("model_name", out object modelNameObj)
                    && modelNameObj is string modelName && !string.IsNullOrEmpty(modelName))
                {
                    // Skip pre-download for self-managed models whose Python libraries handle
                    // their own downloading at runtime (e.g. Whisper, Moonshine, Demucs).
                    if (modelDef.SelfManaged)
                    {
                        Logs.Info($"[AudioLab] Skipping pre-download for '{modelName}' (library-managed model).");
                        continue;
                    }

                    string category = definition.Category.ToString().ToLower();
                    onProgress?.Invoke($"Downloading {modelDef.Name} ({modelDef.EstimatedSize})...");
                    Logs.Info($"[AudioLab] Downloading model '{modelName}' for {definition.Name}...");

                    JObject downloadResult = await AudioServerManager.Instance.DownloadModelAsync(
                        definition.EngineGroup, modelName, category);

                    if (downloadResult["success"]?.Value<bool>() != true)
                    {
                        string error = downloadResult["error"]?.ToString() ?? "Unknown download error";
                        Logs.Error($"[AudioLab] Model download failed for {definition.Name}: {error}");
                        onProgress?.Invoke($"Error: Failed to download {modelDef.Name}: {error}");
                        return false;
                    }

                    Logs.Info($"[AudioLab] Model '{modelName}' downloaded successfully.");
                }
            }

            onProgress?.Invoke($"Registering models for {definition.Name}...");
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
                _supportedFeatureSet.Add(categoryFlag);
            }
            foreach (string flag in definition.FeatureFlags)
            {
                _supportedFeatureSet.Add(flag);
            }

            UpdateRemoteModels();

            InstalledEngines.Add(providerId);
            SaveInstalledEnginesConfig();

            Program.ModelRefreshEvent?.Invoke();

            onProgress?.Invoke($"{definition.Name} installed successfully!");
            Logs.Info($"[AudioLab] Engine '{definition.Name}' installed and registered.");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to install engine '{providerId}': {ex}");
            onProgress?.Invoke($"Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Uninstalls an engine: removes models from registry and persists the change.</summary>
    public void UnregisterEngine(string providerId)
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
                RegisteredAudioModels.Remove(modelName);
            }
        }

        if (Models.TryGetValue("Stable-Diffusion", out List<string> modelList) && definition != null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                modelList.Remove($"Audio Models/{definition.ModelPrefix}/{modelDef.Id}");
            }
        }

        _providers.Remove(providerId);
        RebuildFeatureFlags();
        UpdateRemoteModels();

        InstalledEngines.Remove(providerId);
        SaveInstalledEnginesConfig();

        Program.ModelRefreshEvent?.Invoke();
        Logs.Info($"[AudioLab] Engine '{providerName}' unregistered.");
    }

    /// <summary>Returns the set of currently installed engine IDs.</summary>
    public IReadOnlySet<string> GetInstalledEngineIds() => InstalledEngines;

    /// <summary>Rebuilds the supported feature flags from currently active providers.</summary>
    private void RebuildFeatureFlags()
    {
        _supportedFeatureSet.Clear();
        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            if (CategoryFlags.TryGetValue(meta.Definition.Category, out string categoryFlag))
            {
                _supportedFeatureSet.Add(categoryFlag);
            }
            foreach (string flag in meta.Definition.FeatureFlags)
            {
                _supportedFeatureSet.Add(flag);
            }
        }
    }

    #endregion

    #region Configuration Persistence

    /// <summary>Loads the installed engines set from the JSON config file.</summary>
    private void LoadInstalledEnginesConfig()
    {
        InstalledEngines.Clear();
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
                            InstalledEngines.Add(id);
                        }
                    }
                }
                Logs.Debug($"[AudioLab] Loaded {InstalledEngines.Count} installed engine(s) from config.");
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
                ["installed"] = new JArray(InstalledEngines.OrderBy(id => id).ToArray())
            };
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledEnginesConfigPath));
            File.WriteAllText(InstalledEnginesConfigPath, config.ToString());
            Logs.Debug($"[AudioLab] Saved {InstalledEngines.Count} installed engine(s) to config.");
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
                break;

            case AudioCategory.STT:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.AudioInput);
                args["language"] = input.TryGet(AudioLabParams.Language, out string sttLang) ? sttLang : "en";
                break;

            case AudioCategory.AudioGeneration:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = input.TryGet(AudioLabParams.Duration, out double genDur) ? genDur : 30.0;
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

            case "acestep_music":
                // Core DiT params (acestep_music_params)
                args["lyrics"] = input.TryGet(AudioLabParams.Lyrics, out string ly) ? ly : "[Instrumental]";
                args["seed"] = input.TryGet(AudioLabParams.AudioSeed, out int aceSeed) ? aceSeed : -1;
                args["infer_step"] = input.TryGet(AudioLabParams.InferStep, out int infStep) ? infStep : 8;
                args["guidance_scale"] = input.TryGet(AudioLabParams.ACEGuidanceScale, out double aceGuide) ? aceGuide : 7.0;
                args["instrumental"] = input.TryGet(AudioLabParams.Instrumental, out string aceInst) ? aceInst : "false";
                args["bpm"] = input.TryGet(AudioLabParams.BPM, out int aceBpm) ? aceBpm : 120;
                if (input.TryGet(AudioLabParams.KeyScale, out string aceKey) && !string.IsNullOrEmpty(aceKey))
                    args["key_scale"] = aceKey;
                args["time_signature"] = input.TryGet(AudioLabParams.TimeSignature, out string aceTs) ? aceTs : "4";
                args["vocal_language"] = input.TryGet(AudioLabParams.VocalLanguage, out string aceVl) ? aceVl : "en";
                args["shift"] = input.TryGet(AudioLabParams.ACEShift, out double aceShift) ? aceShift : 3.0;
                args["infer_method"] = input.TryGet(AudioLabParams.InferMethod, out string aceIm) ? aceIm : "ode";
                args["use_adg"] = input.TryGet(AudioLabParams.UseADG, out string aceAdg) ? aceAdg : "false";
                args["cfg_interval_start"] = input.TryGet(AudioLabParams.CFGIntervalStart, out double aceCfgS) ? aceCfgS : 0.0;
                args["cfg_interval_end"] = input.TryGet(AudioLabParams.CFGIntervalEnd, out double aceCfgE) ? aceCfgE : 1.0;
                args["enable_normalization"] = input.TryGet(AudioLabParams.EnableNormalization, out string aceNorm) ? aceNorm : "true";
                args["normalization_db"] = input.TryGet(AudioLabParams.NormalizationDB, out double aceNormDb) ? aceNormDb : -14.0;
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

            case "musicgen_music":
                string melodyRef = GetBase64Audio(input, AudioLabParams.MelodyAudio);
                if (!string.IsNullOrEmpty(melodyRef))
                    args["reference_audio"] = melodyRef;
                break;

            case "yue_music":
                args["lyrics"] = input.TryGet(AudioLabParams.YuELyrics, out string yueLy) ? yueLy : "";
                args["max_new_tokens"] = input.TryGet(AudioLabParams.YuEMaxTokens, out int yueTokens) ? yueTokens : 3000;
                args["quantization"] = input.TryGet(AudioLabParams.YuEQuantization, out string yueQuant) ? yueQuant : "fp16";
                args["seed"] = input.TryGet(AudioLabParams.YuESeed, out int yueSeed) ? yueSeed : -1;
                args["stage2_batch_size"] = input.TryGet(AudioLabParams.YuEStage2BatchSize, out int yueS2Bs) ? yueS2Bs : 4;
                args["temperature"] = input.TryGet(AudioLabParams.YuETemperature, out double yueTemp) ? yueTemp : 0.9;
                args["top_p"] = input.TryGet(AudioLabParams.YuETopP, out double yueTopP) ? yueTopP : 0.93;
                args["repetition_penalty"] = input.TryGet(AudioLabParams.YuERepetitionPenalty, out double yueRepPen) ? yueRepPen : 1.2;
                args["run_n_segments"] = input.TryGet(AudioLabParams.YuESegments, out int yueSegs) ? yueSegs : 2;
                break;

            case "heartlib_music":
                args["lyrics"] = input.TryGet(AudioLabParams.HeartLibLyrics, out string hlLy) ? hlLy : "";
                args["cfg_scale"] = input.TryGet(AudioLabParams.HeartLibCFGScale, out double hlCfg) ? hlCfg : 1.5;
                args["temperature"] = input.TryGet(AudioLabParams.HeartLibTemperature, out double hlTemp) ? hlTemp : 1.0;
                args["topk"] = input.TryGet(AudioLabParams.HeartLibTopK, out int hlTopK) ? hlTopK : 50;
                args["seed"] = input.TryGet(AudioLabParams.HeartLibSeed, out int hlSeed) ? hlSeed : -1;
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
