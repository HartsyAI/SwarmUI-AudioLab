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

            // Pre-create venvs and install dependencies for active compatibility groups
            HashSet<string> activeGroups = [];
            foreach (AudioProviderMetadata providerMeta in _providers.Values)
            {
                if (!providerMeta.Definition.RequiresDocker)
                {
                    activeGroups.Add(providerMeta.Definition.EngineGroup);
                }
            }
            AudioDependencyInstaller installer = new();
            foreach (string group in activeGroups)
            {
                string venvPython = await VenvManager.Instance.EnsureVenvAsync(group);
                if (venvPython == null)
                {
                    Logs.Warning($"[AudioLab] Failed to create venv for group '{group}'. Providers in this group may not work until Python is available.");
                    continue;
                }
                // Install dependencies for all enabled providers in this group
                List<AudioProviderDefinition> groupProviders = [.. _providers.Values
                    .Where(p => !p.Definition.RequiresDocker && p.Definition.EngineGroup == group)
                    .Select(p => p.Definition)];
                if (groupProviders.Count == 0) continue;
                PythonEnvironmentInfo groupPython = await installer.DetectPythonEnvironmentForGroupAsync(group);
                if (groupPython?.IsValid != true)
                {
                    Logs.Warning($"[AudioLab] Cannot install dependencies for group '{group}' — venv not available.");
                    continue;
                }
                Logs.Info($"[AudioLab] Installing dependencies for {groupProviders.Count} provider(s) in group '{group}'...");
                try
                {
                    await installer.InstallMultipleProviderDependenciesAsync(groupPython, groupProviders);
                    Logs.Info($"[AudioLab] Dependencies ready for group '{group}'");
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[AudioLab] Dependency install for group '{group}' had issues: {ex.Message}. Some providers may not work until dependencies are installed.");
                }
            }

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

        // Check streaming conditions: TTS + StreamAudio enabled + 2+ sentences
        if (provider.Category == AudioCategory.TTS
            && user_input.TryGet(AudioLabParams.StreamAudio, out bool streamOn) && streamOn)
        {
            string text = user_input.Get(T2IParamTypes.Prompt, "");
            List<string> sentences = SplitIntoSentences(text);
            if (sentences != null)
            {
                await GenerateLiveStreaming(user_input, batchId, takeOutput, meta, provider, modelDef, sentences);
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

    /// <summary>Streaming generation path — generates each sentence separately,
    /// sends intermediate audio chunks for immediate playback, then concatenates
    /// all PCM data into a final WAV file as the real output.</summary>
    private async Task GenerateLiveStreaming(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef, List<string> sentences)
    {
        Logs.Info($"[AudioLab] Streaming TTS: {sentences.Count} sentences via {provider.Name}");

        // Enable DoNotSaveIntermediates so chunk outputs aren't saved to disk
        user_input.Set(T2IParamTypes.DoNotSaveIntermediates, true);

        List<byte[]> pcmChunks = [];
        int sampleRate = 24000;
        int channels = 1;
        int bitsPerSample = 16;
        bool formatRead = false;
        double totalDuration = 0;
        int consecutiveFailures = 0;

        for (int i = 0; i < sentences.Count; i++)
        {
            // Send progress update
            double overallPercent = (double)i / sentences.Count * 100;
            takeOutput(new JObject
            {
                ["gen_progress"] = new JObject
                {
                    ["batch_index"] = batchId,
                    ["overall_percent"] = overallPercent,
                    ["current_status"] = $"Generating sentence {i + 1}/{sentences.Count}..."
                }
            });

            // Build args with this sentence as the text
            T2IParamInput chunkInput = user_input.Clone();
            chunkInput.Set(T2IParamTypes.Prompt, sentences[i]);
            Dictionary<string, object> args = BuildEngineArgs(chunkInput, provider, modelDef);

            try
            {
                JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args);

                if (result["success"]?.Value<bool>() == true)
                {
                    string audioBase64 = result["audio_data"]?.ToString();
                    if (!string.IsNullOrEmpty(audioBase64))
                    {
                        byte[] audioBytes = Convert.FromBase64String(audioBase64);

                        // Read format from first chunk
                        if (!formatRead)
                        {
                            (sampleRate, channels, bitsPerSample) = ReadWavFormat(audioBytes);
                            formatRead = true;
                        }

                        // Accumulate raw PCM for final concatenation
                        pcmChunks.Add(StripWavHeader(audioBytes));

                        // Send chunk as intermediate (non-real) output for auto-play
                        AudioFile chunkAudio = new(audioBytes, MediaType.AudioWav);
                        takeOutput(new T2IEngine.ImageOutput { File = chunkAudio, IsReal = false });

                        double chunkDuration = result["duration"]?.Value<double>() ?? 0;
                        totalDuration += chunkDuration;
                        Logs.Debug($"[AudioLab] Streamed sentence {i + 1}/{sentences.Count}: {chunkDuration:F2}s");
                    }
                    consecutiveFailures = 0;
                }
                else
                {
                    string error = result["error"]?.ToString() ?? "Unknown error";
                    Logs.Warning($"[AudioLab] Sentence {i + 1} failed: {error}");
                    // Abort early on missing dependencies — all subsequent sentences will fail identically
                    if (error.Contains("No module named") || error.Contains("ModuleNotFoundError"))
                    {
                        Logs.Error($"[AudioLab] Missing Python dependency for {provider.Name}: {error}. Install provider dependencies via the AudioLab UI before generating audio.");
                        meta.LastError = $"Missing dependency: {error}. Install via AudioLab UI.";
                        break;
                    }
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining sentences.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Sentence {i + 1} error: {ex.Message}");
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining sentences.");
                    break;
                }
            }
        }

        // Concatenate all PCM chunks into final WAV
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
            Logs.Error($"[AudioLab] Streaming TTS produced no audio via {provider.Name}");
            meta.LastError ??= "Streaming generation produced no audio";
        }

        // Final progress
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

            case AudioCategory.MusicGen:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = input.TryGet(AudioLabParams.Duration, out double musicDur) ? musicDur : 30.0;
                // Shared AudioCraft sampling (audiocraft_sampling flag)
                if (input.TryGet(AudioLabParams.GuidanceScale, out double musicGuidance))
                    args["cfg_coef"] = musicGuidance;
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
                // Shared AudioCraft sampling (audiocraft_sampling flag)
                if (input.TryGet(AudioLabParams.GuidanceScale, out double sfxGuidance))
                    args["cfg_coef"] = sfxGuidance;
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
                args["diffusion_steps"] = input.TryGet(AudioLabParams.DiffusionSteps, out int diffSteps) ? diffSteps : 20;
                break;

            case "dia_tts":
                args["cfg_filter_top_k"] = input.TryGet(AudioLabParams.CFGFilterTopK, out int cfgTopK) ? cfgTopK : 35;
                break;

            case "f5_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.NFEStep, out int nfeStep) ? nfeStep : 32;
                args["speed"] = input.TryGet(AudioLabParams.F5Speed, out double f5spd) ? f5spd : 1.0;
                break;

            case "zonos_tts":
                if (input.TryGet(AudioLabParams.ZonosLanguage, out string zl))
                    args["language"] = zl;
                if (input.TryGet(AudioLabParams.ZonosEmotion, out string ze))
                    args["emotion"] = ze;
                args["speaking_rate"] = input.TryGet(AudioLabParams.SpeakingRate, out double sr) ? sr : 15.0;
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
                args["seed"] = input.TryGet(AudioLabParams.AudioSeed, out int aceSeed) ? aceSeed : -1;
                args["infer_step"] = input.TryGet(AudioLabParams.InferStep, out int infStep) ? infStep : 60;
                args["guidance_scale"] = input.TryGet(AudioLabParams.ACEGuidanceScale, out double aceGuide) ? aceGuide : 15.0;
                args["scheduler_type"] = input.TryGet(AudioLabParams.SchedulerType, out string sched) ? sched : "euler";
                args["cfg_type"] = input.TryGet(AudioLabParams.CFGType, out string cfgType) ? cfgType : "apg";
                break;

            case "musicgen_music":
                string melodyRef = GetBase64Audio(input, AudioLabParams.MelodyAudio);
                if (!string.IsNullOrEmpty(melodyRef))
                    args["reference_audio"] = melodyRef;
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

    // -- Sentence splitting + WAV helpers for streaming -------------------

    /// <summary>Common abbreviations that should not trigger sentence breaks.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc",
        "Inc", "Ltd", "Corp", "Ave", "Blvd", "Dept", "Est", "Fig", "Gen",
        "Gov", "No", "Sgt", "Vol"
    };

    /// <summary>Splits text into sentences for streaming TTS.
    /// Returns null if fewer than 2 sentences (caller should use normal path).</summary>
    private static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Replace abbreviation periods with placeholder to avoid false splits
        string processed = text;
        foreach (string abbr in Abbreviations)
        {
            processed = Regex.Replace(processed, $@"\b{Regex.Escape(abbr)}\.", $"{abbr}\x00");
        }
        // Also handle U.S.-style abbreviations (single letters with periods)
        processed = Regex.Replace(processed, @"\b([A-Z])\.([A-Z])\.", "$1\x00$2\x00");

        // Split on sentence-ending punctuation followed by whitespace or end
        string[] parts = Regex.Split(processed, @"(?<=[.!?])\s+");

        // Restore abbreviation periods
        List<string> sentences = [];
        foreach (string part in parts)
        {
            string restored = part.Replace("\x00", ".").Trim();
            if (!string.IsNullOrWhiteSpace(restored))
            {
                sentences.Add(restored);
            }
        }

        // Merge short fragments (< 20 chars) with the previous sentence
        for (int i = sentences.Count - 1; i > 0; i--)
        {
            if (sentences[i].Length < 20)
            {
                sentences[i - 1] = sentences[i - 1] + " " + sentences[i];
                sentences.RemoveAt(i);
            }
        }

        return sentences.Count >= 2 ? sentences : null;
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
}
