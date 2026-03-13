using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>Parameter definitions for audio processing backends.
/// These parameters appear in SwarmUI's Generate tab.</summary>
public static class AudioParameters
{
    #region Parameter Groups

    /// <summary>Parameter group for speech-to-text recognition settings.</summary>
    public static readonly T2IParamGroup STTGroup = new("Speech to Text", Toggles: true, Open: false, OrderPriority: 15);

    /// <summary>Parameter group for text-to-speech synthesis settings.</summary>
    public static readonly T2IParamGroup TTSGroup = new("Text to Speech", Toggles: true, Open: false, OrderPriority: 16);

    /// <summary>Parameter group for audio generation settings (music + sound effects).</summary>
    public static readonly T2IParamGroup AudioGenerationGroup = new("Audio Generation", Toggles: true, Open: false, OrderPriority: 17);

    /// <summary>Parameter group for voice conversion settings.</summary>
    public static readonly T2IParamGroup VoiceConversionGroup = new("Voice Conversion", Toggles: true, Open: false, OrderPriority: 18);

    /// <summary>Parameter group for audio processing settings (stem separation, enhancement).</summary>
    public static readonly T2IParamGroup AudioProcessingGroup = new("Audio Processing", Toggles: true, Open: false, OrderPriority: 19);

    /// <summary>Parameter group for advanced audio processing options.</summary>
    public static readonly T2IParamGroup AudioAdvancedGroup = new("Audio Advanced", Toggles: true, Open: false, OrderPriority: 21);

    #endregion

    #region STT Parameters

    /// <summary>Audio file input for speech recognition.</summary>
    public static readonly T2IRegisteredParam<AudioFile> AudioInput = T2IParamTypes.Register<AudioFile>(new(
        "Audio Input", null, "Audio data or file path for speech recognition",
        Group: STTGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["Upload audio file", "Record from microphone"]
    ));

    /// <summary>Language code for speech recognition (e.g. en-US, es-ES).</summary>
    public static readonly T2IRegisteredParam<string> STTLanguage = T2IParamTypes.Register<string>(new(
        "STT Language", "en-US", "Language for speech recognition",
        Group: STTGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "ja-JP", "zh-CN"]
    ));

    /// <summary>Speech recognition model preference (accuracy, speed, balanced).</summary>
    public static readonly T2IRegisteredParam<string> STTModelPreference = T2IParamTypes.Register<string>(new(
        "STT Model", "accuracy", "Speech recognition model preference",
        Group: STTGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_stt",
        Examples: ["accuracy", "speed", "balanced"]
    ));

    #endregion

    #region TTS Parameters

    /// <summary>Text to convert to speech. Falls back to main prompt if empty.</summary>
    public static readonly T2IRegisteredParam<string> TTSText = T2IParamTypes.Register<string>(new(
        "TTS Text", "", "Text to convert to speech (if empty, uses main prompt)",
        Group: TTSGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    /// <summary>Voice identifier for text-to-speech synthesis.</summary>
    public static readonly T2IRegisteredParam<string> TTSVoice = T2IParamTypes.Register<string>(new(
        "TTS Voice", "default", "Voice to use for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 2,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["default", "expressive", "calm", "dramatic"]
    ));

    /// <summary>Language code for text-to-speech synthesis.</summary>
    public static readonly T2IRegisteredParam<string> TTSLanguage = T2IParamTypes.Register<string>(new(
        "TTS Language", "en-US", "Language for text-to-speech synthesis",
        Group: TTSGroup, OrderPriority: 3,
        VisibleNormally: true, FeatureFlag: "voice_tts",
        Examples: ["en-US", "es-ES", "fr-FR", "de-DE", "ja-JP", "zh-CN"]
    ));

    /// <summary>Volume level for generated speech (0.0 to 1.0).</summary>
    public static readonly T2IRegisteredParam<float> TTSVolume = T2IParamTypes.Register<float>(new(
        "TTS Volume", "0.8", "Volume level for generated speech (0.0 to 1.0)",
        Group: TTSGroup, OrderPriority: 4, Min: 0.0, Max: 1.0, Step: 0.1,
        VisibleNormally: true, FeatureFlag: "voice_tts"
    ));

    /// <summary>Speech speed multiplier (0.25 to 3.0).</summary>
    public static readonly T2IRegisteredParam<float> TTSSpeed = T2IParamTypes.Register<float>(new(
        "TTS Speed", "1.0", "Speech speed multiplier",
        Group: TTSGroup, OrderPriority: 5, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    /// <summary>Speech pitch multiplier (0.25 to 3.0).</summary>
    public static readonly T2IRegisteredParam<float> TTSPitch = T2IParamTypes.Register<float>(new(
        "TTS Pitch", "1", "Speech pitch multiplier",
        Group: TTSGroup, OrderPriority: 6, Min: 0.25, Max: 3.0, Step: 0.1,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced"
    ));

    /// <summary>Audio output format (wav, mp3, ogg).</summary>
    public static readonly T2IRegisteredParam<string> TTSFormat = T2IParamTypes.Register<string>(new(
        "TTS Format", "wav", "Audio output format",
        Group: TTSGroup, OrderPriority: 7,
        VisibleNormally: false, FeatureFlag: "voice_tts_advanced",
        Examples: ["wav", "mp3", "ogg"]
    ));

    #endregion

    #region Audio Generation Parameters

    /// <summary>Text description of the music to generate.</summary>
    public static readonly T2IRegisteredParam<string> MusicPrompt = T2IParamTypes.Register<string>(new(
        "Music Prompt", "", "Text description of the music to generate",
        Group: AudioGenerationGroup, OrderPriority: 1,
        VisibleNormally: true, FeatureFlag: "musicgen_params"
    ));

    /// <summary>Duration of generated music in seconds (1 to 60).</summary>
    public static readonly T2IRegisteredParam<float> MusicDuration = T2IParamTypes.Register<float>(new(
        "Music Duration", "10", "Duration of generated music in seconds",
        Group: AudioGenerationGroup, OrderPriority: 2, Min: 1.0, Max: 60.0, Step: 1.0,
        VisibleNormally: true, FeatureFlag: "musicgen_params"
    ));

    #endregion

    #region Advanced Parameters

    /// <summary>Audio processing quality vs speed trade-off.</summary>
    public static readonly T2IRegisteredParam<string> AudioQuality = T2IParamTypes.Register<string>(new(
        "Processing Quality", "balanced", "Audio processing quality vs speed trade-off",
        Group: AudioAdvancedGroup, OrderPriority: 1,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["fast", "balanced", "high_quality"]
    ));

    /// <summary>Audio sample rate in Hz (8000 to 48000).</summary>
    public static readonly T2IRegisteredParam<int> AudioSampleRate = T2IParamTypes.Register<int>(new(
        "Sample Rate", "22050", "Audio sample rate in Hz",
        Group: AudioAdvancedGroup, OrderPriority: 2, Min: 8000, Max: 48000, Step: 1000,
        VisibleNormally: false, FeatureFlag: "voice_advanced",
        Examples: ["8000", "16000", "22050", "44100", "48000"]
    ));

    /// <summary>Enables detailed logging for audio processing debugging.</summary>
    public static readonly T2IRegisteredParam<bool> AudioDebugMode = T2IParamTypes.Register<bool>(new(
        "Debug Mode", "", "Enable detailed logging for audio processing debugging",
        Group: AudioAdvancedGroup, OrderPriority: 3,
        VisibleNormally: false, FeatureFlag: "voice_debug"
    ));

    #endregion
}
