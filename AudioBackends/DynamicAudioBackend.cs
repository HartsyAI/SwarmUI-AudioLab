using System.Collections.Concurrent;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioBackends;

/// <summary>A single routing backend that replaces STTBackend + TTSBackend.
/// Mirrors DynamicAPIBackend from SwarmUI-API-Backends — provider toggles in settings
/// control which audio engines are available, and model prefix matching routes requests
/// to the correct provider's Python engine.</summary>
public class DynamicAudioBackend : AbstractT2IBackend
{
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

        [ConfigComment("Enable CosyVoice TTS (Alibaba streaming TTS with ultra-low latency).")]
        public bool EnableCosyVoice = false;

        [ConfigComment("Enable NeuTTS Air (on-device TTS with instant voice cloning by Neuphonic).")]
        public bool EnableNeuTTS = false;

        [ConfigComment("Enable Piper TTS (CPU-only ONNX runtime with dozens of pre-trained voices).")]
        public bool EnablePiper = false;

        // -- STT providers --
        [ConfigComment("Enable Whisper STT (robust speech recognition across languages).")]
        public bool EnableWhisper = false;

        [ConfigComment("Enable RealtimeSTT (real-time streaming speech-to-text with wake word).")]
        public bool EnableRealtimeSTT = false;

        [ConfigComment("Enable Moonshine STT (ultra-fast, 5x faster than Whisper).")]
        public bool EnableMoonshine = false;

        [ConfigComment("Enable Distil-Whisper STT (6x faster than Whisper large-v3, within 1% WER).")]
        public bool EnableDistilWhisper = false;

        // -- Music generation providers --
        [ConfigComment("Enable ACE-Step (SOTA music generation, up to 4 min in 20 seconds).")]
        public bool EnableAceStep = false;

        [ConfigComment("Enable MusicGen (Meta AudioCraft text-to-music with melody conditioning).")]
        public bool EnableMusicGen = false;

        // -- Voice cloning providers --
        [ConfigComment("Enable OpenVoice V2 (zero-shot voice cloning with tone/style control).")]
        public bool EnableOpenVoice = false;

        [ConfigComment("Enable RVC (retrieval-based voice conversion, industry standard).")]
        public bool EnableRVC = false;

        [ConfigComment("Enable GPT-SoVITS (few-shot voice cloning, strong CJK support).")]
        public bool EnableGPTSoVITS = false;

        // -- Audio FX providers --
        [ConfigComment("Enable Demucs (Meta audio source separation — vocals, drums, bass, other).")]
        public bool EnableDemucs = false;

        [ConfigComment("Enable Resemble Enhance (speech denoising and super-resolution to 44.1kHz).")]
        public bool EnableResembleEnhance = false;

        // -- Sound FX providers --
        [ConfigComment("Enable AudioGen (Meta AudioCraft text-to-sound-effects generation).")]
        public bool EnableAudioGen = false;

        // -- General --
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

    /// <summary>Runtime state for initialized providers, keyed by provider ID.</summary>
    private readonly Dictionary<string, AudioProviderMetadata> _providers = [];

    /// <summary>Current settings accessor.</summary>
    public DynamicAudioSettings Settings => SettingsRaw as DynamicAudioSettings;

    /// <summary>Feature flags from all enabled providers.</summary>
    public override IEnumerable<string> SupportedFeatures =>
        _providers.Values
            .Where(p => p.IsEnabled)
            .SelectMany(p => p.Definition.FeatureFlags)
            .Distinct();

    public DynamicAudioBackend()
    {
        SettingsRaw = new DynamicAudioSettings();
        Status = BackendStatus.LOADING;
    }

    /// <summary>Initializes the backend — enables providers based on settings toggles,
    /// checks dependencies, and prepares the Python processor.</summary>
    public override async Task Init()
    {
        Status = BackendStatus.LOADING;
        _providers.Clear();

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
            // Initialize the Python processor
            bool pythonReady = await PythonAudioProcessor.Instance.InitializeAsync();
            if (!pythonReady)
            {
                Logs.Error("[AudioLab] Python audio processor failed to initialize.");
                Status = BackendStatus.ERRORED;
                AddLoadStatus("Python environment not available.");
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

                AudioProviderMetadata meta = new()
                {
                    Definition = definition,
                    IsEnabled = true
                };
                _providers[providerId] = meta;

                if (Settings.DebugMode)
                {
                    Logs.Debug($"[AudioLab] Enabled provider: {definition.Name} ({providerId})");
                }
            }

            Status = BackendStatus.RUNNING;
            Logs.Info($"[AudioLab] Audio backend initialized with {_providers.Count} provider(s): " +
                      string.Join(", ", _providers.Keys));
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Audio backend initialization failed: {ex}");
            Status = BackendStatus.ERRORED;
        }
    }

    /// <summary>Generates output by routing to the correct provider based on the model name.</summary>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        string modelName = user_input.Get(T2IParamTypes.Model)?.Name ?? "";
        string providerId = GetProviderIdFromModel(modelName);

        if (providerId == null || !_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            Logs.Error($"[AudioLab] No provider found for model: {modelName}");
            return [];
        }

        AudioProviderDefinition provider = meta.Definition;

        // Build args dict from T2I params based on category
        Dictionary<string, object> args = BuildEngineArgs(user_input, provider);

        try
        {
            JObject result = await PythonAudioProcessor.Instance.ProcessAsync(provider, args);

            if (result["success"]?.Value<bool>() != true)
            {
                string error = result["error"]?.ToString() ?? "Unknown error";
                Logs.Error($"[AudioLab] Provider {provider.Name} failed: {error}");
                return [];
            }

            meta.LastUsed = DateTime.UtcNow;

            // Log result info
            if (provider.Category == AudioCategory.TTS)
            {
                double duration = result["duration"]?.Value<double>() ?? 0;
                Logs.Info($"[AudioLab] TTS generated {duration:F2}s of audio via {provider.Name}");
            }
            else if (provider.Category == AudioCategory.STT)
            {
                string transcription = result["text"]?.ToString() ?? "";
                Logs.Info($"[AudioLab] STT transcription via {provider.Name}: {transcription}");
            }

            // Audio output is saved via API endpoints; Generate() returns empty since
            // AbstractT2IBackend.Generate() returns Image[] which cannot hold AudioFile
            return [];
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error processing with {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
            return [];
        }
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

    /// <summary>Shuts down the backend and cleans up providers.</summary>
    public override async Task Shutdown()
    {
        Logs.Info("[AudioLab] Shutting down audio backend");
        _providers.Clear();
        await PythonAudioProcessor.Instance.CleanupAsync();
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

    /// <summary>Builds engine kwargs from T2I parameters based on the provider's category.</summary>
    private static Dictionary<string, object> BuildEngineArgs(T2IParamInput input, AudioProviderDefinition provider)
    {
        Dictionary<string, object> args = [];

        switch (provider.Category)
        {
            case AudioCategory.TTS:
                args["text"] = input.Get(T2IParamTypes.Prompt, "Hello world");
                args["voice"] = "default";
                args["language"] = "en-US";
                args["volume"] = 0.8;
                break;

            case AudioCategory.STT:
                args["audio_data"] = "";
                args["language"] = "en-US";
                break;

            case AudioCategory.MusicGen:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = 30.0;
                break;

            case AudioCategory.VoiceClone:
                args["source_audio"] = "";
                args["target_voice"] = "";
                break;

            case AudioCategory.AudioFX:
                args["audio_data"] = "";
                args["effect"] = "enhance";
                break;

            case AudioCategory.SoundFX:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                args["duration"] = 10.0;
                break;

            default:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                break;
        }

        return args;
    }
}
