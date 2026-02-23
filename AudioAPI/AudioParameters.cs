using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.VoiceAssistant.AudioAPI;

/// <summary>Parameter definitions for audio processing backends.
/// These parameters appear in SwarmUI's Generate tab.</summary>
public static class AudioParameters
{
    #region Parameter Groups

    public static readonly T2IParamGroup STTGroup = new("Speech to Text", Toggles: true, Open: false, OrderPriority: 15);
    public static readonly T2IParamGroup TTSGroup = new("Text to Speech", Toggles: true, Open: false, OrderPriority: 16);
    public static readonly T2IParamGroup MusicGenGroup = new("Music Generation", Toggles: true, Open: false, OrderPriority: 17);
    public static readonly T2IParamGroup VoiceCloneGroup = new("Voice Cloning", Toggles: true, Open: false, OrderPriority: 18);
    public static readonly T2IParamGroup AudioFXGroup = new("Audio Effects", Toggles: true, Open: false, OrderPriority: 19);
    public static readonly T2IParamGroup SoundFXGroup = new("Sound Effects", Toggles: true, Open: false, OrderPriority: 20);
    public static readonly T2IParamGroup AudioAdvancedGroup = new("Audio Advanced", Toggles: true, Open: false, OrderPriority: 21);

    #endregion

    #region STT Parameters

    public static readonly T2IRegisteredParam<object> AudioInput = T2IParamTypes.Register<object>(new(
        "Audio Input", null, "Audio data or file path for speech recognition",
        Group: STTGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["Upload audio file", "Record from microphone"]
    ));

    public static readonly T2IRegisteredParam<string> STTLanguage = T2IParamTypes.Register<string>(new(
        "STT Language", "en-US", "Language for speech recognition",
        Group: STTGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "ja-JP", "zh-CN"]
    ));

    public static readonly T2IRegisteredParam<string> STTModelPreference = T2IParamTypes.Register<string>(new(
        "STT Model", "accuracy", "Speech recognition model preference",
        Group: STTGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["accuracy", "speed", "balanced"]
    ));

    #endregion

    #region TTS Parameters

    public static readonly T2IRegisteredParam<string> TTSText = T2IParamTypes.Register<string>(new(
        "TTS Text", "", "Text to convert to speech (if empty, uses main prompt)",
        Group: TTSGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    public static readonly T2IRegisteredParam<string> TTSVoice = T2IParamTypes.Register<string>(new(
        "TTS Voice", "default", "Voice to use for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["default", "expressive", "calm", "dramatic"]
    ));

    public static readonly T2IRegisteredParam<string> TTSLanguage = T2IParamTypes.Register<string>(new(
        "TTS Language", "en-US", "Language for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "ja-JP", "zh-CN"]
    ));

    public static readonly T2IRegisteredParam<float> TTSVolume = T2IParamTypes.Register<float>(new(
        "TTS Volume", "0.8", "Volume level for generated speech (0.0 to 1.0)",
        Group: TTSGroup, OrderPriority: 4, Min: 0.0, Max: 1.0, Step: 0.1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    public static readonly T2IRegisteredParam<float> TTSSpeed = T2IParamTypes.Register<float>(new(
        "TTS Speed", "1.0", "Speech speed multiplier",
        Group: TTSGroup, OrderPriority: 5, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    public static readonly T2IRegisteredParam<float> TTSPitch = T2IParamTypes.Register<float>(new(
        "TTS Pitch", "1", "Speech pitch multiplier",
        Group: TTSGroup, OrderPriority: 6, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    public static readonly T2IRegisteredParam<string> TTSFormat = T2IParamTypes.Register<string>(new(
        "TTS Format", "wav", "Audio output format",
        Group: TTSGroup, OrderPriority: 7,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced",
        Examples: ["wav", "mp3", "ogg"]
    ));

    #endregion

    #region Music Generation Parameters

    public static readonly T2IRegisteredParam<string> MusicPrompt = T2IParamTypes.Register<string>(new(
        "Music Prompt", "", "Text description of the music to generate",
        Group: MusicGenGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "musicgen_params"
    ));

    public static readonly T2IRegisteredParam<float> MusicDuration = T2IParamTypes.Register<float>(new(
        "Music Duration", "10", "Duration of generated music in seconds",
        Group: MusicGenGroup, OrderPriority: 2, Min: 1.0, Max: 60.0, Step: 1.0,
        VisibleNormally: true, FeatureFlag: "musicgen_params"
    ));

    #endregion

    #region Advanced Parameters

    public static readonly T2IRegisteredParam<string> AudioQuality = T2IParamTypes.Register<string>(new(
        "Processing Quality", "balanced", "Audio processing quality vs speed trade-off",
        Group: AudioAdvancedGroup, OrderPriority: 1,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["fast", "balanced", "high_quality"]
    ));

    public static readonly T2IRegisteredParam<int> AudioSampleRate = T2IParamTypes.Register<int>(new(
        "Sample Rate", "22050", "Audio sample rate in Hz",
        Group: AudioAdvancedGroup, OrderPriority: 2, Min: 8000, Max: 48000, Step: 1000,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["8000", "16000", "22050", "44100", "48000"]
    ));

    public static readonly T2IRegisteredParam<bool> AudioDebugMode = T2IParamTypes.Register<bool>(new(
        "Debug Mode", "", "Enable detailed logging for audio processing debugging",
        Group: AudioAdvancedGroup, OrderPriority: 3,
        VisibleNormally: false, FeatureFlag: "voice_debug"
    ));

    #endregion
}
