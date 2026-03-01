using SwarmUI.Media;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.AudioLab;

/// <summary>Registers AudioLab T2I parameters with feature flags.
/// Category-level params use category flags (audiolab_tts, audiolab_stt, etc.).
/// Provider-specific params use provider flags (kokoro_tts_params, etc.) for visibility.</summary>
public static class AudioLabParams
{
    // ===== Groups =====
    public static T2IParamGroup TTSGroup;
    public static T2IParamGroup STTGroup;
    public static T2IParamGroup MusicGroup;
    public static T2IParamGroup CloneGroup;
    public static T2IParamGroup FXGroup;
    public static T2IParamGroup SFXGroup;

    // ===== TTS shared (flag: audiolab_tts) =====
    public static T2IRegisteredParam<double> Volume;

    // ===== TTS — Chatterbox (flag: chatterbox_tts_params) =====
    public static T2IRegisteredParam<double> Exaggeration;
    public static T2IRegisteredParam<double> CFGWeight;

    // ===== TTS — Kokoro (flag: kokoro_tts_params) =====
    public static T2IRegisteredParam<string> KokoroVoice;
    public static T2IRegisteredParam<double> KokoroSpeed;

    // ===== TTS — Piper (flag: piper_tts_params) =====
    public static T2IRegisteredParam<string> PiperVoice;
    public static T2IRegisteredParam<double> PiperSpeed;

    // ===== TTS — Orpheus (flag: orpheus_tts_params) =====
    public static T2IRegisteredParam<string> OrpheusVoice;

    // ===== TTS — CSM (flag: csm_tts_params) =====
    public static T2IRegisteredParam<string> Speaker;

    // ===== TTS — VibeVoice (flag: vibevoice_tts_params) =====
    public static T2IRegisteredParam<double> CFGScale;

    // ===== TTS — F5-TTS (flag: f5_tts_params) =====
    public static T2IRegisteredParam<AudioFile> F5ReferenceAudio;
    public static T2IRegisteredParam<string> F5ReferenceText;

    // ===== TTS — Zonos (flag: zonos_tts_params) =====
    public static T2IRegisteredParam<AudioFile> ZonosReferenceAudio;
    public static T2IRegisteredParam<string> ZonosLanguage;

    // ===== TTS — CosyVoice (flag: cosyvoice_tts_params) =====
    public static T2IRegisteredParam<string> CosyVoiceVoice;
    public static T2IRegisteredParam<AudioFile> CosyVoiceReferenceAudio;
    public static T2IRegisteredParam<string> CosyVoiceReferenceText;

    // ===== TTS — NeuTTS (flag: neutts_tts_params) =====
    public static T2IRegisteredParam<AudioFile> NeuTTSReferenceAudio;
    public static T2IRegisteredParam<string> NeuTTSReferenceText;

    // ===== STT shared (flag: audiolab_stt) =====
    public static T2IRegisteredParam<AudioFile> AudioInput;
    public static T2IRegisteredParam<string> Language;

    // ===== Music shared (flag: audiolab_music) =====
    public static T2IRegisteredParam<double> Duration;

    // ===== Music — ACE-Step (flag: acestep_music_params) =====
    public static T2IRegisteredParam<string> Lyrics;

    // ===== Music — MusicGen (flag: musicgen_music_params) =====
    public static T2IRegisteredParam<AudioFile> MelodyAudio;

    // ===== Clone shared (flag: audiolab_clone) =====
    public static T2IRegisteredParam<AudioFile> SourceAudio;
    public static T2IRegisteredParam<AudioFile> TargetVoice;

    // ===== Clone — RVC (flag: rvc_clone_params) =====
    public static T2IRegisteredParam<int> PitchShift;

    // ===== Clone — GPT-SoVITS (flag: gptsovits_clone_params) =====
    public static T2IRegisteredParam<string> ClonePromptText;
    public static T2IRegisteredParam<string> CloneLanguage;

    // ===== FX shared (flag: audiolab_fx) =====
    public static T2IRegisteredParam<AudioFile> FXInput;

    // ===== SFX shared (flag: audiolab_sfx) =====
    public static T2IRegisteredParam<double> SFXDuration;

    /// <summary>Registers all AudioLab parameters. Called from AudioLab.OnInit().</summary>
    public static void RegisterAll()
    {
        // ========================== Groups ==========================
        TTSGroup = new("TTS", Open: true, OrderPriority: -28, Toggles: false,
            Description: "Text-to-speech parameters. Enter text in the Prompt box above.");
        STTGroup = new("STT", Open: true, OrderPriority: -27, Toggles: false,
            Description: "Speech-to-text parameters. Upload audio to transcribe.");
        MusicGroup = new("Music Generation", Open: true, OrderPriority: -26, Toggles: false,
            Description: "Music generation parameters. Describe the music in the Prompt box above.");
        CloneGroup = new("Voice Clone", Open: true, OrderPriority: -25, Toggles: false,
            Description: "Voice cloning parameters. Provide source and target audio.");
        FXGroup = new("Audio FX", Open: true, OrderPriority: -24, Toggles: false,
            Description: "Audio effects parameters. Upload audio to process.");
        SFXGroup = new("Sound FX", Open: true, OrderPriority: -23, Toggles: false,
            Description: "Sound effects generation. Describe the sound in the Prompt box above.");

        // ========================== TTS Shared ==========================
        Volume = T2IParamTypes.Register<double>(new("Volume",
            "Output volume multiplier.\n1.0 = full volume, 0.5 = half volume.",
            "0.8",
            Min: 0.1, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "audiolab_tts"));

        // ========================== TTS — Chatterbox ==========================
        Exaggeration = T2IParamTypes.Register<double>(new("Exaggeration",
            "Voice expressiveness level.\nHigher values produce more animated, expressive speech.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "chatterbox_tts_params"));

        CFGWeight = T2IParamTypes.Register<double>(new("CFG Weight",
            "Classifier-free guidance weight.\nHigher = more controlled/stable. Lower = more variation.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "chatterbox_tts_params"));

        // ========================== TTS — Kokoro ==========================
        KokoroVoice = T2IParamTypes.Register<string>(new("Kokoro Voice",
            "Voice preset for Kokoro TTS.",
            "af_heart",
            GetValues: _ => [
                "af_heart///Heart (Female)", "af_bella///Bella (Female)",
                "af_nicole///Nicole (Female)", "af_sarah///Sarah (Female)",
                "af_sky///Sky (Female)", "am_adam///Adam (Male)",
                "am_michael///Michael (Male)", "bf_emma///Emma (British F)",
                "bf_isabella///Isabella (British F)", "bm_george///George (British M)",
                "bm_lewis///Lewis (British M)"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "kokoro_tts_params"));

        KokoroSpeed = T2IParamTypes.Register<double>(new("Kokoro Speed",
            "Speech speed multiplier.\n1.0 = normal, 0.5 = half, 2.0 = double.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "kokoro_tts_params"));

        // ========================== TTS — Piper ==========================
        PiperVoice = T2IParamTypes.Register<string>(new("Piper Voice",
            "Piper voice model. CPU-only ONNX voices, auto-downloaded on first use.",
            "en_US-amy-medium",
            GetValues: _ => [
                "en_US-amy-medium///Amy (US Female)",
                "en_US-danny-low///Danny (US Male)",
                "en_GB-alba-medium///Alba (GB Female)"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "piper_tts_params"));

        PiperSpeed = T2IParamTypes.Register<double>(new("Piper Speed",
            "Speech speed multiplier.\n1.0 = normal, 0.5 = half, 2.0 = double.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "piper_tts_params"));

        // ========================== TTS — Orpheus ==========================
        OrpheusVoice = T2IParamTypes.Register<string>(new("Orpheus Voice",
            "Voice preset for Orpheus TTS.\nSupports emotion tags: <laugh>, <chuckle>, <sigh>, <cough>, <sniffle>, <groan>, <yawn>, <gasp>",
            "tara",
            GetValues: _ => [
                "tara///Tara (Female)", "leah///Leah (Female)", "jess///Jess (Female)",
                "leo///Leo (Male)", "dan///Dan (Male)", "mia///Mia (Female)",
                "zac///Zac (Male)", "zoe///Zoe (Female)"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "orpheus_tts_params"));

        // ========================== TTS — CSM ==========================
        Speaker = T2IParamTypes.Register<string>(new("Speaker",
            "Speaker ID for multi-speaker conversation.\n0 = primary speaker.",
            "0",
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "csm_tts_params"));

        // ========================== TTS — VibeVoice ==========================
        CFGScale = T2IParamTypes.Register<double>(new("VibeVoice CFG Scale",
            "Classifier-free guidance scale.\nHigher values increase adherence to the text prompt.",
            "1.3",
            Min: 0.1, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params"));

        // ========================== TTS — F5-TTS ==========================
        F5ReferenceAudio = T2IParamTypes.Register<AudioFile>(new("F5 Reference Audio",
            "15-second reference audio for zero-shot voice cloning.\nThe generated speech will match this voice.",
            null,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        F5ReferenceText = T2IParamTypes.Register<string>(new("F5 Reference Text",
            "Transcript of the reference audio.\nOptional but improves quality when provided.",
            "",
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        // ========================== TTS — Zonos ==========================
        ZonosReferenceAudio = T2IParamTypes.Register<AudioFile>(new("Zonos Reference Audio",
            "Reference audio for voice conditioning.\nOptional — uses default voice without reference.",
            null,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        ZonosLanguage = T2IParamTypes.Register<string>(new("Zonos Language",
            "Language for Zonos TTS synthesis.",
            "en-us",
            GetValues: _ => [
                "en-us///English (US)", "en-gb///English (UK)",
                "es///Spanish", "fr///French", "de///German",
                "it///Italian", "pt///Portuguese", "ja///Japanese",
                "zh///Chinese", "ko///Korean"
            ],
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        // ========================== TTS — CosyVoice ==========================
        CosyVoiceVoice = T2IParamTypes.Register<string>(new("CosyVoice Voice",
            "Built-in voice for CosyVoice TTS.\nUsed when no reference audio is provided.",
            "中文女",
            GetValues: _ => [
                "中文女///Chinese Female", "中文男///Chinese Male",
                "英文女///English Female", "英文男///English Male",
                "日语男///Japanese Male", "粤语女///Cantonese Female",
                "韩语女///Korean Female"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "cosyvoice_tts_params"));

        CosyVoiceReferenceAudio = T2IParamTypes.Register<AudioFile>(new("CosyVoice Reference Audio",
            "Reference audio for zero-shot voice cloning.\nOverrides the built-in voice when provided.",
            null,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "cosyvoice_tts_params"));

        CosyVoiceReferenceText = T2IParamTypes.Register<string>(new("CosyVoice Reference Text",
            "Transcript of the reference audio.\nImproves quality for zero-shot cloning.",
            "",
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "cosyvoice_tts_params"));

        // ========================== TTS — NeuTTS ==========================
        NeuTTSReferenceAudio = T2IParamTypes.Register<AudioFile>(new("NeuTTS Reference Audio",
            "Reference audio for instant voice cloning.\nRequired — the generated speech will match this voice.",
            null,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "neutts_tts_params"));

        NeuTTSReferenceText = T2IParamTypes.Register<string>(new("NeuTTS Reference Text",
            "Transcript of the reference audio.\nRequired for accurate voice cloning.",
            "",
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "neutts_tts_params"));

        // ========================== STT ==========================
        AudioInput = T2IParamTypes.Register<AudioFile>(new("Audio Input",
            "Audio file to transcribe.\nSupports WAV, MP3, and other common formats.",
            null,
            OrderPriority: -10, Group: STTGroup, FeatureFlag: "audiolab_stt"));

        Language = T2IParamTypes.Register<string>(new("Language",
            "Language hint for transcription.\n'auto' lets the model auto-detect.",
            "en",
            GetValues: _ => [
                "auto///Auto-detect", "en///English",
                "es///Spanish", "fr///French", "de///German",
                "it///Italian", "pt///Portuguese", "ja///Japanese",
                "zh///Chinese", "ko///Korean", "ru///Russian",
                "ar///Arabic", "nl///Dutch", "pl///Polish"
            ],
            OrderPriority: -9, Group: STTGroup, FeatureFlag: "audiolab_stt"));

        // ========================== Music ==========================
        Duration = T2IParamTypes.Register<double>(new("Duration",
            "Duration of generated music in seconds.\nLonger durations need more time and VRAM.",
            "30",
            Min: 1, Max: 300, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: MusicGroup, FeatureFlag: "audiolab_music"));

        // Music — ACE-Step
        Lyrics = T2IParamTypes.Register<string>(new("Lyrics",
            "Song lyrics for ACE-Step generation.\nUse [Instrumental] for instrumental-only tracks.",
            "[Instrumental]",
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        // Music — MusicGen
        MelodyAudio = T2IParamTypes.Register<AudioFile>(new("Melody Audio",
            "Reference melody for MusicGen melody conditioning.\nOnly used with the melody model variant.",
            null,
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "musicgen_music_params"));

        // ========================== Voice Clone ==========================
        SourceAudio = T2IParamTypes.Register<AudioFile>(new("Source Audio",
            "Audio with the voice to clone or the audio to convert.\nProvide a clean recording.",
            null,
            OrderPriority: -10, Group: CloneGroup, FeatureFlag: "audiolab_clone"));

        TargetVoice = T2IParamTypes.Register<AudioFile>(new("Target Voice",
            "Reference voice for tone conversion.\nThe source audio will be converted to match this voice.",
            null,
            OrderPriority: -9, Group: CloneGroup, FeatureFlag: "audiolab_clone"));

        // Clone — RVC
        PitchShift = T2IParamTypes.Register<int>(new("Pitch Shift",
            "Semitone pitch shift for RVC voice conversion.\n0 = no shift, +12 = octave up, -12 = octave down.",
            "0",
            Min: -12, Max: 12, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

        // Clone — GPT-SoVITS
        ClonePromptText = T2IParamTypes.Register<string>(new("Clone Prompt Text",
            "Transcript of the reference audio for GPT-SoVITS.\nImproves cloning accuracy when provided.",
            "",
            OrderPriority: -5, Group: CloneGroup, FeatureFlag: "gptsovits_clone_params"));

        CloneLanguage = T2IParamTypes.Register<string>(new("Clone Language",
            "Language for GPT-SoVITS voice cloning.",
            "en",
            GetValues: _ => [
                "en///English", "zh///Chinese",
                "ja///Japanese", "ko///Korean"
            ],
            OrderPriority: -4, Group: CloneGroup, FeatureFlag: "gptsovits_clone_params"));

        // ========================== Audio FX ==========================
        FXInput = T2IParamTypes.Register<AudioFile>(new("FX Input",
            "Audio file to process.\nUpload audio for separation, enhancement, or denoising.",
            null,
            OrderPriority: -10, Group: FXGroup, FeatureFlag: "audiolab_fx"));

        // ========================== Sound FX ==========================
        SFXDuration = T2IParamTypes.Register<double>(new("SFX Duration",
            "Duration of generated sound effect in seconds.",
            "10",
            Min: 1, Max: 60, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: SFXGroup, FeatureFlag: "audiolab_sfx"));
    }
}
