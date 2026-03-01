using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioBackends;

/// <summary>A single routing backend that replaces STTBackend + TTSBackend.
/// Mirrors DynamicAPIBackend from SwarmUI-API-Backends — provider toggles in settings
/// control which audio engines are available, and model prefix matching routes requests
/// to the correct provider's Python engine.</summary>
public class DynamicAudioBackend : AbstractT2IBackend
{
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

    /// <summary>Settings for the dynamic audio backend.</summary>
    public class DynamicAudioSettings : AutoConfiguration
    {
        // -- TTS providers --
        [ConfigComment("Enable Chatterbox TTS (high-quality voice synthesis with expressive controls).")]
        public bool EnableChatterbox = false;

        [ConfigComment("Enable Bark TTS (text-to-audio with speech, music, and sound effects).")]
        public bool EnableBark = false;

        [ConfigComment("Enable VibeVoice TTS (Microsoft, long-form multi-speaker up to 90 min).")]
        public bool EnableVibeVoice = false;

        [ConfigComment("Enable Orpheus TTS (LLM-based emotional speech with emotion tags).")]
        public bool EnableOrpheus = false;

        [ConfigComment("Enable Kokoro TTS (ultra-fast lightweight TTS, 82M params, CPU-capable).")]
        public bool EnableKokoro = false;

        [ConfigComment("Enable Dia TTS (ultra-realistic dialogue generation with nonverbal sounds).")]
        public bool EnableDia = false;

        [ConfigComment("Enable F5-TTS (zero-shot voice cloning from 15-second reference audio).")]
        public bool EnableF5TTS = false;

        [ConfigComment("Enable CSM TTS (Sesame conversational speech model, Llama backbone).")]
        public bool EnableCSM = false;

        [ConfigComment("Enable Zonos TTS (multilingual TTS trained on 200k+ hours, zero-shot cloning).")]
        public bool EnableZonos = false;

        [ConfigComment("Enable CosyVoice TTS (Alibaba streaming TTS with ultra-low latency). Requires Docker on Windows.")]
        public bool EnableCosyVoice = false;

        [ConfigComment("Enable NeuTTS Air (on-device TTS with instant voice cloning by Neuphonic).")]
        public bool EnableNeuTTS = false;

        [ConfigComment("Enable Piper TTS (CPU-only ONNX runtime with dozens of pre-trained voices).")]
        public bool EnablePiper = false;

        // -- STT providers --
        [ConfigComment("Enable Whisper STT (robust speech recognition across languages).")]
        public bool EnableWhisper = false;

        [ConfigComment("Enable RealtimeSTT (real-time streaming speech-to-text with wake word). Requires Docker on Windows.")]
        public bool EnableRealtimeSTT = false;

        [ConfigComment("Enable Moonshine STT (ultra-fast, 5x faster than Whisper).")]
        public bool EnableMoonshine = false;

        [ConfigComment("Enable Distil-Whisper STT (6x faster than Whisper large-v3, within 1% WER).")]
        public bool EnableDistilWhisper = false;

        // -- Music generation providers --
        [ConfigComment("Enable ACE-Step (SOTA music generation, up to 4 min in 20 seconds). Requires Docker on Windows.")]
        public bool EnableAceStep = false;

        [ConfigComment("Enable MusicGen (Meta AudioCraft text-to-music with melody conditioning).")]
        public bool EnableMusicGen = false;

        // -- Voice cloning providers --
        [ConfigComment("Enable OpenVoice V2 (zero-shot voice cloning with tone/style control).")]
        public bool EnableOpenVoice = false;

        [ConfigComment("Enable RVC (retrieval-based voice conversion, industry standard). Requires Docker on Windows.")]
        public bool EnableRVC = false;

        [ConfigComment("Enable GPT-SoVITS (few-shot voice cloning, strong CJK support). Requires Docker on Windows.")]
        public bool EnableGPTSoVITS = false;

        // -- Audio FX providers --
        [ConfigComment("Enable Demucs (Meta audio source separation — vocals, drums, bass, other).")]
        public bool EnableDemucs = false;

        [ConfigComment("Enable Resemble Enhance (speech denoising and super-resolution to 44.1kHz). Requires Docker on Windows.")]
        public bool EnableResembleEnhance = false;

        // -- Sound FX providers --
        [ConfigComment("Enable AudioGen (Meta AudioCraft text-to-sound-effects generation).")]
        public bool EnableAudioGen = false;

        // -- General --
        [ConfigComment("Enable Docker for Linux-only engines (ACE-Step, RVC, GPT-SoVITS, Resemble-Enhance, CosyVoice, RealtimeSTT). Requires Docker with NVIDIA Container Toolkit.")]
        public bool UseDocker = false;

        [ConfigComment("Audio model storage path. Models are cached here instead of ~/.cache/huggingface/.")]
        public string AudioModelRoot = "Models/audio";

        [ConfigComment("Maximum time in seconds to wait for audio generation to complete.\nIncrease for slow models (e.g. VibeVoice) or long music generation.\nDefault: 300 (5 minutes).")]
        public int TimeoutSeconds = 300;

        [ConfigComment("Enable debug logging for audio processing.")]
        public bool DebugMode = false;
    }

    /// <summary>Maps settings fields to provider IDs.</summary>
    private static readonly Dictionary<string, Func<DynamicAudioSettings, bool>> ProviderSettingsMap = new()
    {
        // TTS
        ["chatterbox_tts"] = s => s.EnableChatterbox,
        ["bark_tts"] = s => s.EnableBark,
        ["vibevoice_tts"] = s => s.EnableVibeVoice,
        ["orpheus_tts"] = s => s.EnableOrpheus,
        ["kokoro_tts"] = s => s.EnableKokoro,
        ["dia_tts"] = s => s.EnableDia,
        ["f5_tts"] = s => s.EnableF5TTS,
        ["csm_tts"] = s => s.EnableCSM,
        ["zonos_tts"] = s => s.EnableZonos,
        ["cosyvoice_tts"] = s => s.EnableCosyVoice,
        ["neutts_tts"] = s => s.EnableNeuTTS,
        ["piper_tts"] = s => s.EnablePiper,
        // STT
        ["whisper_stt"] = s => s.EnableWhisper,
        ["realtimestt_stt"] = s => s.EnableRealtimeSTT,
        ["moonshine_stt"] = s => s.EnableMoonshine,
        ["distilwhisper_stt"] = s => s.EnableDistilWhisper,
        // Music
        ["acestep_music"] = s => s.EnableAceStep,
        ["musicgen_music"] = s => s.EnableMusicGen,
        // Voice Clone
        ["openvoice_clone"] = s => s.EnableOpenVoice,
        ["rvc_clone"] = s => s.EnableRVC,
        ["gptsovits_clone"] = s => s.EnableGPTSoVITS,
        // Audio FX
        ["demucs_fx"] = s => s.EnableDemucs,
        ["resemble_enhance_fx"] = s => s.EnableResembleEnhance,
        // Sound FX
        ["audiogen_sfx"] = s => s.EnableAudioGen,
    };

    /// <summary>Maps AudioCategory enum to category-level feature flag names.</summary>
    private static readonly Dictionary<AudioCategory, string> CategoryFlags = new()
    {
        [AudioCategory.TTS] = "audiolab_tts",
        [AudioCategory.STT] = "audiolab_stt",
        [AudioCategory.MusicGen] = "audiolab_music",
        [AudioCategory.VoiceClone] = "audiolab_clone",
        [AudioCategory.AudioFX] = "audiolab_fx",
        [AudioCategory.SoundFX] = "audiolab_sfx",
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

    public DynamicAudioBackend()
    {
        SettingsRaw = new DynamicAudioSettings();
        Status = BackendStatus.LOADING;
    }

    /// <summary>Initializes the backend — enables providers based on settings toggles,
    /// registers virtual models, and starts the Python audio server.</summary>
    public override async Task Init()
    {
        Status = BackendStatus.LOADING;
        Models = new ConcurrentDictionary<string, List<string>>();
        _supportedFeatureSet.Clear();
        _providers.Clear();
        RegisteredAudioModels.Clear();
        RemoteModels.Clear();
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;

        // Apply settings to configuration
        AudioConfiguration.UseDocker = Settings.UseDocker;
        AudioConfiguration.TimeoutSeconds = Settings.TimeoutSeconds;
        if (!string.IsNullOrEmpty(Settings.AudioModelRoot))
        {
            AudioConfiguration.ModelRoot = Settings.AudioModelRoot;
        }

        List<string> enabledIds = GetEnabledProviderIds();
        if (enabledIds.Count == 0)
        {
            Logs.Warning("[AudioLab] No audio providers enabled. Enable at least one provider and save settings.");
            Status = BackendStatus.DISABLED;
            AddLoadStatus("Enable at least one audio provider checkbox and click Save.");
            return;
        }

        try
        {
            // Start the persistent audio server (uses SwarmUI's DoSelfStart for lifecycle)
            await AudioServerManager.Instance.StartAsync(this);
            if (Status == BackendStatus.ERRORED)
            {
                AddLoadStatus("Audio server failed to start. Check Python environment.");
                return;
            }

            foreach (string providerId in enabledIds)
            {
                AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
                if (definition == null)
                {
                    Logs.Warning($"[AudioLab] Provider '{providerId}' not found in registry, skipping.");
                    continue;
                }

                // Platform check: warn about Docker-only engines on Windows
                if (definition.RequiresDocker && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Settings.UseDocker)
                {
                    Logs.Warning($"[AudioLab] {definition.Name} requires Docker on Windows (Linux-only engine). Enable 'Use Docker' in settings.");
                    AddLoadStatus($"{definition.Name} requires Docker on Windows. Enable 'Use Docker' to use this engine.");
                    continue;
                }

                AudioProviderMetadata meta = new()
                {
                    Definition = definition,
                    IsEnabled = true
                };
                _providers[providerId] = meta;

                // Register models for this provider (mirrors DynamicAPIBackend pattern)
                RegisterModelsForProvider(definition);

                // Add feature flags: category-level + provider-specific
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
                    Logs.Debug($"[AudioLab] Enabled provider: {definition.Name} ({providerId})");
                }
            }

            UpdateRemoteModels();
            Program.ModelRefreshEvent += ReRegisterModelsAfterRefresh;

            // Start Docker container if any enabled providers require it
            if (AudioConfiguration.UseDocker && _providers.Values.Any(p => p.Definition.RequiresDocker))
            {
                await AudioServerManager.Instance.StartDockerAsync();
            }

            Status = BackendStatus.RUNNING;
            Logs.Info($"[AudioLab] Audio backend initialized with {_providers.Count} provider(s), " +
                      $"{RegisteredAudioModels.Count} model(s): {string.Join(", ", _providers.Keys)}");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Audio backend initialization failed: {ex}");
            Status = BackendStatus.ERRORED;
        }
    }

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
            ["api_source"] = "audiolab"
        };
    }

    /// <summary>Generate with live output. Yields AudioFile objects via takeOutput.
    /// Mirrors DynamicAPIBackend.GenerateLive().</summary>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        // Send progress indicator
        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["step"] = 0,
                ["total_steps"] = 1
            }
        });

        string modelName = user_input.Get(T2IParamTypes.Model)?.Name ?? "";
        string providerId = GetProviderIdFromModel(modelName);

        if (providerId == null || !_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            Logs.Error($"[AudioLab] No provider found for model: {modelName}");
            return;
        }

        AudioProviderDefinition provider = meta.Definition;
        AudioModelDefinition modelDef = GetModelDefinition(modelName, provider);
        Dictionary<string, object> args = BuildEngineArgs(user_input, provider, modelDef);

        try
        {
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args);

            if (result["success"]?.Value<bool>() == true)
            {
                string audioBase64 = result["audio_data"]?.ToString();
                if (!string.IsNullOrEmpty(audioBase64))
                {
                    byte[] audioBytes = Convert.FromBase64String(audioBase64);
                    AudioFile audio = new(audioBytes, MediaType.AudioWav);
                    takeOutput(audio);
                }

                // For STT, also output the transcription text as progress
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
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error processing with {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
        }
    }

    /// <summary>Fallback Generate() — returns empty since GenerateLive() handles output.</summary>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        return [];
    }

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

    // -- private helpers --------------------------------------------------

    /// <summary>Gets the list of enabled provider IDs based on current settings.</summary>
    private List<string> GetEnabledProviderIds()
    {
        List<string> enabled = [];
        foreach (KeyValuePair<string, Func<DynamicAudioSettings, bool>> kvp in ProviderSettingsMap)
        {
            if (kvp.Value(Settings))
            {
                enabled.Add(kvp.Key);
            }
        }
        return enabled;
    }

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
                break;

            case AudioCategory.STT:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.AudioInput);
                args["language"] = input.TryGet(AudioLabParams.Language, out string sttLang) ? sttLang : "en";
                break;

            case AudioCategory.MusicGen:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = input.TryGet(AudioLabParams.Duration, out double musicDur) ? musicDur : 30.0;
                break;

            case AudioCategory.VoiceClone:
                args["source_audio"] = GetBase64Audio(input, AudioLabParams.SourceAudio);
                args["target_voice"] = GetBase64Audio(input, AudioLabParams.TargetVoice);
                break;

            case AudioCategory.AudioFX:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.FXInput);
                break;

            case AudioCategory.SoundFX:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = input.TryGet(AudioLabParams.SFXDuration, out double sfxDur) ? sfxDur : 10.0;
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
                args["temperature"] = input.TryGet(AudioLabParams.Temperature, out double temp) ? temp : 0.8;
                args["repetition_penalty"] = input.TryGet(AudioLabParams.RepetitionPenalty, out double repPen) ? repPen : 1.2;
                args["top_p"] = input.TryGet(AudioLabParams.TopP, out double topP) ? topP : 1.0;
                args["min_p"] = input.TryGet(AudioLabParams.MinP, out double minP) ? minP : 0.05;
                string chatterboxRef = GetBase64Audio(input, AudioLabParams.ReferenceAudio);
                if (!string.IsNullOrEmpty(chatterboxRef))
                    args["reference_audio"] = chatterboxRef;
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

            case "vibevoice_tts":
                args["cfg_scale"] = input.TryGet(AudioLabParams.CFGScale, out double vcs) ? vcs : 1.3;
                break;

            case "f5_tts":
                args["reference_audio"] = GetBase64Audio(input, AudioLabParams.F5ReferenceAudio);
                if (input.TryGet(AudioLabParams.F5ReferenceText, out string frt) && !string.IsNullOrEmpty(frt))
                    args["ref_text"] = frt;
                break;

            case "zonos_tts":
                string zonosRef = GetBase64Audio(input, AudioLabParams.ZonosReferenceAudio);
                if (!string.IsNullOrEmpty(zonosRef))
                    args["reference_audio"] = zonosRef;
                if (input.TryGet(AudioLabParams.ZonosLanguage, out string zl))
                    args["language"] = zl;
                break;

            case "cosyvoice_tts":
                if (input.TryGet(AudioLabParams.CosyVoiceVoice, out string cvv))
                    args["voice"] = cvv;
                string cosyRef = GetBase64Audio(input, AudioLabParams.CosyVoiceReferenceAudio);
                if (!string.IsNullOrEmpty(cosyRef))
                    args["reference_audio"] = cosyRef;
                if (input.TryGet(AudioLabParams.CosyVoiceReferenceText, out string cvrt) && !string.IsNullOrEmpty(cvrt))
                    args["reference_text"] = cvrt;
                break;

            case "neutts_tts":
                args["reference_audio"] = GetBase64Audio(input, AudioLabParams.NeuTTSReferenceAudio);
                args["reference_text"] = input.TryGet(AudioLabParams.NeuTTSReferenceText, out string nrt) ? nrt : "";
                break;

            case "acestep_music":
                args["lyrics"] = input.TryGet(AudioLabParams.Lyrics, out string ly) ? ly : "[Instrumental]";
                break;

            case "musicgen_music":
                string melodyRef = GetBase64Audio(input, AudioLabParams.MelodyAudio);
                if (!string.IsNullOrEmpty(melodyRef))
                    args["reference_audio"] = melodyRef;
                break;

            case "rvc_clone":
                args["pitch_shift"] = input.TryGet(AudioLabParams.PitchShift, out int pitchShift) ? pitchShift : 0;
                break;

            case "gptsovits_clone":
                args["text"] = input.Get(T2IParamTypes.Prompt, "");
                if (input.TryGet(AudioLabParams.ClonePromptText, out string gpt) && !string.IsNullOrEmpty(gpt))
                    args["prompt_text"] = gpt;
                args["language"] = input.TryGet(AudioLabParams.CloneLanguage, out string gl) ? gl : "en";
                break;
        }

        return args;
    }
}
