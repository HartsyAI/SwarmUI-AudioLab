using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

/// <summary>Parameter definitions for Voice Assistant backends.
/// These parameters appear in SwarmUI's Generate tab for voice processing.</summary>
public static class VoiceParameters
{
    #region Parameter Groups

    /// <summary>Speech-to-Text parameter group</summary>
    public static readonly T2IParamGroup STTGroup = new("Speech to Text", Toggles: true, Open: false, OrderPriority: 15);

    /// <summary>Text-to-Speech parameter group</summary>
    public static readonly T2IParamGroup TTSGroup = new("Text to Speech", Toggles: true, Open: false, OrderPriority: 16);

    /// <summary>Voice processing advanced options</summary>
    public static readonly T2IParamGroup VoiceAdvancedGroup = new("Voice Advanced", Toggles: true, Open: false, OrderPriority: 18);

    #endregion

    #region STT Parameters

    /// <summary>Audio input for STT processing</summary>
    public static readonly T2IRegisteredParam<object> AudioInput = T2IParamTypes.Register<object>(new(
        "Audio Input", null, "Audio data or file path for speech recognition",
        Group: STTGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["Upload audio file", "Record from microphone"]
    ));

    /// <summary>Language for STT processing</summary>
    public static readonly T2IRegisteredParam<string> STTLanguage = T2IParamTypes.Register<string>(new(
        "STT Language", "en-US", "Language for speech recognition",
        Group: STTGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "it-IT", "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"]
    ));

    /// <summary>STT model preference</summary>
    public static readonly T2IRegisteredParam<string> STTModelPreference = T2IParamTypes.Register<string>(new(
        "STT Model", "accuracy", "Speech recognition model preference",
        Group: STTGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["accuracy", "speed", "balanced"]
    ));

    /// <summary>Return confidence scores</summary>
    public static readonly T2IRegisteredParam<bool> STTReturnConfidence = T2IParamTypes.Register<bool>(new(
        "Return Confidence", "", "Include confidence scores in STT results",
        Group: STTGroup, OrderPriority: 4,
        VisibleNormally: false, FeatureFlag: "voice_stt_advanced"
    ));

    /// <summary>Return alternative transcriptions</summary>
    public static readonly T2IRegisteredParam<bool> STTReturnAlternatives = T2IParamTypes.Register<bool>(new(
        "Return Alternatives", "", "Include alternative transcriptions in STT results",
        Group: STTGroup, OrderPriority: 5,
        VisibleNormally: false, FeatureFlag: "voice_stt_advanced"
    ));

    #endregion

    #region TTS Parameters

    /// <summary>Text input for TTS processing</summary>
    public static readonly T2IRegisteredParam<string> TTSText = T2IParamTypes.Register<string>(new(
        "TTS Text", "", "Text to convert to speech (if empty, uses main prompt)",
        Group: TTSGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    /// <summary>Voice selection for TTS</summary>
    public static readonly T2IRegisteredParam<string> TTSVoice = T2IParamTypes.Register<string>(new(
        "TTS Voice", "default", "Voice to use for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["default", "expressive", "calm", "dramatic", "male", "female", "neural"]
    ));

    /// <summary>Language for TTS processing</summary>
    public static readonly T2IRegisteredParam<string> TTSLanguage = T2IParamTypes.Register<string>(new(
        "TTS Language", "en-US", "Language for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "it-IT", "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"]
    ));

    /// <summary>Volume control for TTS</summary>
    public static readonly T2IRegisteredParam<float> TTSVolume = T2IParamTypes.Register<float>(new(
        "TTS Volume", "0.8", "Volume level for generated speech (0.0 to 1.0)",
        Group: TTSGroup, OrderPriority: 4, Min: 0.0, Max: 1.0, Step: 0.1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    /// <summary>Speech speed control</summary>
    public static readonly T2IRegisteredParam<float> TTSSpeed = T2IParamTypes.Register<float>(new(
        "TTS Speed", "1.0", "Speech speed multiplier (0.5 = slower, 2.0 = faster)",
        Group: TTSGroup, OrderPriority: 5, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    /// <summary>Speech pitch control</summary>
    public static readonly T2IRegisteredParam<float> TTSPitch = T2IParamTypes.Register<float>(new(
        "TTS Pitch", "1", "Speech pitch multiplier (0.5 = lower, 2.0 = higher)",
        Group: TTSGroup, OrderPriority: 6, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    /// <summary>Audio output format</summary>
    public static readonly T2IRegisteredParam<string> TTSFormat = T2IParamTypes.Register<string>(new(
        "TTS Format", "wav", "Audio output format for generated speech",
        Group: TTSGroup, OrderPriority: 7,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced",
        Examples: ["wav", "mp3", "ogg"]
    ));
    #endregion

    #region Advanced Parameters

    /// <summary>Enable real-time processing</summary>
    public static readonly T2IRegisteredParam<bool> VoiceRealTime = T2IParamTypes.Register<bool>(new(
        "Real-time Processing", "", "Enable real-time voice processing with streaming",
        Group: VoiceAdvancedGroup, OrderPriority: 1,
        VisibleNormally: false, FeatureFlag: "voice_realtime"
    ));

    /// <summary>Audio processing quality</summary>
    public static readonly T2IRegisteredParam<string> VoiceQuality = T2IParamTypes.Register<string>(new(
        "Processing Quality", "balanced", "Audio processing quality vs speed trade-off",
        Group: VoiceAdvancedGroup, OrderPriority: 2,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["fast", "balanced", "high_quality"]
    ));

    /// <summary>Noise reduction level</summary>
    public static readonly T2IRegisteredParam<float> VoiceNoiseReduction = T2IParamTypes.Register<float>(new(
        "Noise Reduction", "0.5", "Audio noise reduction level (0.0 = none, 1.0 = maximum)",
        Group: VoiceAdvancedGroup, OrderPriority: 3, Min: 0.0, Max: 1.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_advanced"
    ));

    /// <summary>Audio sample rate</summary>
    public static readonly T2IRegisteredParam<int> VoiceSampleRate = T2IParamTypes.Register<int>(new(
        "Sample Rate", "22050", "Audio sample rate in Hz",
        Group: VoiceAdvancedGroup, OrderPriority: 4, Min: 8000, Max: 48000, Step: 1000,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["8000", "16000", "22050", "44100", "48000"]
    ));

    /// <summary>Enable GPU acceleration</summary>
    public static readonly T2IRegisteredParam<bool> VoiceGPUAcceleration = T2IParamTypes.Register<bool>(new(
        "GPU Acceleration", "", "Use GPU acceleration for voice processing when available",
        Group: VoiceAdvancedGroup, OrderPriority: 5,
        VisibleNormally: false, FeatureFlag: "voice_advanced"
    ));

    /// <summary>Processing timeout</summary>
    public static readonly T2IRegisteredParam<int> VoiceTimeout = T2IParamTypes.Register<int>(new(
        "Processing Timeout", "30", "Maximum processing time in seconds",
        Group: VoiceAdvancedGroup, OrderPriority: 6, Min: 5, Max: 300, Step: 5,
        VisibleNormally: false, FeatureFlag: "voice_advanced"
    ));

    /// <summary>Debug mode</summary>
    public static readonly T2IRegisteredParam<bool> VoiceDebugMode = T2IParamTypes.Register<bool>(new(
        "Debug Mode", "", "Enable detailed logging for voice processing debugging",
        Group: VoiceAdvancedGroup, OrderPriority: 7,
        VisibleNormally: false, FeatureFlag: "voice_debug"
    ));

    #endregion

    #region Feature Flags

    /// <summary>Initialize feature flags for voice processing</summary>
    public static void InitializeFeatureFlags()
    {
        // Register feature flags that control parameter visibility
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_stt", "Speech to Text features", ""));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_tts", "Text to Speech features", typeof(bool), false));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_stt_advanced", "Advanced STT options", typeof(bool), false));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_tts_advanced", "Advanced TTS options", typeof(bool), false));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_realtime", "Real-time processing", typeof(bool), false));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_advanced", "Advanced voice options", typeof(bool), false));
        //T2IParamTypes.FakeTypeProviders.Add(new T2IParamType("voice_debug", "Voice debugging features", typeof(bool), false));
    }

    #endregion
}