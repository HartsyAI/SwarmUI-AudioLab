using SwarmUI.Media;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.AudioLab;

/// <summary>Registers AudioLab T2I parameters with feature flags.
/// Category-level params use category flags (audiolab_tts, audiolab_stt, etc.).
/// Provider-specific params use provider flags (kokoro_tts_params, etc.) for visibility.</summary>
public static class AudioLabParams
{
    #region Groups

    /// <summary>Text-to-speech parameter group.</summary>
    public static T2IParamGroup TTSGroup;
    /// <summary>Speech-to-text parameter group.</summary>
    public static T2IParamGroup STTGroup;
    /// <summary>Music generation parameter group.</summary>
    public static T2IParamGroup MusicGroup;
    /// <summary>Voice reference parameter group for TTS voice cloning.</summary>
    public static T2IParamGroup VoiceRefGroup;
    /// <summary>Voice cloning parameter group.</summary>
    public static T2IParamGroup CloneGroup;
    /// <summary>Audio effects parameter group.</summary>
    public static T2IParamGroup FXGroup;
    /// <summary>Sound effects parameter group.</summary>
    public static T2IParamGroup SFXGroup;

    #endregion

    #region TTS Shared (flag: audiolab_tts)

    /// <summary>Output volume multiplier. Feature flag: <c>audiolab_tts</c>.</summary>
    public static T2IRegisteredParam<double> Volume;
    /// <summary>Text chunking strategy for streaming TTS. Feature flag: <c>audiolab_tts</c>.</summary>
    public static T2IRegisteredParam<string> StreamChunkSize;

    #endregion

    #region TTS Shared Sampling (flag: tts_sampling)

    /// <summary>Sampling temperature for TTS generation. Feature flag: <c>tts_sampling</c>.</summary>
    public static T2IRegisteredParam<double> Temperature;
    /// <summary>Nucleus sampling threshold for TTS. Feature flag: <c>tts_sampling</c>.</summary>
    public static T2IRegisteredParam<double> TopP;
    /// <summary>Repetition penalty for TTS token sampling. Feature flag: <c>tts_sampling</c>.</summary>
    public static T2IRegisteredParam<double> RepetitionPenalty;
    /// <summary>Top-K token sampling limit for TTS. Feature flag: <c>tts_sampling</c>.</summary>
    public static T2IRegisteredParam<int> TopK;
    /// <summary>Minimum probability threshold for TTS sampling. Feature flag: <c>tts_sampling</c>.</summary>
    public static T2IRegisteredParam<double> MinP;

    #endregion

    #region Voice Reference Shared (flag: tts_voice_ref)

    /// <summary>Reference audio clip for voice cloning. Feature flag: <c>tts_voice_ref</c>.</summary>
    public static T2IRegisteredParam<AudioFile> ReferenceAudio;
    /// <summary>Transcript of the reference audio. Feature flag: <c>tts_voice_ref</c>.</summary>
    public static T2IRegisteredParam<string> ReferenceText;

    #endregion

    #region TTS — Bark (flag: bark_tts_params)

    /// <summary>Voice preset for Bark TTS. Feature flag: <c>bark_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> BarkVoice;
    /// <summary>Text token generation temperature for Bark. Feature flag: <c>bark_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> TextTemp;
    /// <summary>Audio waveform generation temperature for Bark. Feature flag: <c>bark_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> WaveformTemp;

    #endregion

    #region TTS — Chatterbox (flag: chatterbox_tts_params)

    /// <summary>Voice expressiveness level for Chatterbox. Feature flag: <c>chatterbox_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> Exaggeration;
    /// <summary>Classifier-free guidance weight for Chatterbox. Feature flag: <c>chatterbox_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> CFGWeight;

    #endregion

    #region TTS — Kokoro (flag: kokoro_tts_params)

    /// <summary>Voice preset for Kokoro TTS. Feature flag: <c>kokoro_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> KokoroVoice;
    /// <summary>Speech speed multiplier for Kokoro. Feature flag: <c>kokoro_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> KokoroSpeed;

    #endregion

    #region TTS — Piper (flag: piper_tts_params)

    /// <summary>Voice model for Piper TTS. Feature flag: <c>piper_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PiperVoice;
    /// <summary>Speech speed multiplier for Piper. Feature flag: <c>piper_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> PiperSpeed;

    #endregion

    #region TTS — Orpheus (flag: orpheus_tts_params)

    /// <summary>Voice preset for Orpheus TTS. Feature flag: <c>orpheus_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> OrpheusVoice;

    #endregion

    #region TTS — CSM (flag: csm_tts_params)

    /// <summary>Speaker ID for CSM multi-speaker TTS. Feature flag: <c>csm_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> Speaker;

    #endregion

    #region TTS — VibeVoice (flag: vibevoice_tts_params)

    /// <summary>DDPM denoising step count for VibeVoice. Feature flag: <c>vibevoice_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> DiffusionSteps;
    public static T2IRegisteredParam<double> VibeVoiceCFG;

    #endregion

    #region TTS — Dia (flag: dia_tts_params)

    /// <summary>Top-K filtering for Dia CFG guidance. Feature flag: <c>dia_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> CFGFilterTopK;

    #endregion

    #region TTS — F5-TTS (flag: f5_tts_params)

    /// <summary>Flow-matching function evaluation step count for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> NFEStep;
    /// <summary>Speech speed multiplier for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> F5Speed;
    /// <summary>Classifier-free guidance scale for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> F5CFG;

    #endregion

    #region TTS — Zonos (flag: zonos_tts_params)

    /// <summary>Language selection for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> ZonosLanguage;
    /// <summary>Emotional tone for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> ZonosEmotion;
    /// <summary>Speaking rate for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> SpeakingRate;

    #endregion

    #region TTS — Qwen3-TTS (flag: qwen3tts_tts_params)

    /// <summary>Language for Qwen3-TTS synthesis. Feature flag: <c>qwen3tts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Language;
    /// <summary>Speaker voice for Qwen3-TTS CustomVoice models. Feature flag: <c>qwen3tts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Speaker;
    /// <summary>Natural language instruction for Qwen3-TTS voice style/emotion. Feature flag: <c>qwen3tts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Instruct;

    #endregion

    #region TTS — Fish Speech (flag: fishspeech_tts_params)

    /// <summary>Maximum new tokens to generate for Fish Speech. Feature flag: <c>fishspeech_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> FishSpeechMaxTokens;
    /// <summary>Text chunk size in bytes for Fish Speech batched generation. Feature flag: <c>fishspeech_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> FishSpeechChunkLength;
    /// <summary>Text normalization toggle for Fish Speech. Feature flag: <c>fishspeech_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> FishSpeechNormalize;

    #endregion

    #region TTS — CosyVoice (flag: cosyvoice_tts_params)

    /// <summary>Built-in voice preset for CosyVoice TTS. Feature flag: <c>cosyvoice_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> CosyVoiceVoice;

    #endregion

    #region STT Shared (flag: audiolab_stt)

    /// <summary>Audio file input for speech-to-text. Feature flag: <c>audiolab_stt</c>.</summary>
    public static T2IRegisteredParam<AudioFile> AudioInput;
    /// <summary>Language hint for STT transcription. Feature flag: <c>audiolab_stt</c>.</summary>
    public static T2IRegisteredParam<string> Language;

    #endregion

    #region STT — Whisper (flag: whisper_stt_params)

    /// <summary>Whisper task type (transcribe or translate). Feature flag: <c>whisper_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> WhisperTask;

    #endregion

    #region Music Shared (flag: audiolab_music)

    /// <summary>Duration of generated music in seconds. Feature flag: <c>audiolab_music</c>.</summary>
    public static T2IRegisteredParam<double> Duration;

    #endregion

    #region Music — AudioCraft Shared (flag: audiocraft_sampling)

    /// <summary>Classifier-free guidance scale for AudioCraft. Feature flag: <c>audiocraft_sampling</c>.</summary>
    public static T2IRegisteredParam<double> GuidanceScale;
    /// <summary>Sampling temperature for AudioCraft generation. Feature flag: <c>audiocraft_sampling</c>.</summary>
    public static T2IRegisteredParam<double> AudioCraftTemperature;
    /// <summary>Top-K token sampling for AudioCraft. Feature flag: <c>audiocraft_sampling</c>.</summary>
    public static T2IRegisteredParam<int> AudioCraftTopK;
    /// <summary>Nucleus sampling for AudioCraft. Feature flag: <c>audiocraft_sampling</c>.</summary>
    public static T2IRegisteredParam<double> AudioCraftTopP;

    #endregion

    #region Music — ACE-Step Core (flag: acestep_music_params)

    /// <summary>Song lyrics for ACE-Step generation. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> Lyrics;
    /// <summary>Random seed for reproducible ACE-Step generation. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<int> AudioSeed;
    /// <summary>Diffusion inference step count for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<int> InferStep;
    /// <summary>Classifier-free guidance strength for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> ACEGuidanceScale;
    /// <summary>Instrumental-only toggle for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> Instrumental;
    /// <summary>Beats per minute for ACE-Step music. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<int> BPM;
    /// <summary>Musical key and scale for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> KeyScale;
    /// <summary>Musical time signature for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> TimeSignature;
    /// <summary>Vocal language for ACE-Step music. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> VocalLanguage;
    /// <summary>Noise schedule shift factor for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> ACEShift;
    /// <summary>ODE solver method for ACE-Step diffusion. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> InferMethod;
    /// <summary>Adaptive Diffusion Guidance toggle for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> UseADG;
    /// <summary>CFG application interval start for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> CFGIntervalStart;
    /// <summary>CFG application interval end for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> CFGIntervalEnd;
    /// <summary>Output audio normalization toggle for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<string> EnableNormalization;
    /// <summary>Target loudness in dBFS for ACE-Step normalization. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> NormalizationDB;

    #endregion

    #region Music — ACE-Step LM Planner (flag: acestep_lm_params)

    /// <summary>Language Model planner selection for ACE-Step. Feature flag: <c>acestep_lm_params</c>.
    /// <para>TODO: Integrate with SwarmUI <c>AbstractLLMBackend</c> when LLMAPI.cs is complete.</para></summary>
    public static T2IRegisteredParam<string> ACELMModel;
    /// <summary>Chain-of-thought reasoning toggle for ACE-Step LM. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<string> Thinking;
    /// <summary>Sampling temperature for ACE-Step LM planner. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<double> LMTemperature;
    /// <summary>Classifier-free guidance scale for ACE-Step LM. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<double> LMCFGScale;
    /// <summary>Top-K sampling for ACE-Step LM planner. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<int> LMTopK;
    /// <summary>Nucleus sampling threshold for ACE-Step LM. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<double> LMTopP;
    /// <summary>Negative prompt for ACE-Step LM planner. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<string> LMNegativePrompt;
    /// <summary>Meta tag inclusion in ACE-Step chain-of-thought. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<string> UseCotMetas;
    /// <summary>Music caption inclusion in ACE-Step chain-of-thought. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<string> UseCotCaption;
    /// <summary>Language detection inclusion in ACE-Step chain-of-thought. Feature flag: <c>acestep_lm_params</c>.</summary>
    public static T2IRegisteredParam<string> UseCotLanguage;

    #endregion

    #region Music — ACE-Step Tasks (flag: acestep_task_params)

    /// <summary>ACE-Step generation task type. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<string> ACETaskType;
    /// <summary>Source audio for ACE-Step cover/repaint/extract/lego/complete tasks. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<AudioFile> ACESourceAudio;
    /// <summary>Style/timbre reference audio for ACE-Step. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<AudioFile> ACEReferenceAudio;
    /// <summary>Repaint start time in seconds for ACE-Step. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<double> RepaintStart;
    /// <summary>Repaint end time in seconds for ACE-Step. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<double> RepaintEnd;
    /// <summary>Style transfer strength for ACE-Step cover task. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<double> CoverStrength;
    /// <summary>Noise injection strength for ACE-Step cover task. Feature flag: <c>acestep_task_params</c>.</summary>
    public static T2IRegisteredParam<double> CoverNoiseStrength;

    #endregion

    #region Music — MusicGen (flag: musicgen_music_params)

    /// <summary>Reference melody audio for MusicGen melody conditioning. Feature flag: <c>musicgen_music_params</c>.</summary>
    public static T2IRegisteredParam<AudioFile> MelodyAudio;

    #endregion

    #region Clone Shared (flag: audiolab_clone)

    /// <summary>Source audio for voice cloning or conversion. Feature flag: <c>audiolab_clone</c>.</summary>
    public static T2IRegisteredParam<AudioFile> SourceAudio;
    /// <summary>Target voice reference for tone conversion. Feature flag: <c>audiolab_clone</c>.</summary>
    public static T2IRegisteredParam<AudioFile> TargetVoice;

    #endregion

    #region Clone — RVC (flag: rvc_clone_params)

    /// <summary>Semitone pitch shift for RVC voice conversion. Feature flag: <c>rvc_clone_params</c>.</summary>
    public static T2IRegisteredParam<int> PitchShift;
    /// <summary>Pitch extraction algorithm for RVC. Feature flag: <c>rvc_clone_params</c>.</summary>
    public static T2IRegisteredParam<string> F0Method;
    /// <summary>RVC feature index influence rate. Feature flag: <c>rvc_clone_params</c>.</summary>
    public static T2IRegisteredParam<double> IndexRate;
    /// <summary>Volume envelope mixing ratio for RVC. Feature flag: <c>rvc_clone_params</c>.</summary>
    public static T2IRegisteredParam<double> RMSMixRate;
    /// <summary>Voiceless consonant protection for RVC. Feature flag: <c>rvc_clone_params</c>.</summary>
    public static T2IRegisteredParam<double> Protect;

    #endregion

    #region Clone — GPT-SoVITS (flag: gptsovits_clone_params)

    /// <summary>Reference audio transcript for GPT-SoVITS. Feature flag: <c>gptsovits_clone_params</c>.</summary>
    public static T2IRegisteredParam<string> ClonePromptText;
    /// <summary>Language selection for GPT-SoVITS cloning. Feature flag: <c>gptsovits_clone_params</c>.</summary>
    public static T2IRegisteredParam<string> CloneLanguage;

    #endregion

    #region FX Shared (flag: audiolab_fx)

    /// <summary>Audio file input for effects processing. Feature flag: <c>audiolab_fx</c>.</summary>
    public static T2IRegisteredParam<AudioFile> FXInput;

    #endregion

    #region FX — Demucs (flag: demucs_fx_params)

    /// <summary>Processing chunk overlap for Demucs separation. Feature flag: <c>demucs_fx_params</c>.</summary>
    public static T2IRegisteredParam<double> Overlap;
    /// <summary>Random shift count for Demucs equivariant stabilization. Feature flag: <c>demucs_fx_params</c>.</summary>
    public static T2IRegisteredParam<int> Shifts;

    #endregion

    #region FX — Resemble Enhance (flag: resemble_enhance_fx_params)

    /// <summary>Function evaluation step count for Resemble Enhance. Feature flag: <c>resemble_enhance_fx_params</c>.</summary>
    public static T2IRegisteredParam<int> EnhanceNFE;
    /// <summary>ODE solver method for Resemble Enhance. Feature flag: <c>resemble_enhance_fx_params</c>.</summary>
    public static T2IRegisteredParam<string> EnhanceSolver;
    /// <summary>Prior temperature for Resemble Enhance. Feature flag: <c>resemble_enhance_fx_params</c>.</summary>
    public static T2IRegisteredParam<double> EnhanceLambda;
    /// <summary>CFM posterior temperature for Resemble Enhance. Feature flag: <c>resemble_enhance_fx_params</c>.</summary>
    public static T2IRegisteredParam<double> EnhanceTau;

    #endregion

    #region SFX Shared (flag: audiolab_sfx)

    /// <summary>Duration of generated sound effect in seconds. Feature flag: <c>audiolab_sfx</c>.</summary>
    public static T2IRegisteredParam<double> SFXDuration;

    #endregion

    /// <summary>Registers all AudioLab parameters. Called from <see cref="AudioLab.OnInit"/>.</summary>
    public static void RegisterAll()
    {
        #region Groups
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

        #endregion

        #region TTS Shared
        Volume = T2IParamTypes.Register<double>(new("Volume",
            "Output volume multiplier.\n1.0 = full volume, 0.5 = half volume.",
            "0.8",
            Min: 0.1, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "audiolab_tts"));

        StreamChunkSize = T2IParamTypes.Register<string>(new("Stream Chunk Size",
            "How to split text for streaming audio generation.\nSmaller chunks = faster first audio. Larger chunks = better quality per chunk.\nPer Sentence is recommended for most models.\nEach chunk plays immediately while the next generates.",
            "off", IgnoreIf: "off",
            GetValues: _ => [
                "off///Off (Full Text)",
                "word///Per Word",
                "phrase///Short Phrases (~5 words)",
                "sentence///Per Sentence",
                "paragraph///Per Paragraph"
            ],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "audiolab_tts"));

        #endregion

        #region TTS Shared Sampling
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

        #endregion

        #region Voice Reference Shared
        ReferenceAudio = T2IParamTypes.Register<AudioFile>(new("Reference Audio",
            "Reference audio clip for voice cloning.\nOptional — uses default voice when not provided.",
            null,
            OrderPriority: -10, Group: VoiceRefGroup, FeatureFlag: "tts_voice_ref"));

        ReferenceText = T2IParamTypes.Register<string>(new("Reference Text",
            "Transcript of the reference audio.\nOptional but improves quality when provided.",
            "",
            OrderPriority: -9, Group: VoiceRefGroup, FeatureFlag: "tts_voice_ref"));

        #endregion

        #region TTS — Bark
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

        #endregion

        #region TTS — Chatterbox
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

        #endregion

        #region TTS — Kokoro
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

        #endregion

        #region TTS — Piper
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

        #endregion

        #region TTS — Orpheus
        OrpheusVoice = T2IParamTypes.Register<string>(new("Orpheus Voice",
            "Voice preset for Orpheus TTS.\nSupports emotion tags: <laugh>, <chuckle>, <sigh>, <cough>, <sniffle>, <groan>, <yawn>, <gasp>",
            "tara",
            GetValues: _ => [
                "tara///Tara (Female)", "leah///Leah (Female)", "jess///Jess (Female)",
                "leo///Leo (Male)", "dan///Dan (Male)", "mia///Mia (Female)",
                "zac///Zac (Male)", "zoe///Zoe (Female)"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "orpheus_tts_params"));

        #endregion

        #region TTS — CSM
        Speaker = T2IParamTypes.Register<string>(new("Speaker",
            "Speaker ID for multi-speaker conversation.\n0 = primary speaker.",
            "0",
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "csm_tts_params"));

        #endregion

        #region TTS — VibeVoice
        DiffusionSteps = T2IParamTypes.Register<int>(new("Diffusion Steps",
            "Number of DDPM denoising steps.\nMore steps = higher quality but slower. 10 is recommended.",
            "10",
            Min: 5, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params"));

        VibeVoiceCFG = T2IParamTypes.Register<double>(new("CFG Scale",
            "Classifier-free guidance scale for speech diffusion.\n1.3 is recommended for standard models, 1.5 for streaming.",
            "1.3",
            Min: 0.0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params"));

        #endregion

        #region TTS — Dia
        CFGFilterTopK = T2IParamTypes.Register<int>(new("CFG Filter Top K",
            "Top-K filtering for classifier-free guidance.\nLimits CFG to top K tokens. Higher = less filtering.",
            "35",
            Min: 0, Max: 500, Step: 5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "dia_tts_params"));

        #endregion

        #region TTS — F5-TTS
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

        F5CFG = T2IParamTypes.Register<double>(new("CFG Scale",
            "Classifier-free guidance for flow matching.\n2.0 is recommended. Higher = stronger prompt adherence.",
            "2.0",
            Min: 0.0, Max: 10.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        #endregion

        #region TTS — Zonos
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

        #endregion

        #region TTS — Fish Speech
        FishSpeechMaxTokens = T2IParamTypes.Register<int>(new("FishSpeech Max Tokens",
            "Maximum new tokens to generate.\nHigher values allow longer audio output but take more time.",
            "1024",
            Min: 256, Max: 4096, Step: 64, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params"));

        FishSpeechChunkLength = T2IParamTypes.Register<int>(new("FishSpeech Chunk Length",
            "Text chunk size in bytes for batched generation.\nSmaller = faster first audio, larger = better coherence.",
            "200",
            Min: 100, Max: 300, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params"));

        FishSpeechNormalize = T2IParamTypes.Register<string>(new("FishSpeech Normalize",
            "Normalize text before synthesis.\nImproves handling of numbers, abbreviations, and special characters.",
            "true",
            GetValues: _ => ["true///Yes (Recommended)", "false///No"],
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params"));

        #endregion

        #region TTS — Qwen3-TTS
        Qwen3Language = T2IParamTypes.Register<string>(new("Qwen3 Language",
            "Language for Qwen3-TTS synthesis.\n'Auto' lets the model detect automatically.",
            "Auto",
            GetValues: _ => [
                "Auto///Auto-detect",
                "Chinese///Chinese", "English///English",
                "Japanese///Japanese", "Korean///Korean",
                "German///German", "French///French",
                "Russian///Russian", "Portuguese///Portuguese",
                "Spanish///Spanish", "Italian///Italian"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "qwen3tts_tts_params"));

        Qwen3Speaker = T2IParamTypes.Register<string>(new("Qwen3 Speaker",
            "Built-in speaker for CustomVoice models.\nIgnored for Base (voice clone) and VoiceDesign models.",
            "Ryan",
            GetValues: _ => [
                "Ryan///Ryan (English Male)",
                "Aiden///Aiden (English Male)",
                "Vivian///Vivian (Chinese Female)",
                "Serena///Serena (Chinese Female)",
                "Uncle_Fu///Uncle Fu (Chinese Male)",
                "Dylan///Dylan (Chinese Male, Beijing)",
                "Eric///Eric (Chinese Male, Sichuan)",
                "Ono_Anna///Ono Anna (Japanese Female)",
                "Sohee///Sohee (Korean Female)"
            ],
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "qwen3tts_tts_params"));

        Qwen3Instruct = T2IParamTypes.Register<string>(new("Qwen3 Instruct",
            "Natural language instruction for voice control.\nCustomVoice: describe emotion/style (e.g. 'Speak with excitement').\nVoiceDesign: describe the voice (e.g. 'A deep male voice with a British accent').\nIgnored for Base models.",
            "",
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "qwen3tts_tts_params"));

        #endregion

        #region TTS — CosyVoice
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

        #endregion

        #region STT Shared
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

        #endregion

        #region STT — Whisper
        WhisperTask = T2IParamTypes.Register<string>(new("Whisper Task",
            "Whisper task type.\nTranscribe = speech-to-text in original language.\nTranslate = speech-to-English translation.",
            "transcribe",
            GetValues: _ => ["transcribe///Transcribe", "translate///Translate to English"],
            OrderPriority: -8, Group: STTGroup, FeatureFlag: "whisper_stt_params"));

        #endregion

        #region Music Shared
        Duration = T2IParamTypes.Register<double>(new("Duration",
            "Duration of generated music in seconds.\nLonger durations need more time and VRAM.",
            "30",
            Min: 1, Max: 300, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: MusicGroup, FeatureFlag: "audiolab_music"));

        #endregion

        #region Music — AudioCraft Shared
        GuidanceScale = T2IParamTypes.Register<double>(new("Guidance Scale",
            "Classifier-free guidance for music/sound generation.\nHigher values increase prompt adherence.",
            "3.0",
            Min: 0.0, Max: 10.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: MusicGroup, FeatureFlag: "audiocraft_sampling"));

        AudioCraftTemperature = T2IParamTypes.Register<double>(new("AudioCraft Temperature",
            "Sampling temperature for audio generation.\nHigher = more varied/creative. Lower = more predictable.",
            "1.0",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: MusicGroup, FeatureFlag: "audiocraft_sampling"));

        AudioCraftTopK = T2IParamTypes.Register<int>(new("AudioCraft Top K",
            "Top-K token sampling for audio generation.\nLimits sampling to the K most likely tokens. 250 is the AudioCraft default.",
            "250",
            Min: 0, Max: 1000, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: MusicGroup, FeatureFlag: "audiocraft_sampling"));

        AudioCraftTopP = T2IParamTypes.Register<double>(new("AudioCraft Top P",
            "Nucleus sampling for audio generation.\n0.0 = disabled (use Top K instead). Values > 0 enable nucleus sampling.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "audiocraft_sampling"));

        #endregion

        #region Music — ACE-Step Core
        Lyrics = T2IParamTypes.Register<string>(new("Lyrics",
            "Song lyrics for ACE-Step generation.\nUse [Instrumental] for instrumental-only tracks.\nSupports section tags like [Verse], [Chorus], [Bridge].",
            "[Instrumental]",
            OrderPriority: -9, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        AudioSeed = T2IParamTypes.Register<int>(new("Audio Seed",
            "Random seed for reproducible generation.\n-1 = random seed each time.",
            "-1",
            Min: -1, Max: 999999, Step: 1,
            OrderPriority: -8, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        InferStep = T2IParamTypes.Register<int>(new("Infer Steps",
            "Number of diffusion inference steps.\nTurbo models: 8. SFT/Base models: 50.",
            "8",
            Min: 1, Max: 200, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        ACEGuidanceScale = T2IParamTypes.Register<double>(new("ACE Guidance",
            "Classifier-free guidance strength.\nOnly effective with SFT/Base models that support CFG.",
            "7.0",
            Min: 1.0, Max: 30.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        Instrumental = T2IParamTypes.Register<string>(new("Instrumental",
            "Generate instrumental-only track without vocals.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        BPM = T2IParamTypes.Register<int>(new("BPM",
            "Beats per minute for the generated music.",
            "120",
            Min: 30, Max: 300, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        KeyScale = T2IParamTypes.Register<string>(new("Key / Scale",
            "Musical key and scale.\nLeave empty for auto-detection.",
            "",
            GetValues: _ => [
                "///Auto",
                "C major///C Major", "C minor///C Minor",
                "C# major///C# Major", "C# minor///C# Minor",
                "D major///D Major", "D minor///D Minor",
                "Eb major///Eb Major", "Eb minor///Eb Minor",
                "E major///E Major", "E minor///E Minor",
                "F major///F Major", "F minor///F Minor",
                "F# major///F# Major", "F# minor///F# Minor",
                "G major///G Major", "G minor///G Minor",
                "Ab major///Ab Major", "Ab minor///Ab Minor",
                "A major///A Major", "A minor///A Minor",
                "Bb major///Bb Major", "Bb minor///Bb Minor",
                "B major///B Major", "B minor///B Minor"
            ],
            OrderPriority: -3, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        TimeSignature = T2IParamTypes.Register<string>(new("Time Signature",
            "Musical time signature (beats per measure).",
            "4",
            GetValues: _ => [
                "4///4/4 (Common Time)", "3///3/4 (Waltz)", "2///2/4 (March)", "6///6/8 (Compound)"
            ],
            OrderPriority: -2, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        VocalLanguage = T2IParamTypes.Register<string>(new("Vocal Language",
            "Language for vocal content in the generated music.",
            "en",
            GetValues: _ => [
                "en///English", "zh///Chinese", "es///Spanish", "fr///French",
                "de///German", "ja///Japanese", "ko///Korean", "pt///Portuguese",
                "ru///Russian", "it///Italian", "ar///Arabic", "tr///Turkish",
                "nl///Dutch", "pl///Polish", "sv///Swedish", "da///Danish",
                "fi///Finnish", "no///Norwegian", "id///Indonesian", "vi///Vietnamese",
                "th///Thai", "ms///Malay", "ro///Romanian", "cs///Czech",
                "el///Greek", "hu///Hungarian", "uk///Ukrainian", "bg///Bulgarian",
                "hr///Croatian", "sk///Slovak", "sl///Slovenian", "sr///Serbian",
                "lt///Lithuanian", "lv///Latvian", "et///Estonian", "mk///Macedonian",
                "sq///Albanian", "bs///Bosnian", "gl///Galician", "ka///Georgian",
                "eu///Basque", "cy///Welsh", "ga///Irish", "mt///Maltese",
                "is///Icelandic", "az///Azerbaijani", "kk///Kazakh", "uz///Uzbek",
                "tg///Tajik", "mn///Mongolian"
            ],
            OrderPriority: -1, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        ACEShift = T2IParamTypes.Register<double>(new("Shift",
            "Noise schedule shift factor.\nHigher values increase generation diversity.",
            "3.0",
            Min: 1.0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: 0, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        InferMethod = T2IParamTypes.Register<string>(new("Infer Method",
            "ODE solver method for diffusion inference.\nODE = deterministic. SDE = stochastic (more varied).",
            "ode",
            GetValues: _ => ["ode///ODE (Default)", "sde///SDE (Stochastic)"],
            OrderPriority: 1, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        UseADG = T2IParamTypes.Register<string>(new("Use ADG",
            "Enable Adaptive Diffusion Guidance.\nCan improve prompt adherence for some models.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: 2, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        CFGIntervalStart = T2IParamTypes.Register<double>(new("CFG Interval Start",
            "Start of the CFG application interval.\n0.0 = apply from beginning of denoising.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: 3, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        CFGIntervalEnd = T2IParamTypes.Register<double>(new("CFG Interval End",
            "End of the CFG application interval.\n1.0 = apply through end of denoising.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: 4, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        EnableNormalization = T2IParamTypes.Register<string>(new("Normalize Audio",
            "Normalize output audio to a target loudness level.",
            "true",
            GetValues: _ => ["true///Yes (Recommended)", "false///No"],
            OrderPriority: 5, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        NormalizationDB = T2IParamTypes.Register<double>(new("Normalization dB",
            "Target loudness in dBFS when normalization is enabled.\n-14 dB is typical for streaming.",
            "-14.0",
            Min: -30.0, Max: 0.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: 6, Group: MusicGroup, FeatureFlag: "acestep_music_params"));

        #endregion

        #region Music — ACE-Step LM Planner
        // TODO: Integrate with SwarmUI's AbstractLLMBackend when LLMAPI.cs is complete.
        // These params are registered and wired through BuildEngineArgs but the actual
        // LM inference is stubbed in music_acestep.py until SwarmUI LLM integration is ready.
        ACELMModel = T2IParamTypes.Register<string>(new("ACE LM Model",
            "Language Model planner for structured music metadata generation.\nRequires SwarmUI LLM backend integration (not yet available).",
            "none",
            GetValues: _ => [
                "none///None (Disabled)", "0.6B///Qwen3 0.6B (Fast)",
                "1.7B///Qwen3 1.7B (Balanced)", "4B///Qwen3 4B (Best)"
            ],
            OrderPriority: -10, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        Thinking = T2IParamTypes.Register<string>(new("LM Thinking",
            "Enable chain-of-thought reasoning in the LM planner.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -9, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        LMTemperature = T2IParamTypes.Register<double>(new("LM Temperature",
            "Sampling temperature for the LM planner.\nHigher = more creative metadata generation.",
            "0.85",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        LMCFGScale = T2IParamTypes.Register<double>(new("LM CFG Scale",
            "Classifier-free guidance scale for the LM planner.",
            "2.0",
            Min: 1.0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        LMTopK = T2IParamTypes.Register<int>(new("LM Top K",
            "Top-K sampling for the LM planner.\n0 = disabled.",
            "0",
            Min: 0, Max: 500, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        LMTopP = T2IParamTypes.Register<double>(new("LM Top P",
            "Nucleus sampling threshold for the LM planner.",
            "0.9",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        LMNegativePrompt = T2IParamTypes.Register<string>(new("LM Negative Prompt",
            "Negative prompt for the LM planner.\nDescribes unwanted characteristics to avoid.",
            "",
            OrderPriority: -4, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        UseCotMetas = T2IParamTypes.Register<string>(new("CoT Metas",
            "Include meta tags (genre, mood, instruments) in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -3, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        UseCotCaption = T2IParamTypes.Register<string>(new("CoT Caption",
            "Include music description caption in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -2, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        UseCotLanguage = T2IParamTypes.Register<string>(new("CoT Language",
            "Include language detection in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -1, Group: MusicGroup, FeatureFlag: "acestep_lm_params"));

        #endregion

        #region Music — ACE-Step Tasks
        ACETaskType = T2IParamTypes.Register<string>(new("Task Type",
            "ACE-Step generation task type.\ntext2music = generate from prompt. cover = style transfer.\nrepaint = regenerate a section. extract = extract elements.\nlego = combine elements. complete = extend/continue.",
            "text2music",
            GetValues: _ => [
                "text2music///Text to Music", "cover///Cover (Style Transfer)",
                "repaint///Repaint (Section Regen)", "extract///Extract Elements",
                "lego///Lego (Combine)", "complete///Complete (Extend)"
            ],
            OrderPriority: -10, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        ACESourceAudio = T2IParamTypes.Register<AudioFile>(new("ACE Source Audio",
            "Source audio for cover, repaint, extract, lego, and complete tasks.\nRequired for all tasks except text2music.",
            null,
            OrderPriority: -9, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        ACEReferenceAudio = T2IParamTypes.Register<AudioFile>(new("Style Reference Audio",
            "Optional style/timbre reference audio.\nThe generated music will match the style of this reference.",
            null,
            OrderPriority: -8, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        RepaintStart = T2IParamTypes.Register<double>(new("Repaint Start",
            "Start time in seconds for repaint task.\nThe section from this point will be regenerated.",
            "0.0",
            Min: 0.0, Max: 600.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        RepaintEnd = T2IParamTypes.Register<double>(new("Repaint End",
            "End time in seconds for repaint task.\n-1 = auto (repaint to end of audio).",
            "-1.0",
            Min: -1.0, Max: 600.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        CoverStrength = T2IParamTypes.Register<double>(new("Cover Strength",
            "Style transfer strength for cover task.\n1.0 = full transfer. Lower = more of original.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        CoverNoiseStrength = T2IParamTypes.Register<double>(new("Cover Noise",
            "Noise injection strength for cover task.\nAdds variation to the style transfer.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: MusicGroup, FeatureFlag: "acestep_task_params"));

        #endregion

        #region Music — MusicGen
        MelodyAudio = T2IParamTypes.Register<AudioFile>(new("Melody Audio",
            "Reference melody for MusicGen melody conditioning.\nOnly used with the melody model variant.",
            null,
            OrderPriority: -5, Group: MusicGroup, FeatureFlag: "musicgen_music_params"));

        #endregion

        #region Voice Clone Shared
        SourceAudio = T2IParamTypes.Register<AudioFile>(new("Source Audio",
            "Audio with the voice to clone or the audio to convert.\nProvide a clean recording.",
            null,
            OrderPriority: -10, Group: CloneGroup, FeatureFlag: "audiolab_clone"));

        TargetVoice = T2IParamTypes.Register<AudioFile>(new("Target Voice",
            "Reference voice for tone conversion.\nThe source audio will be converted to match this voice.",
            null,
            OrderPriority: -9, Group: CloneGroup, FeatureFlag: "audiolab_clone"));

        #endregion

        #region Clone — RVC
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

        #endregion

        #region Clone — GPT-SoVITS
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

        #endregion

        #region Audio FX Shared
        FXInput = T2IParamTypes.Register<AudioFile>(new("FX Input",
            "Audio file to process.\nUpload audio for separation, enhancement, or denoising.",
            null,
            OrderPriority: -10, Group: FXGroup, FeatureFlag: "audiolab_fx"));

        #endregion

        #region FX — Demucs
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

        #endregion

        #region FX — Resemble Enhance
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

        #endregion

        #region Sound FX Shared
        SFXDuration = T2IParamTypes.Register<double>(new("SFX Duration",
            "Duration of generated sound effect in seconds.",
            "10",
            Min: 1, Max: 60, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: SFXGroup, FeatureFlag: "audiolab_sfx"));

        #endregion
    }
}
