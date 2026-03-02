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
    public static T2IParamGroup VoiceRefGroup;
    public static T2IParamGroup CloneGroup;
    public static T2IParamGroup FXGroup;
    public static T2IParamGroup SFXGroup;

    // ===== TTS shared (flag: audiolab_tts) =====
    public static T2IRegisteredParam<double> Volume;
    public static T2IRegisteredParam<bool> StreamAudio;

    // ===== TTS shared sampling (flag: tts_sampling) =====
    public static T2IRegisteredParam<double> Temperature;
    public static T2IRegisteredParam<double> TopP;
    public static T2IRegisteredParam<double> RepetitionPenalty;
    public static T2IRegisteredParam<int> TopK;
    public static T2IRegisteredParam<double> MinP;

    // ===== Voice Reference shared (flag: tts_voice_ref) =====
    public static T2IRegisteredParam<AudioFile> ReferenceAudio;
    public static T2IRegisteredParam<string> ReferenceText;

    // ===== TTS — Bark (flag: bark_tts_params) =====
    public static T2IRegisteredParam<string> BarkVoice;
    public static T2IRegisteredParam<double> TextTemp;
    public static T2IRegisteredParam<double> WaveformTemp;

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
    public static T2IRegisteredParam<int> DiffusionSteps;

    // ===== TTS — Dia (flag: dia_tts_params) =====
    public static T2IRegisteredParam<int> CFGFilterTopK;

    // ===== TTS — F5-TTS (flag: f5_tts_params) =====
    public static T2IRegisteredParam<int> NFEStep;
    public static T2IRegisteredParam<double> F5Speed;

    // ===== TTS — Zonos (flag: zonos_tts_params) =====
    public static T2IRegisteredParam<string> ZonosLanguage;
    public static T2IRegisteredParam<string> ZonosEmotion;
    public static T2IRegisteredParam<double> SpeakingRate;

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

    // ===== Music — AudioCraft shared (flag: audiocraft_sampling) =====
    public static T2IRegisteredParam<double> GuidanceScale;

    // ===== Music — ACE-Step (flag: acestep_music_params) =====
    public static T2IRegisteredParam<string> Lyrics;
    public static T2IRegisteredParam<int> AudioSeed;
    public static T2IRegisteredParam<int> InferStep;
    public static T2IRegisteredParam<double> ACEGuidanceScale;
    public static T2IRegisteredParam<string> SchedulerType;
    public static T2IRegisteredParam<string> CFGType;

    // ===== Music — MusicGen (flag: musicgen_music_params) =====
    public static T2IRegisteredParam<AudioFile> MelodyAudio;

    // ===== Clone shared (flag: audiolab_clone) =====
    public static T2IRegisteredParam<AudioFile> SourceAudio;
    public static T2IRegisteredParam<AudioFile> TargetVoice;

    // ===== Clone — RVC (flag: rvc_clone_params) =====
    public static T2IRegisteredParam<int> PitchShift;
    public static T2IRegisteredParam<string> F0Method;
    public static T2IRegisteredParam<double> IndexRate;
    public static T2IRegisteredParam<double> RMSMixRate;
    public static T2IRegisteredParam<double> Protect;

    // ===== Clone — GPT-SoVITS (flag: gptsovits_clone_params) =====
    public static T2IRegisteredParam<string> ClonePromptText;
    public static T2IRegisteredParam<string> CloneLanguage;

    // ===== FX shared (flag: audiolab_fx) =====
    public static T2IRegisteredParam<AudioFile> FXInput;

    // ===== FX — Demucs (flag: demucs_fx_params) =====
    public static T2IRegisteredParam<double> Overlap;
    public static T2IRegisteredParam<int> Shifts;

    // ===== FX — Resemble Enhance (flag: resemble_enhance_fx_params) =====
    public static T2IRegisteredParam<int> EnhanceNFE;
    public static T2IRegisteredParam<string> EnhanceSolver;
    public static T2IRegisteredParam<double> EnhanceLambda;
    public static T2IRegisteredParam<double> EnhanceTau;

    // ===== SFX shared (flag: audiolab_sfx) =====
    public static T2IRegisteredParam<double> SFXDuration;

    /// <summary>Registers all AudioLab parameters. Called from AudioLab.OnInit().</summary>
    public static void RegisterAll()
    {
        // ========================== Groups ==========================
        TTSGroup = new("TTS", Open: true, OrderPriority: -28, Toggles: false,
            Description: "Text-to-speech parameters. Enter text in the Prompt box above.");
        VoiceRefGroup = new("Voice Reference", Open: true, OrderPriority: -27, Toggles: false,
            Description: "Reference audio for voice cloning in TTS. Upload a clean ~10 second recording to clone.");
        STTGroup = new("STT", Open: true, OrderPriority: -26, Toggles: false,
            Description: "Speech-to-text parameters. Upload audio to transcribe.");
        MusicGroup = new("Music Generation", Open: true, OrderPriority: -25, Toggles: false,
            Description: "Music generation parameters. Describe the music in the Prompt box above.");
        CloneGroup = new("Voice Clone", Open: true, OrderPriority: -24, Toggles: false,
            Description: "Voice cloning parameters. Provide source and target audio.");
        FXGroup = new("Audio FX", Open: true, OrderPriority: -23, Toggles: false,
            Description: "Audio effects parameters. Upload audio to process.");
        SFXGroup = new("Sound FX", Open: true, OrderPriority: -22, Toggles: false,
            Description: "Sound effects generation. Describe the sound in the Prompt box above.");

        // ========================== TTS Shared ==========================
        Volume = T2IParamTypes.Register<double>(new("Volume",
            "Output volume multiplier.\n1.0 = full volume, 0.5 = half volume.",
            "0.8",
            Min: 0.1, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "audiolab_tts"));

        StreamAudio = T2IParamTypes.Register<bool>(new("Stream Audio",
            "Stream audio sentence-by-sentence as it generates.\nEach sentence plays immediately while the next generates.\nThe complete audio is saved as one file when done.",
            "false", IgnoreIf: "false",
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "audiolab_tts"));

        // ========================== TTS Shared Sampling ==========================
        Temperature = T2IParamTypes.Register<double>(new("Temperature",
            "Sampling temperature.\nHigher = more varied/creative speech. Lower = more consistent.",
            "0.8",
            Min: 0.1, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "tts_sampling"));

        TopP = T2IParamTypes.Register<double>(new("Top P",
            "Nucleus sampling threshold.\n1.0 = no filtering. Lower values restrict to higher probability tokens.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "tts_sampling"));

        RepetitionPenalty = T2IParamTypes.Register<double>(new("Repetition Penalty",
            "Penalizes repeated tokens.\nHigher values reduce stuttering and repetitive speech patterns.",
            "1.2",
            Min: 1.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: TTSGroup, FeatureFlag: "tts_sampling"));

        TopK = T2IParamTypes.Register<int>(new("Top K",
            "Top-K token sampling.\nLimits sampling to the K most likely tokens. 0 = disabled.",
            "50",
            Min: 0, Max: 1000, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "tts_sampling"));

        MinP = T2IParamTypes.Register<double>(new("Min P",
            "Minimum probability threshold.\nTokens below this probability are excluded from sampling.",
            "0.05",
            Min: 0.0, Max: 1.0, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "tts_sampling"));

        // ========================== Voice Reference Shared ==========================
        ReferenceAudio = T2IParamTypes.Register<AudioFile>(new("Reference Audio",
            "Reference audio clip for voice cloning.\nOptional — uses default voice when not provided.",
            null,
            OrderPriority: -10, Group: VoiceRefGroup, FeatureFlag: "tts_voice_ref"));

        ReferenceText = T2IParamTypes.Register<string>(new("Reference Text",
            "Transcript of the reference audio.\nOptional but improves quality when provided.",
            "",
            OrderPriority: -9, Group: VoiceRefGroup, FeatureFlag: "tts_voice_ref"));

        // ========================== TTS — Bark ==========================
        BarkVoice = T2IParamTypes.Register<string>(new("Bark Voice",
            "Voice preset for Bark TTS.\nSelect a speaker voice. 'Random' generates a random voice.",
            "v2/en_speaker_6",
            GetValues: _ => [
                "v2/en_speaker_6///English Speaker 6", "v2/en_speaker_0///English Speaker 0",
                "v2/en_speaker_1///English Speaker 1", "v2/en_speaker_2///English Speaker 2",
                "v2/en_speaker_3///English Speaker 3", "v2/en_speaker_4///English Speaker 4",
                "v2/en_speaker_5///English Speaker 5", "v2/en_speaker_7///English Speaker 7",
                "v2/en_speaker_8///English Speaker 8", "v2/en_speaker_9///English Speaker 9",
                "v2/zh_speaker_0///Chinese Speaker 0", "v2/zh_speaker_1///Chinese Speaker 1",
                "v2/de_speaker_0///German Speaker 0", "v2/fr_speaker_0///French Speaker 0",
                "v2/ja_speaker_0///Japanese Speaker 0", "v2/ko_speaker_0///Korean Speaker 0",
                "random///Random"
            ],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "bark_tts_params"));

        TextTemp = T2IParamTypes.Register<double>(new("Text Temperature",
            "Controls randomness of text token generation.\nHigher = more varied speech patterns.",
            "0.7",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "bark_tts_params"));

        WaveformTemp = T2IParamTypes.Register<double>(new("Waveform Temperature",
            "Controls randomness of audio waveform generation.\nHigher = more varied audio quality.",
            "0.7",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "bark_tts_params"));

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
        DiffusionSteps = T2IParamTypes.Register<int>(new("Diffusion Steps",
            "Number of DDPM denoising steps.\nMore steps = higher quality but slower. 20 is a good balance.",
            "20",
            Min: 5, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params"));

        // ========================== TTS — Dia ==========================
        CFGFilterTopK = T2IParamTypes.Register<int>(new("CFG Filter Top K",
            "Top-K filtering for classifier-free guidance.\nLimits CFG to top K tokens. Higher = less filtering.",
            "35",
            Min: 0, Max: 500, Step: 5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "dia_tts_params"));

        // ========================== TTS — F5-TTS ==========================
        NFEStep = T2IParamTypes.Register<int>(new("NFE Steps",
            "Number of function evaluation steps for flow matching.\nMore steps = higher quality but slower.",
            "32",
            Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        F5Speed = T2IParamTypes.Register<double>(new("Speed",
            "Speech speed multiplier.\n1.0 = normal, 0.5 = half speed, 2.0 = double speed.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        // ========================== TTS — Zonos ==========================
        ZonosLanguage = T2IParamTypes.Register<string>(new("Zonos Language",
            "Language for Zonos TTS synthesis.",
            "en-us",
            GetValues: _ => [
                "en-us///English (US)", "en-gb///English (UK)",
                "es///Spanish", "fr///French", "de///German",
                "it///Italian", "pt///Portuguese", "ja///Japanese",
                "zh///Chinese", "ko///Korean"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        ZonosEmotion = T2IParamTypes.Register<string>(new("Emotion",
            "Emotional tone for Zonos speech synthesis.\nControls the emotional expression of the generated voice.",
            "neutral",
            GetValues: _ => [
                "neutral///Neutral", "happy///Happy", "sad///Sad",
                "angry///Angry", "fearful///Fearful", "surprised///Surprised",
                "disgusted///Disgusted"
            ],
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        SpeakingRate = T2IParamTypes.Register<double>(new("Speaking Rate",
            "Speaking rate for Zonos TTS.\nHigher values produce faster speech.",
            "15.0",
            Min: 5.0, Max: 30.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

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

        // Music — AudioCraft shared
        GuidanceScale = T2IParamTypes.Register<double>(new("Guidance Scale",
            "Classifier-free guidance for music/sound generation.\nHigher values increase prompt adherence.",
            "3.0",
            Min: 0.0, Max: 10.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: MusicGroup, FeatureFlag: "audiocraft_sampling"));

        // Music — ACE-Step
        Lyrics = T2IParamTypes.Register<string>(new("Lyrics",
            "Song lyrics for ACE-Step generation.\nUse [Instrumental] for instrumental-only tracks.",
            "[Instrumental]",
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        AudioSeed = T2IParamTypes.Register<int>(new("Audio Seed",
            "Random seed for reproducible generation.\n-1 = random seed each time.",
            "-1",
            Min: -1, Max: 999999, Step: 1,
            OrderPriority: -4, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        InferStep = T2IParamTypes.Register<int>(new("Infer Steps",
            "Number of diffusion inference steps.\nMore steps = higher quality but slower.",
            "60",
            Min: 1, Max: 200, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        ACEGuidanceScale = T2IParamTypes.Register<double>(new("ACE Guidance",
            "Classifier-free guidance strength for ACE-Step.\nHigher values increase prompt adherence.",
            "15.0",
            Min: 1.0, Max: 30.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        SchedulerType = T2IParamTypes.Register<string>(new("ACE Scheduler",
            "Diffusion scheduler type for ACE-Step generation.",
            "euler",
            GetValues: _ => [
                "euler///Euler (Default)", "heun///Heun", "pingpong///Ping-Pong"
            ],
            OrderPriority: -1, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        CFGType = T2IParamTypes.Register<string>(new("CFG Type",
            "Classifier-free guidance algorithm for ACE-Step.\nAPG = Adaptive Projected Guidance.",
            "apg",
            GetValues: _ => [
                "apg///APG (Default)", "cfg///Standard CFG", "cfg_star///CFG Star"
            ],
            OrderPriority: 0, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

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

        F0Method = T2IParamTypes.Register<string>(new("F0 Method",
            "Pitch extraction algorithm for RVC.\nRMVPE = best quality. PM = fastest.",
            "rmvpe",
            GetValues: _ => [
                "rmvpe///RMVPE (Best Quality)", "pm///PM (Fastest)",
                "harvest///Harvest", "crepe///CREPE (GPU)"
            ],
            OrderPriority: -4, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

        IndexRate = T2IParamTypes.Register<double>(new("Index Rate",
            "Influence of the RVC feature index.\nHigher values strengthen voice characteristics from the model.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

        RMSMixRate = T2IParamTypes.Register<double>(new("RMS Mix Rate",
            "Volume envelope mixing ratio.\n1.0 = use original input volume. 0.0 = use model output volume.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

        Protect = T2IParamTypes.Register<double>(new("Protect",
            "Protects voiceless consonants and breath sounds.\nHigher values preserve more consonant detail. 0.5 = max protection.",
            "0.33",
            Min: 0.0, Max: 0.5, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -1, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

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

        // FX — Demucs
        Overlap = T2IParamTypes.Register<double>(new("Overlap",
            "Overlap between processing chunks.\nHigher values improve quality at boundaries but take longer.",
            "0.25",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: FXGroup, FeatureFlag: "demucs_fx_params"));

        Shifts = T2IParamTypes.Register<int>(new("Shifts",
            "Random shifts for equivariant stabilization.\nMore shifts improve quality but take longer. 0 = disabled.",
            "1",
            Min: 0, Max: 10, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: FXGroup, FeatureFlag: "demucs_fx_params"));

        // FX — Resemble Enhance
        EnhanceNFE = T2IParamTypes.Register<int>(new("Enhancement Steps",
            "Number of function evaluations for audio enhancement.\nMore steps = higher quality but slower.",
            "64",
            Min: 1, Max: 128, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: FXGroup, FeatureFlag: "resemble_enhance_fx_params"));

        EnhanceSolver = T2IParamTypes.Register<string>(new("Solver",
            "ODE solver method for enhancement.\nMidpoint is recommended for best quality/speed balance.",
            "midpoint",
            GetValues: _ => [
                "midpoint///Midpoint (Recommended)", "euler///Euler", "rk4///RK4"
            ],
            OrderPriority: -4, Group: FXGroup, FeatureFlag: "resemble_enhance_fx_params"));

        EnhanceLambda = T2IParamTypes.Register<double>(new("Lambda",
            "Prior temperature.\nControls balance between denoising and super-resolution.",
            "0.1",
            Min: 0.0, Max: 1.0, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: FXGroup, FeatureFlag: "resemble_enhance_fx_params"));

        EnhanceTau = T2IParamTypes.Register<double>(new("Tau",
            "CFM posterior temperature.\nControls the level of enhancement applied.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: FXGroup, FeatureFlag: "resemble_enhance_fx_params"));

        // ========================== Sound FX ==========================
        SFXDuration = T2IParamTypes.Register<double>(new("SFX Duration",
            "Duration of generated sound effect in seconds.",
            "10",
            Min: 1, Max: 60, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: SFXGroup, FeatureFlag: "audiolab_sfx"));
    }
}
