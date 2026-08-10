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
    /// <summary>Audio generation parameter group (music + sound effects).</summary>
    public static T2IParamGroup AudioGenGroup;
    /// <summary>Voice reference parameter group for TTS voice cloning.</summary>
    public static T2IParamGroup VoiceRefGroup;
    /// <summary>Voice conversion parameter group (RVC, OpenVoice, GPT-SoVITS).</summary>
    public static T2IParamGroup CloneGroup;
    /// <summary>Audio processing parameter group (stem separation, enhancement).</summary>
    public static T2IParamGroup AudioProcGroup;

    /// <summary>Output format parameter group (shared across all audio-producing categories).</summary>
    public static T2IParamGroup OutputGroup;

    #endregion

    #region Output Format (flag: audiolab_output)

    /// <summary>Audio output file format. Feature flag: <c>audiolab_output</c>.</summary>
    public static T2IRegisteredParam<string> AudioOutputFormat;
    /// <summary>Quality/compression level for lossy formats. Feature flag: <c>audiolab_output</c>.</summary>
    public static T2IRegisteredParam<string> AudioQuality;

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
    /// <summary>Classifier-free guidance scale for Dia. Feature flag: <c>dia_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> DiaCFGScale;

    #endregion

    #region TTS — F5-TTS (flag: f5_tts_params)

    /// <summary>Flow-matching function evaluation step count for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> NFEStep;
    /// <summary>Speech speed multiplier for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> F5Speed;
    /// <summary>Classifier-free guidance scale for F5-TTS. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> F5CFG;
    /// <summary>Sway-sampling coefficient for F5-TTS flow matching. Feature flag: <c>f5_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> F5SwaySampling;

    #endregion

    #region TTS — ZipVoice (flag: zipvoice_tts_params)

    /// <summary>Euler flow-matching step count for ZipVoice. Feature flag: <c>zipvoice_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> ZipVoiceSteps;
    /// <summary>Speech speed multiplier for ZipVoice. Feature flag: <c>zipvoice_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ZipVoiceSpeed;
    /// <summary>Classifier-free guidance scale for ZipVoice. Feature flag: <c>zipvoice_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ZipVoiceCFG;

    #endregion

    #region TTS — Zonos (flag: zonos_tts_params)

    /// <summary>Language selection for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> ZonosLanguage;
    /// <summary>Emotional tone for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> ZonosEmotion;
    /// <summary>Pitch standard deviation for Zonos. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ZonosPitchStd;
    /// <summary>Speaking rate for Zonos TTS. Feature flag: <c>zonos_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> SpeakingRate;

    #endregion

    #region TTS — Qwen3-TTS

    /// <summary>Language for Qwen3-TTS synthesis. Feature flag: <c>qwen3tts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Language;
    /// <summary>Speaker voice for Qwen3-TTS CustomVoice models. Feature flag: <c>qwen3tts_speaker_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Speaker;
    /// <summary>Natural language instruction for Qwen3-TTS voice style/emotion. Feature flag: <c>qwen3tts_instruct_params</c>.</summary>
    public static T2IRegisteredParam<string> Qwen3Instruct;

    #endregion

    #region TTS — MeloTTS (flag: melotts_tts_params)

    /// <summary>Speaker/accent selection within a MeloTTS checkpoint. Feature flag: <c>melotts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> MeloSpeaker;
    /// <summary>Speech speed multiplier for MeloTTS. Feature flag: <c>melotts_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> MeloSpeed;

    #endregion

    #region TTS — StyleTTS 2 (flag: styletts2_tts_params)

    /// <summary>Diffusion sampler step count for StyleTTS 2. Feature flag: <c>styletts2_tts_params</c>.</summary>
    public static T2IRegisteredParam<int> StyleTTS2DiffusionSteps;
    /// <summary>Classifier-free-style embedding scale for StyleTTS 2. Feature flag: <c>styletts2_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> StyleTTS2EmbeddingScale;
    /// <summary>Timbre blend toward the reference voice. Feature flag: <c>styletts2_clone_params</c>.</summary>
    public static T2IRegisteredParam<double> StyleTTS2Alpha;
    /// <summary>Prosody blend toward the reference voice. Feature flag: <c>styletts2_clone_params</c>.</summary>
    public static T2IRegisteredParam<double> StyleTTS2Beta;

    #endregion

    #region TTS — Spark-TTS (flag: sparktts_tts_params)

    /// <summary>Voice gender for Spark-TTS voice creation. Feature flag: <c>sparktts_create_params</c>.</summary>
    public static T2IRegisteredParam<string> SparkGender;
    /// <summary>Pitch level for Spark-TTS voice creation. Feature flag: <c>sparktts_create_params</c>.</summary>
    public static T2IRegisteredParam<string> SparkPitch;
    /// <summary>Speed level for Spark-TTS voice creation. Feature flag: <c>sparktts_create_params</c>.</summary>
    public static T2IRegisteredParam<string> SparkSpeed;

    #endregion

    #region TTS — Qwen3-TTS extras

    /// <summary>Faster clone mode that conditions on the speaker vector only, at some prosody cost.</summary>
    public static T2IRegisteredParam<string> Qwen3XVectorOnly;

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

    #region TTS — Pocket TTS (flag: pockettts_tts_params)

    /// <summary>Built-in voice preset for Pocket TTS. Feature flag: <c>pockettts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PocketTTSVoice;

    #endregion

    #region TTS — Kyutai TTS (flag: kyutaitts_tts_params)

    /// <summary>Voice selection for Kyutai TTS from the tts-voices repo. Feature flag: <c>kyutaitts_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> KyutaiTTSVoice;

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

    #region STT — Whisper extras (flag: whisper_stt_params)

    /// <summary>Beam count for Whisper beam search. Feature flag: <c>whisper_stt_params</c>.</summary>
    public static T2IRegisteredParam<int> WhisperBeamSize;
    /// <summary>Optional text prompt biasing Whisper's vocabulary. Feature flag: <c>whisper_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> WhisperInitialPrompt;

    #endregion

    #region STT — AssemblyAI (flag: assemblyai_stt_params)

    /// <summary>Per-speaker labelling. Feature flag: <c>assemblyai_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> AssemblySpeakerLabels;
    /// <summary>Per-utterance sentiment analysis. Feature flag: <c>assemblyai_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> AssemblySentiment;

    #endregion

    #region TTS — ElevenLabs (flag: elevenlabs_tts_params)

    /// <summary>Voice consistency vs expressiveness. Feature flag: <c>elevenlabs_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ElevenStability;
    /// <summary>Adherence to the original voice. Feature flag: <c>elevenlabs_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ElevenSimilarity;
    /// <summary>Style exaggeration. Feature flag: <c>elevenlabs_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> ElevenStyle;
    /// <summary>Speaker-boost toggle. Feature flag: <c>elevenlabs_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> ElevenSpeakerBoost;

    #endregion

    #region TTS — Azure (flag: azure_tts_params)

    /// <summary>mstts express-as speaking style. Feature flag: <c>azure_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> AzureStyle;
    /// <summary>Intensity of the selected Azure style. Feature flag: <c>azure_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> AzureStyleDegree;

    #endregion

    #region TTS — Amazon Polly (flag: polly_tts_params)

    /// <summary>Polly synthesis engine. Feature flag: <c>polly_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PollyEngine;
    /// <summary>Polly voice id. Feature flag: <c>polly_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PollyVoice;

    #endregion

    /// <summary>Azure STT profanity handling. Feature flag: <c>azure_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> AzureProfanity;
    /// <summary>Deepgram STT model. Feature flag: <c>deepgram_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> DeepgramSTTModel;
    /// <summary>Google Cloud STT v1 model. Feature flag: <c>google_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> GoogleSTTModel;
    /// <summary>Optional vocabulary hint for OpenAI transcription. Feature flag: <c>openai_stt_params</c>.</summary>
    public static T2IRegisteredParam<string> OpenAISTTPrompt;

    #region TTS — Cloud voice/style extras

    /// <summary>OpenAI TTS voice. Feature flag: <c>openai_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> OpenAIVoice;
    /// <summary>Free-form delivery instructions (gpt-4o-mini-tts only). Feature flag: <c>openai_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> OpenAIInstructions;
    /// <summary>Speech speed for OpenAI TTS. Feature flag: <c>openai_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> OpenAISpeed;
    /// <summary>Google Cloud TTS voice name. Feature flag: <c>google_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> GoogleVoiceName;
    /// <summary>Google Cloud TTS speaking rate. Feature flag: <c>google_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> GoogleSpeakingRate;
    /// <summary>Google Cloud TTS pitch offset in semitones. Feature flag: <c>google_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> GooglePitch;
    /// <summary>Deepgram Aura voice model. Feature flag: <c>deepgram_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> DeepgramVoice;
    /// <summary>Cartesia voice id. Feature flag: <c>cartesia_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> CartesiaVoice;
    /// <summary>Cartesia speech speed (generation_config, 0.6-1.5). Feature flag: <c>cartesia_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> CartesiaSpeed;
    /// <summary>Cartesia model id. Feature flag: <c>cartesia_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> CartesiaModel;
    /// <summary>PlayHT voice id. Feature flag: <c>playht_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PlayHTVoice;
    /// <summary>PlayHT voice engine. Feature flag: <c>playht_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PlayHTEngine;
    /// <summary>PlayHT speech speed. Feature flag: <c>playht_tts_params</c>.</summary>
    public static T2IRegisteredParam<double> PlayHTSpeed;
    /// <summary>PlayHT output quality tier. Feature flag: <c>playht_tts_params</c>.</summary>
    public static T2IRegisteredParam<string> PlayHTQuality;
    /// <summary>Dolby.io Media Enhance preset. Feature flag: <c>dolby_audioproc_params</c>.</summary>
    public static T2IRegisteredParam<string> DolbyPreset;
    /// <summary>Strip background noise before voice conversion. Feature flag: <c>elevenlabs_vc_params</c>.</summary>
    public static T2IRegisteredParam<string> ElevenRemoveNoise;
    /// <summary>ElevenLabs SFX clip length in seconds. Feature flag: <c>elevenlabs_sfx_params</c>.</summary>
    public static T2IRegisteredParam<double> ElevenSFXDuration;
    /// <summary>How literally ElevenLabs SFX follows the prompt. Feature flag: <c>elevenlabs_sfx_params</c>.</summary>
    public static T2IRegisteredParam<double> ElevenSFXInfluence;

    #endregion

    #region Audio Generation Shared (flag: audiolab_audiogen)

    /// <summary>Duration of generated music in seconds. Feature flag: <c>audiolab_audiogen</c>.</summary>
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
    /// <summary>Diffusion inference step count for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<int> InferStep;
    /// <summary>Classifier-free guidance strength for ACE-Step. Feature flag: <c>acestep_music_params</c>.</summary>
    public static T2IRegisteredParam<double> ACEGuidanceScale;
    /// <summary>Instrumental-only toggle, shared by ACE-Step and Suno. Feature flag: <c>music_instrumental_param</c>.</summary>
    public static T2IRegisteredParam<string> Instrumental;
    /// <summary>Style/genre tags for cloud music providers (Suno, Udio). Feature flag: <c>music_style_params</c>.</summary>
    public static T2IRegisteredParam<string> MusicStyle;
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

    #region Music — Stable Audio (flag: stableaudio_music_params)

    /// <summary>Diffusion steps for Stable Audio Open Small. Feature flag: <c>stableaudio_music_params</c>.</summary>
    public static T2IRegisteredParam<int> StableAudioSteps;

    #endregion

    #region Music — MusicGen (flag: musicgen_music_params)

    #endregion

    #region Music — YuE (flag: yue_music_params)

    /// <summary>Song lyrics with [verse]/[chorus] section markers for YuE. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<string> YuELyrics;
    /// <summary>Max new tokens for Stage-1 generation (controls output length). Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<int> YuEMaxTokens;
    /// <summary>Quantization mode for YuE Stage-1 model. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<string> YuEQuantization;
    /// <summary>Stage-2 batch size (lower = less VRAM). Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<int> YuEStage2BatchSize;
    /// <summary>Sampling temperature for YuE generation. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<double> YuETemperature;
    /// <summary>Nucleus sampling threshold for YuE. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<double> YuETopP;
    /// <summary>Repetition penalty for YuE token generation. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<double> YuERepetitionPenalty;
    /// <summary>Number of lyric segments to generate for YuE. Feature flag: <c>yue_music_params</c>.</summary>
    public static T2IRegisteredParam<int> YuESegments;

    #endregion

    #region Music — HeartLib (flag: heartlib_music_params)

    /// <summary>Song lyrics with [Verse]/[Chorus]/[Bridge] section markers for HeartLib. Feature flag: <c>heartlib_music_params</c>.</summary>
    public static T2IRegisteredParam<string> HeartLibLyrics;
    /// <summary>Classifier-free guidance strength for HeartLib. Feature flag: <c>heartlib_music_params</c>.</summary>
    public static T2IRegisteredParam<double> HeartLibCFGScale;
    /// <summary>Sampling temperature for HeartLib generation. Feature flag: <c>heartlib_music_params</c>.</summary>
    public static T2IRegisteredParam<double> HeartLibTemperature;
    /// <summary>Top-K token sampling for HeartLib. Feature flag: <c>heartlib_music_params</c>.</summary>
    public static T2IRegisteredParam<int> HeartLibTopK;

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

    #region Audio Processing Shared (flag: audiolab_audioproc)

    /// <summary>Audio file input for effects processing. Feature flag: <c>audiolab_audioproc</c>.</summary>
    public static T2IRegisteredParam<AudioFile> FXInput;

    #endregion

    #region FX — Demucs (flag: demucs_fx_params)

    /// <summary>Processing chunk overlap for Demucs separation. Feature flag: <c>demucs_fx_params</c>.</summary>
    public static T2IRegisteredParam<double> Overlap;
    /// <summary>Random shift count for Demucs equivariant stabilization. Feature flag: <c>demucs_fx_params</c>.</summary>
    public static T2IRegisteredParam<int> Shifts;
    /// <summary>Demucs segment length in seconds (lower = less memory). Feature flag: <c>demucs_fx_params</c>.</summary>
    public static T2IRegisteredParam<double> DemucsSegment;

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
        AudioGenGroup = new("Audio Generation", Open: true, OrderPriority: -25, Toggles: false,
            Description: "Audio generation parameters for music and sound effects. Describe what you want in the Prompt box above.");
        CloneGroup = new("Voice Conversion", Open: true, OrderPriority: -24, Toggles: false,
            Description: "Voice conversion parameters. Provide source audio to convert and target voice reference.");
        AudioProcGroup = new("Audio Processing", Open: true, OrderPriority: -23, Toggles: false,
            Description: "Audio processing parameters. Upload audio to process (stem separation, denoising, enhancement).");

        OutputGroup = new("Output Format", Open: false, OrderPriority: -20, Toggles: false,
            Description: "Audio output format and quality settings. Applies to all generated audio.");

        #endregion

        #region Output Format
        AudioOutputFormat = T2IParamTypes.Register<string>(new("Audio Output Format",
            "File format for saved audio output.\nWAV 16-bit = standard quality, smallest WAV. WAV 32-bit = lossless float, larger.\nFLAC = lossless compression (~50% of WAV). MP3/OGG = lossy, smallest files.",
            "wav_16", IgnoreIf: "wav_16",
            GetValues: _ => [
                "wav_16///WAV (16-bit PCM)",
                "wav_32///WAV (32-bit Float)",
                "flac///FLAC (Lossless)",
                "mp3///MP3",
                "ogg///OGG Vorbis"
            ],
            OrderPriority: -10, Group: OutputGroup, FeatureFlag: "audiolab_output"));

        AudioQuality = T2IParamTypes.Register<string>(new("Audio Quality",
            "Quality level for compressed formats.\nAffects MP3 bitrate and OGG quality. Ignored for WAV and FLAC.",
            "high", IgnoreIf: "high",
            GetValues: _ => [
                "low///Low (128kbps MP3)",
                "medium///Medium (192kbps MP3)",
                "high///High (256kbps MP3)",
                "max///Maximum (320kbps MP3)"
            ],
            OrderPriority: -9, Group: OutputGroup, FeatureFlag: "audiolab_output", IsAdvanced: true));

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
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "audiolab_tts", IsAdvanced: true));

        #endregion

        #region TTS Shared Sampling
        Temperature = T2IParamTypes.Register<double>(new("Temperature",
            "Sampling temperature.\nHigher = more varied/creative speech. Lower = more consistent.",
            "0.8",
            Min: 0.1, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "tts_sampling", IsAdvanced: true));

        TopP = T2IParamTypes.Register<double>(new("Top P",
            "Nucleus sampling threshold.\n1.0 = no filtering. Lower values restrict to higher probability tokens.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "tts_sampling", IsAdvanced: true));

        RepetitionPenalty = T2IParamTypes.Register<double>(new("Repetition Penalty",
            "Penalizes repeated tokens.\nHigher values reduce stuttering and repetitive speech patterns.",
            "1.2",
            Min: 1.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: TTSGroup, FeatureFlag: "tts_sampling", IsAdvanced: true));

        TopK = T2IParamTypes.Register<int>(new("Top K",
            "Top-K token sampling.\nLimits sampling to the K most likely tokens. 0 = disabled.",
            "50",
            Min: 0, Max: 1000, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "tts_sampling", IsAdvanced: true));

        MinP = T2IParamTypes.Register<double>(new("Min P",
            "Minimum probability threshold.\nTokens below this probability are excluded from sampling.",
            "0.05",
            Min: 0.0, Max: 1.0, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "tts_sampling", IsAdvanced: true));

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
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "bark_tts_params", IsAdvanced: true));

        WaveformTemp = T2IParamTypes.Register<double>(new("Waveform Temperature",
            "Controls randomness of audio waveform generation.\nHigher = more varied audio quality.",
            "0.7",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "bark_tts_params", IsAdvanced: true));

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
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "chatterbox_tts_params", IsAdvanced: true));

        #endregion

        #region TTS — Kokoro
        KokoroVoice = T2IParamTypes.Register<string>(new("Kokoro Voice",
            "Voice to synthesize with. The first letter is the language (a=American, b=British, j=Japanese, z=Mandarin, e=Spanish, f=French, h=Hindi, i=Italian, p=Portuguese); the second is f=female / m=male.\nAll 54 official voices from the model card are listed.",
            "af_heart",
            GetValues: _ => [
                "af_heart///American English — Heart (F)",
                "af_alloy///American English — Alloy (F)",
                "af_aoede///American English — Aoede (F)",
                "af_bella///American English — Bella (F)",
                "af_jessica///American English — Jessica (F)",
                "af_kore///American English — Kore (F)",
                "af_nicole///American English — Nicole (F)",
                "af_nova///American English — Nova (F)",
                "af_river///American English — River (F)",
                "af_sarah///American English — Sarah (F)",
                "af_sky///American English — Sky (F)",
                "am_adam///American English — Adam (M)",
                "am_echo///American English — Echo (M)",
                "am_eric///American English — Eric (M)",
                "am_fenrir///American English — Fenrir (M)",
                "am_liam///American English — Liam (M)",
                "am_michael///American English — Michael (M)",
                "am_onyx///American English — Onyx (M)",
                "am_puck///American English — Puck (M)",
                "am_santa///American English — Santa (M)",
                "bf_alice///British English — Alice (F)",
                "bf_emma///British English — Emma (F)",
                "bf_isabella///British English — Isabella (F)",
                "bf_lily///British English — Lily (F)",
                "bm_daniel///British English — Daniel (M)",
                "bm_fable///British English — Fable (M)",
                "bm_george///British English — George (M)",
                "bm_lewis///British English — Lewis (M)",
                "jf_alpha///Japanese — Alpha (F)",
                "jf_gongitsune///Japanese — Gongitsune (F)",
                "jf_nezumi///Japanese — Nezumi (F)",
                "jf_tebukuro///Japanese — Tebukuro (F)",
                "jm_kumo///Japanese — Kumo (M)",
                "zf_xiaobei///Mandarin — Xiaobei (F)",
                "zf_xiaoni///Mandarin — Xiaoni (F)",
                "zf_xiaoxiao///Mandarin — Xiaoxiao (F)",
                "zf_xiaoyi///Mandarin — Xiaoyi (F)",
                "zm_yunjian///Mandarin — Yunjian (M)",
                "zm_yunxi///Mandarin — Yunxi (M)",
                "zm_yunxia///Mandarin — Yunxia (M)",
                "zm_yunyang///Mandarin — Yunyang (M)",
                "ef_dora///Spanish — Dora (F)",
                "em_alex///Spanish — Alex (M)",
                "em_santa///Spanish — Santa (M)",
                "ff_siwis///French — Siwis (F)",
                "hf_alpha///Hindi — Alpha (F)",
                "hf_beta///Hindi — Beta (F)",
                "hm_omega///Hindi — Omega (M)",
                "hm_psi///Hindi — Psi (M)",
                "if_sara///Italian — Sara (F)",
                "im_nicola///Italian — Nicola (M)",
                "pf_dora///Brazilian Portuguese — Dora (F)",
                "pm_alex///Brazilian Portuguese — Alex (M)",
                "pm_santa///Brazilian Portuguese — Santa (M)"
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
            "Piper voice. Each voice is a separate download, named language-speaker-quality.\nAll 37 English voices from the official VOICES.md are listed; higher quality is larger and slower.",
            "en_US-amy-medium",
            GetValues: _ => [
                "en_US-amy-low///US amy (low)",
                "en_US-amy-medium///US amy (medium)",
                "en_US-arctic-medium///US arctic (medium)",
                "en_US-bryce-medium///US bryce (medium)",
                "en_US-danny-low///US danny (low)",
                "en_US-hfc_female-medium///US hfc female (medium)",
                "en_US-hfc_male-medium///US hfc male (medium)",
                "en_US-joe-medium///US joe (medium)",
                "en_US-john-medium///US john (medium)",
                "en_US-kathleen-low///US kathleen (low)",
                "en_US-kristin-medium///US kristin (medium)",
                "en_US-kusal-medium///US kusal (medium)",
                "en_US-l2arctic-medium///US l2arctic (medium)",
                "en_US-lessac-low///US lessac (low)",
                "en_US-lessac-medium///US lessac (medium)",
                "en_US-lessac-high///US lessac (high)",
                "en_US-libritts-high///US libritts (high)",
                "en_US-libritts_r-medium///US libritts r (medium)",
                "en_US-ljspeech-medium///US ljspeech (medium)",
                "en_US-ljspeech-high///US ljspeech (high)",
                "en_US-norman-medium///US norman (medium)",
                "en_US-reza_ibrahim-medium///US reza ibrahim (medium)",
                "en_US-ryan-low///US ryan (low)",
                "en_US-ryan-medium///US ryan (medium)",
                "en_US-ryan-high///US ryan (high)",
                "en_US-sam-medium///US sam (medium)",
                "en_GB-alan-low///GB alan (low)",
                "en_GB-alan-medium///GB alan (medium)",
                "en_GB-alba-medium///GB alba (medium)",
                "en_GB-aru-medium///GB aru (medium)",
                "en_GB-cori-medium///GB cori (medium)",
                "en_GB-cori-high///GB cori (high)",
                "en_GB-jenny_dioco-medium///GB jenny dioco (medium)",
                "en_GB-northern_english_male-medium///GB northern english male (medium)",
                "en_GB-semaine-medium///GB semaine (medium)",
                "en_GB-southern_english_female-low///GB southern english female (low)",
                "en_GB-vctk-medium///GB vctk (medium)"
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
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params", IsAdvanced: true));

        VibeVoiceCFG = T2IParamTypes.Register<double>(new("VibeVoice CFG",
            "Classifier-free guidance scale for speech diffusion.\n1.3 is recommended for standard models, 1.5 for streaming.",
            "1.3",
            Min: 0.0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "vibevoice_tts_params"));

        #endregion

        #region TTS — Dia
        CFGFilterTopK = T2IParamTypes.Register<int>(new("CFG Filter Top K",
            "Top-K filtering for classifier-free guidance (Dia\'s cfg_filter_top_k).\nUpstream default is 45.",
            "45",
            Min: 0, Max: 500, Step: 5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "dia_tts_params", IsAdvanced: true));

        DiaCFGScale = T2IParamTypes.Register<double>(new("Dia CFG Scale",
            "Classifier-free guidance strength for Dia.\nHigher = closer to the prompt, lower = more natural variation.",
            "3.0",
            Min: 1.0, Max: 10.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: TTSGroup, FeatureFlag: "dia_tts_params"));

        #endregion

        #region TTS — F5-TTS
        NFEStep = T2IParamTypes.Register<int>(new("NFE Steps",
            "Number of function evaluation steps for flow matching.\nMore steps = higher quality but slower.",
            "32",
            Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "f5_tts_params", IsAdvanced: true));

        F5Speed = T2IParamTypes.Register<double>(new("Speed",
            "Speech speed multiplier.\n1.0 = normal, 0.5 = half speed, 2.0 = double speed.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "f5_tts_params"));

        F5CFG = T2IParamTypes.Register<double>(new("F5 CFG",
            "Classifier-free guidance for flow matching.\n2.0 is recommended. Higher = stronger prompt adherence.",
            "2.0",
            Min: 0.0, Max: 10.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "f5_tts_params", IsAdvanced: true));

        F5SwaySampling = T2IParamTypes.Register<double>(new("F5 Sway Sampling",
            "Sway-sampling coefficient.\nNegative values bias sampling toward earlier flow steps; -1.0 is the upstream default.",
            "-1.0",
            Min: -1.0, Max: 1.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: TTSGroup, FeatureFlag: "f5_tts_params", IsAdvanced: true));

        #endregion

        #region TTS — ZipVoice
        ZipVoiceSteps = T2IParamTypes.Register<int>(new("ZipVoice Steps",
            "Flow-matching sampling steps.\nUpstream default is 8; the distill model can go as low as 4.",
            "8",
            Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "zipvoice_tts_params", IsAdvanced: true));

        ZipVoiceSpeed = T2IParamTypes.Register<double>(new("ZipVoice Speed",
            "Speech speed multiplier.\n1.0 = normal, 0.5 = half speed, 2.0 = double speed.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "zipvoice_tts_params"));

        ZipVoiceCFG = T2IParamTypes.Register<double>(new("ZipVoice CFG",
            "Classifier-free guidance for flow matching.\n1.0 is the base checkpoint's default. Higher = stronger prompt adherence.",
            "1.0",
            Min: 0.0, Max: 10.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "zipvoice_tts_params", IsAdvanced: true));

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
            "Emotion preset. Zonos conditions on an 8-way vector (Happiness, Sadness, Disgust, Fear, Surprise, Anger, Other, Neutral);\neach preset weights that vector, which is then renormalized to sum 1.",
            "neutral",
            GetValues: _ => [
                "neutral///Neutral", "happy///Happy", "sad///Sad",
                "angry///Angry", "fearful///Fearful", "surprised///Surprised",
                "disgusted///Disgusted"
            ],
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        SpeakingRate = T2IParamTypes.Register<double>(new("Speaking Rate",
            "Speaking rate in phonemes per second.\nReference range is 0-40: 15 is the default, 30 is very fast, 10 is slow.",
            "15.0",
            Min: 0.0, Max: 40.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "zonos_tts_params"));

        ZonosPitchStd = T2IParamTypes.Register<double>(new("Pitch Variation",
            "Pitch standard deviation.\nReference guidance: 20-45 for normal speech, 60-150 for expressive delivery.",
            "20.0",
            Min: 0.0, Max: 400.0, Step: 5.0, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: TTSGroup, FeatureFlag: "zonos_tts_params", IsAdvanced: true));

        #endregion

        #region TTS — Fish Speech
        FishSpeechMaxTokens = T2IParamTypes.Register<int>(new("FishSpeech Max Tokens",
            "Cap on generated tokens.\nUpstream default is 0, which means generate until the stop token.",
            "0",
            Min: 0, Max: 4096, Step: 64, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params", IsAdvanced: true));

        FishSpeechChunkLength = T2IParamTypes.Register<int>(new("FishSpeech Chunk Length",
            "Text chunk size for long-form synthesis (upstream default 300).\nNOTE: the in-process engine does not chunk text yet, so this currently has no effect.",
            "300",
            Min: 100, Max: 1000, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params", IsAdvanced: true));

        FishSpeechNormalize = T2IParamTypes.Register<string>(new("FishSpeech Normalize",
            "Normalize text before synthesis.\nImproves handling of numbers, abbreviations, and special characters.",
            "true",
            GetValues: _ => ["true///Yes (Recommended)", "false///No"],
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "fishspeech_tts_params", IsAdvanced: true));

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
            "Built-in CustomVoice speaker.\nFour are confirmed against the checkpoint (Ryan, Serena, Ono_Anna, Sohee); the others fall back to the default voice until their ids are verified.",
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
            OrderPriority: -4, Group: TTSGroup, FeatureFlag: "qwen3tts_speaker_params"));

        Qwen3Instruct = T2IParamTypes.Register<string>(new("Qwen3 Instruct",
            "Natural language instruction for voice control.\nCustomVoice: describe emotion/style (e.g. 'Speak with excitement').\nVoiceDesign: describe the voice (e.g. 'A deep male voice with a British accent').\nIgnored for Base models.",
            "",
            OrderPriority: -3, Group: TTSGroup, FeatureFlag: "qwen3tts_instruct_params"));

        Qwen3XVectorOnly = T2IParamTypes.Register<string>(new("Qwen3 X-Vector Only",
            "Condition on the speaker vector alone instead of the full reference encoding.\nFaster, with less prosody transfer. Clone (Base) models only.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -2, Group: TTSGroup, FeatureFlag: "qwen3tts_tts_params", IsAdvanced: true));

        #endregion

        #region TTS — MeloTTS
        MeloSpeaker = T2IParamTypes.Register<string>(new("MeloTTS Speaker",
            "Speaker/accent within the selected MeloTTS checkpoint.\nEnglish ships EN-US, EN-BR, EN-AU, EN-Default and EN-India; other languages have a single speaker.",
            "EN-US",
            GetValues: _ => ["EN-US///English (US)", "EN-BR///English (British)", "EN-AU///English (Australian)",
                "EN-Default///English (Default)", "EN_INDIA///English (Indian)"],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "melotts_tts_params"));

        MeloSpeed = T2IParamTypes.Register<double>(new("MeloTTS Speed",
            "Speech speed multiplier.",
            "1.0",
            Min: 0.5, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "melotts_tts_params"));

        #endregion

        #region TTS — StyleTTS 2
        StyleTTS2DiffusionSteps = T2IParamTypes.Register<int>(new("StyleTTS2 Diffusion Steps",
            "Style-diffusion sampler steps.\nUpstream inference uses 5; more steps trade speed for style stability.",
            "5",
            Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "styletts2_tts_params", IsAdvanced: true));

        StyleTTS2EmbeddingScale = T2IParamTypes.Register<double>(new("StyleTTS2 Embedding Scale",
            "Classifier-free-style guidance on the text embedding.\nHigher = more emotive delivery. This is NOT the alpha/beta reference blend.",
            "1.0",
            Min: 0.5, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "styletts2_tts_params", IsAdvanced: true));

        StyleTTS2Alpha = T2IParamTypes.Register<double>(new("StyleTTS2 Alpha (Timbre)",
            "Timbre balance between the reference clip and the sampled style.\nUpstream: 0 matches the reference deterministically, 1 is maximum diversity and least similarity. Default 0.3.",
            "0.3",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: VoiceRefGroup, FeatureFlag: "styletts2_clone_params"));

        StyleTTS2Beta = T2IParamTypes.Register<double>(new("StyleTTS2 Beta (Prosody)",
            "Prosody balance between the reference clip and the sampled style.\nUpstream: 0 matches the reference deterministically, 1 is maximum diversity. Default 0.7.",
            "0.7",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: VoiceRefGroup, FeatureFlag: "styletts2_clone_params"));

        #endregion

        #region TTS — Spark-TTS
        SparkGender = T2IParamTypes.Register<string>(new("Spark Voice Gender",
            "Voice gender for Spark-TTS voice CREATION.\nIgnored when a reference clip is supplied (that switches it to cloning).",
            "female",
            GetValues: _ => ["female///Female", "male///Male"],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "sparktts_create_params"));

        SparkPitch = T2IParamTypes.Register<string>(new("Spark Pitch",
            "Pitch level for Spark-TTS voice creation.",
            "moderate",
            GetValues: _ => ["very_low///Very Low", "low///Low", "moderate///Moderate", "high///High", "very_high///Very High"],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "sparktts_create_params"));

        SparkSpeed = T2IParamTypes.Register<string>(new("Spark Speed",
            "Speed level for Spark-TTS voice creation.",
            "moderate",
            GetValues: _ => ["very_low///Very Slow", "low///Slow", "moderate///Moderate", "high///Fast", "very_high///Very Fast"],
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "sparktts_create_params"));

        #endregion

        #region TTS — CosyVoice
        CosyVoiceVoice = T2IParamTypes.Register<string>(new("CosyVoice Voice",
            "Preset speaker. NOTE: these presets belong to the CosyVoice-300M-SFT checkpoint.\nThe shipped CosyVoice2-0.5B has no built-in presets and works zero-shot — supply a Reference Audio clip and its transcript instead; this selection is ignored.",
            "中文女",
            GetValues: _ => [
                "中文女///Chinese Female", "中文男///Chinese Male",
                "英文女///English Female", "英文男///English Male",
                "日语男///Japanese Male", "粤语女///Cantonese Female",
                "韩语女///Korean Female"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "cosyvoice_tts_params"));

        #endregion

        #region TTS — Pocket TTS
        PocketTTSVoice = T2IParamTypes.Register<string>(new("Pocket TTS Voice",
            "Built-in voice embedding. All 26 voices published in the model repo are listed.",
            "alba",
            GetValues: _ => [
                "alba///Alba",
                "anna///Anna",
                "azelma///Azelma",
                "bill_boerst///Bill Boerst",
                "caro_davy///Caro Davy",
                "charles///Charles",
                "cosette///Cosette",
                "eponine///Eponine",
                "estelle///Estelle",
                "eve///Eve",
                "fantine///Fantine",
                "george///George",
                "giovanni///Giovanni",
                "jane///Jane",
                "javert///Javert",
                "jean///Jean",
                "juergen///Juergen",
                "lola///Lola",
                "marius///Marius",
                "mary///Mary",
                "michael///Michael",
                "paul///Paul",
                "peter_yearsley///Peter Yearsley",
                "rafael///Rafael",
                "stuart_bell///Stuart Bell",
                "vera///Vera"
            ],
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "pockettts_tts_params"));

        #endregion

        #region TTS — Kyutai TTS
        KyutaiTTSVoice = T2IParamTypes.Register<string>(new("Kyutai TTS Voice",
            "Voice file path within the kyutai/tts-voices repo.\nFolders: expresso/ (emotive), ears/ (per-speaker emotion), vctk/ (speaker ids), voice-donations/ (named).\nExamples: expresso/ex03-ex01_happy_001_channel1_334s.wav, vctk/p225_023.wav, voice-donations/James.wav",
            "expresso/ex03-ex01_happy_001_channel1_334s.wav",
            OrderPriority: -5, Group: TTSGroup, FeatureFlag: "kyutaitts_tts_params"));

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

        WhisperBeamSize = T2IParamTypes.Register<int>(new("Whisper Beam Size",
            "Beam-search width.\n1 = greedy (fastest); 5 is the upstream default for best accuracy.",
            "5",
            Min: 1, Max: 10, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: STTGroup, FeatureFlag: "whisper_stt_params", IsAdvanced: true));

        WhisperInitialPrompt = T2IParamTypes.Register<string>(new("Whisper Initial Prompt",
            "Optional text to bias vocabulary and spelling — useful for names, jargon and acronyms.",
            "",
            OrderPriority: -6, Group: STTGroup, FeatureFlag: "whisper_stt_params", IsAdvanced: true));

        #endregion

        #region STT — AssemblyAI
        AssemblySpeakerLabels = T2IParamTypes.Register<string>(new("Speaker Labels",
            "Label each utterance with a speaker id (diarization).",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -8, Group: STTGroup, FeatureFlag: "assemblyai_stt_params"));

        AssemblySentiment = T2IParamTypes.Register<string>(new("Sentiment Analysis",
            "Return per-utterance sentiment alongside the transcript.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -7, Group: STTGroup, FeatureFlag: "assemblyai_stt_params"));

        #endregion

        #region TTS — ElevenLabs
        ElevenStability = T2IParamTypes.Register<double>(new("ElevenLabs Stability",
            "Lower = more expressive and variable, higher = more consistent and monotone.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "elevenlabs_tts_params"));

        ElevenSimilarity = T2IParamTypes.Register<double>(new("ElevenLabs Similarity Boost",
            "How closely to match the original voice.\nVery high values can reproduce artifacts present in the source recording.",
            "0.75",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "elevenlabs_tts_params"));

        ElevenStyle = T2IParamTypes.Register<double>(new("ElevenLabs Style",
            "Style exaggeration. 0 disables it and is fastest.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "elevenlabs_tts_params"));

        ElevenSpeakerBoost = T2IParamTypes.Register<string>(new("ElevenLabs Speaker Boost",
            "Boost similarity to the original speaker, at some latency cost.",
            "true",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "elevenlabs_tts_params"));

        #endregion

        #region TTS — Azure
        AzureStyle = T2IParamTypes.Register<string>(new("Azure Speaking Style",
            "mstts express-as style. Availability depends on the chosen neural voice.",
            "",
            GetValues: _ => ["///Default", "cheerful///Cheerful", "sad///Sad", "angry///Angry", "excited///Excited",
                "friendly///Friendly", "hopeful///Hopeful", "shouting///Shouting", "whispering///Whispering",
                "terrified///Terrified", "unfriendly///Unfriendly", "newscast///Newscast", "customerservice///Customer Service"],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "azure_tts_params"));

        AzureStyleDegree = T2IParamTypes.Register<double>(new("Azure Style Degree",
            "Intensity of the selected speaking style. 1.0 is normal.",
            "1.0",
            Min: 0.01, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "azure_tts_params"));

        #endregion

        #region STT — Cloud model selection
        AzureProfanity = T2IParamTypes.Register<string>(new("Azure Profanity Handling",
            "How Azure handles profanity in the transcript.\nAPI default is masked (asterisks); removed strips it; raw leaves it in.",
            "masked",
            GetValues: _ => ["masked///Masked", "removed///Removed", "raw///Raw"],
            OrderPriority: -10, Group: STTGroup, FeatureFlag: "azure_stt_params"));

        DeepgramSTTModel = T2IParamTypes.Register<string>(new("Deepgram Model",
            "Deepgram speech-to-text model. Nova-3 is the current general-purpose recommendation.",
            "nova-3",
            GetValues: _ => ["nova-3///Nova-3 (recommended)", "nova-3-medical///Nova-3 Medical",
                "nova-2///Nova-2", "flux-general-en///Flux General (EN)", "flux-general-multi///Flux General (multi)",
                "whisper-large///Whisper Cloud (large)"],
            OrderPriority: -10, Group: STTGroup, FeatureFlag: "deepgram_stt_params"));

        GoogleSTTModel = T2IParamTypes.Register<string>(new("Google STT Model",
            "Google Speech-to-Text v1 model.\nChirp 2/3 are Speech-to-Text v2 only and are not reachable from this v1 endpoint.",
            "latest_long",
            GetValues: _ => ["latest_long///Latest Long", "latest_short///Latest Short", "telephony///Telephony",
                "telephony_short///Telephony Short", "medical_dictation///Medical Dictation",
                "medical_conversation///Medical Conversation", "command_and_search///Command & Search",
                "video///Video", "phone_call///Phone Call", "default///Default"],
            OrderPriority: -10, Group: STTGroup, FeatureFlag: "google_stt_params"));

        OpenAISTTPrompt = T2IParamTypes.Register<string>(new("Transcription Prompt",
            "Optional text biasing the transcript's vocabulary and spelling — names, jargon, acronyms.",
            "",
            OrderPriority: -10, Group: STTGroup, FeatureFlag: "openai_stt_params"));

        #endregion

        #region TTS — Cloud voice/style extras
        OpenAIVoice = T2IParamTypes.Register<string>(new("OpenAI Voice",
            "Voice for OpenAI text-to-speech. All 13 documented voices are listed.\ntts-1 and tts-1-hd support only 9 of them — ballad, marin and cedar are gpt-4o-mini-tts only.",
            "alloy",
            GetValues: _ => [
                "alloy///Alloy",
                "ash///Ash",
                "ballad///Ballad (gpt-4o-mini-tts only)",
                "coral///Coral",
                "echo///Echo",
                "fable///Fable",
                "nova///Nova",
                "onyx///Onyx",
                "sage///Sage",
                "shimmer///Shimmer",
                "verse///Verse",
                "marin///Marin (gpt-4o-mini-tts only)",
                "cedar///Cedar (gpt-4o-mini-tts only)"
            ],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "openai_tts_params"));

        OpenAIInstructions = T2IParamTypes.Register<string>(new("OpenAI Instructions",
            "Free-form delivery direction, e.g. 'Speak slowly and sound apologetic'.\nSupported by gpt-4o-mini-tts only.",
            "",
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "openai_tts_instructions_params"));

        OpenAISpeed = T2IParamTypes.Register<double>(new("OpenAI Speed",
            "Speech speed multiplier.",
            "1.0",
            Min: 0.25, Max: 4.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "openai_tts_params"));

        GoogleVoiceName = T2IParamTypes.Register<string>(new("Google Voice Name",
            "Google Cloud voice name. Must match the selected language.",
            "en-US-Neural2-F",
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "google_tts_params"));

        GoogleSpeakingRate = T2IParamTypes.Register<double>(new("Google Speaking Rate",
            "Speaking rate. 1.0 is the voice's natural speed.\nThe API accepts 0.25 to 2.0.",
            "1.0",
            Min: 0.25, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "google_tts_params"));

        GooglePitch = T2IParamTypes.Register<double>(new("Google Pitch",
            "Pitch offset in semitones.",
            "0.0",
            Min: -20.0, Max: 20.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "google_tts_params"));

        DeepgramVoice = T2IParamTypes.Register<string>(new("Deepgram Voice",
            "Deepgram Aura voice model.\nAura-2 is the newer generation; the Aura-1 voices remain available.",
            "aura-2-thalia-en",
            GetValues: _ => [
                "aura-2-thalia-en///Thalia (Aura-2)",
                "aura-2-andromeda-en///Andromeda (Aura-2)",
                "aura-2-helena-en///Helena (Aura-2)",
                "aura-2-apollo-en///Apollo (Aura-2)",
                "aura-2-arcas-en///Arcas (Aura-2)",
                "aura-2-asteria-en///Asteria (Aura-2)",
                "aura-2-athena-en///Athena (Aura-2)",
                "aura-2-atlas-en///Atlas (Aura-2)",
                "aura-2-aurora-en///Aurora (Aura-2)",
                "aura-2-cora-en///Cora (Aura-2)",
                "aura-2-draco-en///Draco (Aura-2)",
                "aura-2-electra-en///Electra (Aura-2)",
                "aura-2-hera-en///Hera (Aura-2)",
                "aura-2-hermes-en///Hermes (Aura-2)",
                "aura-2-iris-en///Iris (Aura-2)",
                "aura-2-juno-en///Juno (Aura-2)",
                "aura-2-jupiter-en///Jupiter (Aura-2)",
                "aura-2-luna-en///Luna (Aura-2)",
                "aura-2-mars-en///Mars (Aura-2)",
                "aura-2-minerva-en///Minerva (Aura-2)",
                "aura-2-neptune-en///Neptune (Aura-2)",
                "aura-2-orion-en///Orion (Aura-2)",
                "aura-2-orpheus-en///Orpheus (Aura-2)",
                "aura-2-phoebe-en///Phoebe (Aura-2)",
                "aura-2-saturn-en///Saturn (Aura-2)",
                "aura-2-selene-en///Selene (Aura-2)",
                "aura-2-theia-en///Theia (Aura-2)",
                "aura-2-vesta-en///Vesta (Aura-2)",
                "aura-2-zeus-en///Zeus (Aura-2)",
                "aura-asteria-en///Asteria (Aura-1)", "aura-luna-en///Luna (F)", "aura-stella-en///Stella (F)",
                "aura-athena-en///Athena (F)", "aura-hera-en///Hera (F)", "aura-orion-en///Orion (M)",
                "aura-arcas-en///Arcas (M)", "aura-perseus-en///Perseus (M)", "aura-angus-en///Angus (M)",
                "aura-orpheus-en///Orpheus (M)", "aura-helios-en///Helios (M)", "aura-zeus-en///Zeus (M)"],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "deepgram_tts_params"));

        CartesiaVoice = T2IParamTypes.Register<string>(new("Cartesia Voice ID",
            "Cartesia voice id from your voice library.",
            "",
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "cartesia_tts_params"));

        CartesiaModel = T2IParamTypes.Register<string>(new("Cartesia Model",
            "Cartesia Sonic model id.\nsonic-3.5 is the current API default.",
            "sonic-3.5",
            GetValues: _ => ["sonic-3.5///Sonic 3.5 (default)", "sonic-3///Sonic 3",
                "sonic-preview///Sonic Preview", "sonic-latest///Sonic Latest"],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "cartesia_tts_params"));

        CartesiaSpeed = T2IParamTypes.Register<double>(new("Cartesia Speed",
            "Speech speed via generation_config.\nThe API accepts 0.6x to 1.5x.",
            "1.0",
            Min: 0.6, Max: 1.5, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "cartesia_tts_params"));

        PlayHTVoice = T2IParamTypes.Register<string>(new("PlayHT Voice",
            "PlayHT voice id or manifest URL.",
            "",
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "playht_tts_params"));

        PlayHTQuality = T2IParamTypes.Register<string>(new("PlayHT Quality",
            "Output quality tier. Higher costs more and is slower.",
            "medium",
            GetValues: _ => ["draft///Draft", "low///Low", "medium///Medium", "high///High", "premium///Premium"],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "playht_tts_params"));

        PlayHTEngine = T2IParamTypes.Register<string>(new("PlayHT Voice Engine",
            "Synthesis engine. The API default is PlayHT2.0; the Play3.0/PlayDialog engines are newer.",
            "PlayHT2.0",
            GetValues: _ => ["PlayDialog-turbo///PlayDialog Turbo", "PlayDialog///PlayDialog",
                "Play3.0-mini///Play 3.0 Mini", "PlayHT2.0-turbo///PlayHT 2.0 Turbo",
                "PlayHT2.0///PlayHT 2.0 (default)", "PlayHT1.0///PlayHT 1.0"],
            OrderPriority: -8, Group: TTSGroup, FeatureFlag: "playht_tts_params"));

        PlayHTSpeed = T2IParamTypes.Register<double>(new("PlayHT Speed",
            "Speech speed. The API accepts 0.1 to 5.0.",
            "1.0",
            Min: 0.1, Max: 5.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: TTSGroup, FeatureFlag: "playht_tts_params"));

        DolbyPreset = T2IParamTypes.Register<string>(new("Dolby Enhance Preset",
            "Dolby.io Media Enhance content.type preset.\nNOTE: the public Media Enhance reference is currently unreachable, so this list could not be verified — an unsupported value will be rejected by the API.",
            "voice_over",
            GetValues: _ => [
                "voice_over///Voice Over", "conference///Conference", "interview///Interview",
                "lecture///Lecture", "meeting///Meeting", "mobile_phone///Mobile Phone",
                "music///Music", "podcast///Podcast", "studio///Studio"
            ],
            OrderPriority: -10, Group: AudioProcGroup, FeatureFlag: "dolby_audioproc_params"));

        ElevenSFXDuration = T2IParamTypes.Register<double>(new("SFX Duration",
            "Length of the generated sound effect, in seconds.\nThe API accepts 0.5-30; 0 lets it choose the optimal duration, which is the API default.",
            "0",
            Min: 0.0, Max: 30.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: AudioGenGroup, FeatureFlag: "elevenlabs_sfx_params"));

        ElevenSFXInfluence = T2IParamTypes.Register<double>(new("SFX Prompt Influence",
            "How literally the result follows the prompt. Higher = more literal, less creative.",
            "0.3",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "elevenlabs_sfx_params"));

        ElevenRemoveNoise = T2IParamTypes.Register<string>(new("Remove Background Noise",
            "Run the audio-isolation model on the input before converting.\nDocumented for Voice Changer only.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -10, Group: CloneGroup, FeatureFlag: "elevenlabs_vc_params"));

        #endregion

        #region TTS — Amazon Polly
        PollyEngine = T2IParamTypes.Register<string>(new("Polly Engine",
            "Polly synthesis engine. Neural sounds better; standard covers more voices.",
            "neural",
            GetValues: _ => ["neural///Neural", "standard///Standard", "long-form///Long Form", "generative///Generative"],
            OrderPriority: -10, Group: TTSGroup, FeatureFlag: "polly_tts_params"));

        PollyVoice = T2IParamTypes.Register<string>(new("Polly Voice",
            "Polly voice id. Each voice supports only some engines — the label lists which, so pair them correctly.",
            "Joanna",
            GetValues: _ => [
                "Joanna///Joanna (en-US F, neural/standard/generative)",
                "Matthew///Matthew (en-US M, neural/generative)",
                "Ruth///Ruth (en-US F, neural/long-form/generative)",
                "Stephen///Stephen (en-US M, neural/generative)",
                "Danielle///Danielle (en-US F, neural/long-form/generative)",
                "Gregory///Gregory (en-US M, neural/long-form)",
                "Patrick///Patrick (en-US M, long-form)",
                "Tiffany///Tiffany (en-US F, generative)",
                "Salli///Salli (en-US F, neural/standard/generative)",
                "Kimberly///Kimberly (en-US F, neural/standard)",
                "Kendra///Kendra (en-US F, neural/standard)",
                "Ivy///Ivy (en-US F child, neural/standard)",
                "Joey///Joey (en-US M, neural/standard)",
                "Justin///Justin (en-US M child, neural)",
                "Kevin///Kevin (en-US M child, neural/standard)",
                "Amy///Amy (en-GB F, neural/standard/generative)",
                "Emma///Emma (en-GB F, neural/standard)",
                "Brian///Brian (en-GB M, neural/standard/generative)",
                "Arthur///Arthur (en-GB M, neural)",
                "Olivia///Olivia (en-AU F, neural/generative)",
                "Nicole///Nicole (en-AU F, standard)",
                "Russell///Russell (en-AU M, standard)",
                "Aria///Aria (en-NZ F, neural/generative)",
                "Ayanda///Ayanda (en-ZA F, neural/generative)",
                "Niamh///Niamh (en-IE F, neural/generative)",
                "Kajal///Kajal (en-IN F, neural/generative)",
                "Raveena///Raveena (en-IN F, standard)"
            ],
            OrderPriority: -9, Group: TTSGroup, FeatureFlag: "polly_tts_params"));

        #endregion

        #region Music Shared
        Duration = T2IParamTypes.Register<double>(new("Max Duration",
            "Maximum duration of generated audio in seconds.\nThe actual output may be shorter depending on lyrics/content.\nLonger durations need more time and VRAM.",
            "30",
            Min: 1, Max: 300, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: AudioGenGroup, FeatureFlag: "audiolab_audiogen"));

        #endregion

        #region Music — AudioCraft Shared
        GuidanceScale = T2IParamTypes.Register<double>(new("Guidance Scale",
            "Classifier-free guidance for music/sound generation.\nHigher values increase prompt adherence.",
            "3.0",
            Min: 0.0, Max: 10.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "audiocraft_sampling", IsAdvanced: true));

        AudioCraftTemperature = T2IParamTypes.Register<double>(new("AudioCraft Temperature",
            "Sampling temperature for audio generation.\nHigher = more varied/creative. Lower = more predictable.",
            "1.0",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "audiocraft_sampling", IsAdvanced: true));

        AudioCraftTopK = T2IParamTypes.Register<int>(new("AudioCraft Top K",
            "Top-K token sampling for audio generation.\nLimits sampling to the K most likely tokens. 250 is the AudioCraft default.",
            "250",
            Min: 0, Max: 1000, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: AudioGenGroup, FeatureFlag: "audiocraft_sampling", IsAdvanced: true));

        AudioCraftTopP = T2IParamTypes.Register<double>(new("AudioCraft Top P",
            "Nucleus sampling for audio generation.\n0.0 = disabled (use Top K instead). Values > 0 enable nucleus sampling.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioGenGroup, FeatureFlag: "audiocraft_sampling", IsAdvanced: true));

        #endregion

        #region Music — ACE-Step Core
        Lyrics = T2IParamTypes.Register<string>(new("Lyrics",
            "Song lyrics for ACE-Step. Put one section tag on its own line, then that section's lines under it:\n"
            + "  [verse] [chorus] [bridge] [intro] [outro]\n"
            + "Use [Instrumental] (or leave empty) for an instrumental-only track.\n\n"
            + "GENRE / STYLE goes in the main Prompt box (NOT here), as COMMA-separated tags —\n"
            + "genre, mood, instruments, vocals, tempo. Example:\n"
            + "  pop, electronic, upbeat, female vocals, catchy melody, 120 bpm\n\n"
            + "EXAMPLE lyrics:\n  [Verse]\n  first verse lines\n  [Chorus]\n  the hook",
            "[Instrumental]",
            ViewType: ParamViewType.PROMPT,
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "acestep_music_params"));


        InferStep = T2IParamTypes.Register<int>(new("Infer Steps",
            "Denoising steps. 0 uses the checkpoint default.\nUpstream guidance: turbo 1-20 (8 recommended), base 1-200 (32-64 recommended).",
            "0",
            Min: 0, Max: 200, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "acestep_music_params", IsAdvanced: true));

        ACEGuidanceScale = T2IParamTypes.Register<double>(new("ACE Guidance",
            "Classifier-free guidance scale (documented range 1.0-15.0, default 7.0).\nNot shown for Turbo checkpoints — they bake guidance into distillation and run without CFG.",
            "7.0",
            Min: 1.0, Max: 15.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: AudioGenGroup, FeatureFlag: "acestep_cfg_params", IsAdvanced: true));

        // Shared across every music provider whose API takes an instrumental toggle (ACE-Step, Suno) —
        // one param on its own flag rather than a near-duplicate registered per provider.
        Instrumental = T2IParamTypes.Register<string>(new("Instrumental",
            "Generate instrumental-only track without vocals.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: -5, Group: AudioGenGroup, FeatureFlag: "music_instrumental_param"));

        // Shared by the cloud music providers that take a separate style/tags field. ACE-Step uses core's
        // Text2AudioStyle instead (it has the whole text2audio group), so it does NOT get this flag.
        MusicStyle = T2IParamTypes.Register<string>(new("Music Style",
            "Style / genre tags for the generated music, comma-separated.\nExample: pop, electronic, upbeat, female vocals",
            "",
            ViewType: ParamViewType.PROMPT,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "music_style_params"));

        BPM = T2IParamTypes.Register<int>(new("BPM",
            "Beats per minute (30-300).\n0 = auto-detect via the LM planner, matching upstream\'s default of none.",
            "0",
            Min: 0, Max: 300, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: AudioGenGroup, FeatureFlag: "acestep_music_params"));

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
            OrderPriority: -3, Group: AudioGenGroup, FeatureFlag: "acestep_music_params"));

        TimeSignature = T2IParamTypes.Register<string>(new("Time Signature",
            "Musical time signature (beats per measure).",
            "4",
            GetValues: _ => [
                "4///4/4 (Common Time)", "3///3/4 (Waltz)", "2///2/4 (March)", "6///6/8 (Compound)"
            ],
            OrderPriority: -2, Group: AudioGenGroup, FeatureFlag: "acestep_music_params"));

        VocalLanguage = T2IParamTypes.Register<string>(new("Vocal Language",
            "Language for generated vocals.\nUpstream defaults to auto-detect, letting the LM infer it from the lyrics.",
            "unknown",
            GetValues: _ => [
                "unknown///Auto-detect", 
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
            OrderPriority: -1, Group: AudioGenGroup, FeatureFlag: "acestep_music_params"));

        ACEShift = T2IParamTypes.Register<double>(new("Shift",
            "Timestep shift factor (documented range 1.0-5.0, default 1.0). 0 uses the checkpoint default.\nUpstream recommends 3.0 for turbo checkpoints, and it is NOT auto-corrected.",
            "0",
            Min: 0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: 0, Group: AudioGenGroup, FeatureFlag: "acestep_music_params", IsAdvanced: true));

        InferMethod = T2IParamTypes.Register<string>(new("Infer Method",
            "ODE solver method for diffusion inference.\nODE = deterministic. SDE = stochastic (more varied).",
            "ode",
            GetValues: _ => ["ode///ODE (Default)", "sde///SDE (Stochastic)"],
            OrderPriority: 1, Group: AudioGenGroup, FeatureFlag: "acestep_music_params", IsAdvanced: true));

        UseADG = T2IParamTypes.Register<string>(new("Use ADG",
            "Enable Adaptive Diffusion Guidance.\nCan improve prompt adherence for some models.",
            "false",
            GetValues: _ => ["false///No", "true///Yes"],
            OrderPriority: 2, Group: AudioGenGroup, FeatureFlag: "acestep_music_params", IsAdvanced: true));

        CFGIntervalStart = T2IParamTypes.Register<double>(new("CFG Interval Start",
            "Start of the CFG application interval.\n0.0 = apply from beginning of denoising.\nNot shown for Turbo checkpoints — they run without CFG.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: 3, Group: AudioGenGroup, FeatureFlag: "acestep_cfg_params", IsAdvanced: true));

        CFGIntervalEnd = T2IParamTypes.Register<double>(new("CFG Interval End",
            "End of the CFG application interval.\n1.0 = apply through end of denoising.\nNot shown for Turbo checkpoints — they run without CFG.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: 4, Group: AudioGenGroup, FeatureFlag: "acestep_cfg_params", IsAdvanced: true));

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
            OrderPriority: -10, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        Thinking = T2IParamTypes.Register<string>(new("LM Thinking",
            "Enable chain-of-thought reasoning in the LM planner.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        LMTemperature = T2IParamTypes.Register<double>(new("LM Temperature",
            "Sampling temperature for the LM planner.\nHigher = more creative metadata generation.",
            "0.85",
            Min: 0.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        LMCFGScale = T2IParamTypes.Register<double>(new("LM CFG Scale",
            "Classifier-free guidance scale for the LM planner.",
            "2.0",
            Min: 1.0, Max: 5.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        LMTopK = T2IParamTypes.Register<int>(new("LM Top K",
            "Top-K sampling for the LM planner.\n0 = disabled.",
            "0",
            Min: 0, Max: 500, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        LMTopP = T2IParamTypes.Register<double>(new("LM Top P",
            "Nucleus sampling threshold for the LM planner.",
            "0.9",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        LMNegativePrompt = T2IParamTypes.Register<string>(new("LM Negative Prompt",
            "Negative prompt for the LM planner.\nDescribes unwanted characteristics to avoid.",
            "",
            OrderPriority: -4, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        UseCotMetas = T2IParamTypes.Register<string>(new("CoT Metas",
            "Include meta tags (genre, mood, instruments) in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -3, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        UseCotCaption = T2IParamTypes.Register<string>(new("CoT Caption",
            "Include music description caption in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -2, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        UseCotLanguage = T2IParamTypes.Register<string>(new("CoT Language",
            "Include language detection in chain-of-thought.",
            "true",
            GetValues: _ => ["true///Yes", "false///No"],
            OrderPriority: -1, Group: AudioGenGroup, FeatureFlag: "acestep_lm_params", IsAdvanced: true));

        #endregion

        #region Music — ACE-Step Tasks
        ACETaskType = T2IParamTypes.Register<string>(new("Task Type",
            "ACE-Step generation task type.\ntext2music = generate from prompt. cover = style transfer.\nrepaint = regenerate a section. complete = extend/continue."
            + "\n'Extract Elements' and 'Lego (Combine)' are not listed — the Engine has no implementation for either yet (would silently fall back to plain text2music instead of erroring, which is worse than not offering them).",
            "text2music",
            GetValues: _ => [
                "text2music///Text to Music", "cover///Cover (Style Transfer)",
                "repaint///Repaint (Section Regen)", "complete///Complete (Extend)"
            ],
            OrderPriority: -10, Group: AudioGenGroup, FeatureFlag: "acestep_task_params"));

        ACESourceAudio = T2IParamTypes.Register<AudioFile>(new("ACE Source Audio",
            "Source audio for cover, repaint, and complete tasks.\nRequired for all tasks except text2music.",
            null,
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "acestep_task_params"));

        ACEReferenceAudio = T2IParamTypes.Register<AudioFile>(new("Style Reference Audio",
            "Optional style/timbre reference audio.\nThe generated music will match the style of this reference.",
            null,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "acestep_task_params"));

        RepaintStart = T2IParamTypes.Register<double>(new("Repaint Start",
            "Start time in seconds for repaint task.\nThe section from this point will be regenerated.",
            "0.0",
            Min: 0.0, Max: 600.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "acestep_task_params", IsAdvanced: true));

        RepaintEnd = T2IParamTypes.Register<double>(new("Repaint End",
            "End time in seconds for repaint task.\n-1 = auto (repaint to end of audio).",
            "-1.0",
            Min: -1.0, Max: 600.0, Step: 0.5, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: AudioGenGroup, FeatureFlag: "acestep_task_params", IsAdvanced: true));

        CoverStrength = T2IParamTypes.Register<double>(new("Cover Strength",
            "Style transfer strength for cover task.\n1.0 = full transfer. Lower = more of original.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioGenGroup, FeatureFlag: "acestep_task_params", IsAdvanced: true));

        CoverNoiseStrength = T2IParamTypes.Register<double>(new("Cover Noise",
            "Noise injection strength for cover task.\nAdds variation to the style transfer.",
            "0.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: AudioGenGroup, FeatureFlag: "acestep_task_params", IsAdvanced: true));

        #endregion

        #region Music — Stable Audio
        StableAudioSteps = T2IParamTypes.Register<int>(new("Stable Audio Steps",
            "Diffusion steps.\nThe official example uses 8; this is a distilled small model tuned for few steps.",
            "8",
            Min: 1, Max: 100, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -10, Group: AudioGenGroup, FeatureFlag: "stableaudio_music_params", IsAdvanced: true));

        #endregion

        #region Music — YuE
        YuELyrics = T2IParamTypes.Register<string>(new("YuE Lyrics",
            "Song lyrics for YuE. Structure them with SECTION MARKERS — each becomes its own generated segment:\n"
            + "  [verse] [chorus] [bridge] [intro] [outro]\n"
            + "Put one section tag on its own line, then that section's lines under it. Required — un-tagged\n"
            + "lyrics are treated as a single verse and tend to drift.\n\n"
            + "GENRE / STYLE goes in the main Prompt box (NOT here), as SPACE-separated tags (NO commas),\n"
            + "recommended order: genre, instrument, mood, gender, timbre. Example:\n"
            + "  inspiring female uplifting pop airy vocal electronic bright vocal\n"
            + "Include a gender tag (male / female) for vocals; more descriptive tags = better adherence.\n\n"
            + "EXAMPLE lyrics:\n  [verse]\n  first verse lines\n  [chorus]\n  the hook",
            "",
            ViewType: ParamViewType.PROMPT,
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "yue_music_params"));

        YuEMaxTokens = T2IParamTypes.Register<int>(new("Max Tokens",
            "Maximum new tokens for Stage-1 generation.\nControls output length. Higher = longer songs but slower.\n3000 ≈ ~30s of audio.",
            "3000",
            Min: 1000, Max: 12000, Step: 500, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuEQuantization = T2IParamTypes.Register<string>(new("Quantization",
            "Weight quantization for the Stage-1 LM.\nThis is a HartsyInference engine feature, not an upstream YuE flag.",
            "fp16",
            GetValues: _ => ["fp16///FP16 (Best Quality)", "8bit///8-bit (Balanced)", "4bit///4-bit (Low VRAM)"],
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuEStage2BatchSize = T2IParamTypes.Register<int>(new("Stage-2 Batch Size",
            "Batch size for Stage-2 refinement.\nLower = less VRAM but slower. Higher = faster but more VRAM.\nProcesses in 6-second chunks.",
            "4",
            Min: 1, Max: 32, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuETemperature = T2IParamTypes.Register<double>(new("YuE Temperature",
            "Sampling temperature for music generation.\nHigher = more creative/varied. Lower = more predictable.",
            "0.9",
            Min: 0.1, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuETopP = T2IParamTypes.Register<double>(new("YuE Top P",
            "Nucleus sampling threshold.\nLower values produce more focused, deterministic output.",
            "0.93",
            Min: 0.0, Max: 1.0, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuERepetitionPenalty = T2IParamTypes.Register<double>(new("YuE Repetition Penalty",
            "Penalty on repeated tokens.\nUpstream inference uses 1.1.",
            "1.1",
            Min: 1.0, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        YuESegments = T2IParamTypes.Register<int>(new("Segments",
            "Number of lyric segments to generate.\nMore segments = longer song. 0 = generate all segments from lyrics.",
            "2",
            Min: 0, Max: 10, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -1, Group: AudioGenGroup, FeatureFlag: "yue_music_params", IsAdvanced: true));

        #endregion

        #region Music — HeartLib
        HeartLibLyrics = T2IParamTypes.Register<string>(new("HeartLib Lyrics",
            "Song lyrics for HeartLib music generation.\n\n"
            + "SECTION MARKERS (only these 6 are recognized):\n"
            + "  [Intro] [Verse] [Prechorus] [Chorus] [Bridge] [Outro]\n"
            + "Repeat markers for multiple sections (e.g. two [Verse] blocks), do NOT number them ([Verse 1] won't work).\n"
            + "[Prechorus] is heavily used in training data — include it if your song has a pre-chorus.\n\n"
            + "INSTRUMENTAL SECTIONS:\n"
            + "  Use <||> under a section marker for sections without vocals.\n"
            + "  (Instrumental) is NOT recognized.\n\n"
            + "FORMATTING NOTES:\n"
            + "  - All text is lowercased internally, capitalization doesn't matter.\n"
            + "  - No inline controls for emphasis, yelling, pauses, or dynamics.\n"
            + "  - Vocal style is controlled globally via tags (Prompt), not lyrics.\n\n"
            + "GENRE / STYLE goes in the main Prompt box (NOT here) — genre, mood, instruments, and vocal\n"
            + "  style tags, e.g. 'pop, energetic, female vocal, bright synths, driving drums'.\n\n"
            + "EXAMPLE:\n"
            + "  [Intro]\n  <||>\n  [Verse]\n  your lyrics here\n  [Prechorus]\n  building up\n"
            + "  [Chorus]\n  main hook\n  [Verse]\n  second verse\n  [Bridge]\n  bridge lyrics\n"
            + "  [Chorus]\n  hook again\n  [Outro]\n  <||>",
            "",
            ViewType: ParamViewType.PROMPT,
            OrderPriority: -9, Group: AudioGenGroup, FeatureFlag: "heartlib_music_params"));

        HeartLibCFGScale = T2IParamTypes.Register<double>(new("HeartLib CFG Scale",
            "Classifier-free guidance strength.\nUpstream inference uses 1.5.",
            "1.5",
            Min: 0.1, Max: 10.0, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -8, Group: AudioGenGroup, FeatureFlag: "heartlib_music_params", IsAdvanced: true));

        HeartLibTemperature = T2IParamTypes.Register<double>(new("HeartLib Temperature",
            "Sampling temperature for music generation.\nHigher = more creative/varied. Lower = more predictable.",
            "1.0",
            Min: 0.1, Max: 2.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -7, Group: AudioGenGroup, FeatureFlag: "heartlib_music_params", IsAdvanced: true));

        HeartLibTopK = T2IParamTypes.Register<int>(new("HeartLib Top K",
            "Top-K token sampling limit.\nLower values produce more focused output. Higher values increase variety.",
            "50",
            Min: 1, Max: 500, Step: 10, ViewType: ParamViewType.SLIDER,
            OrderPriority: -6, Group: AudioGenGroup, FeatureFlag: "heartlib_music_params", IsAdvanced: true));

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
            "Pitch-extraction algorithm.\nNOTE: the in-process engine currently estimates F0 with YIN regardless of this setting; the other methods are listed for when they land and will fall back to YIN today.",
            "yin",
            GetValues: _ => [
                "yin///YIN (in use today)", "rmvpe///RMVPE (pending)", "pm///PM (pending)",
                "harvest///Harvest (pending)", "crepe///CREPE (pending)"
            ],
            OrderPriority: -4, Group: CloneGroup, FeatureFlag: "rvc_clone_params"));

        IndexRate = T2IParamTypes.Register<double>(new("Index Rate",
            "How much the index file influences the result.\nUpstream default is 0.75; lower values can reduce artifacts.\nNOTE: index retrieval is not implemented in the engine yet.",
            "0.75",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: CloneGroup, FeatureFlag: "rvc_clone_params", IsAdvanced: true));

        RMSMixRate = T2IParamTypes.Register<double>(new("RMS Mix Rate",
            "Volume envelope mixing ratio.\n1.0 = use original input volume. 0.0 = use model output volume.",
            "1.0",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: CloneGroup, FeatureFlag: "rvc_clone_params", IsAdvanced: true));

        Protect = T2IParamTypes.Register<double>(new("Protect",
            "Protects voiceless consonants and breath sounds.\nHigher values preserve more consonant detail. 0.5 = max protection.",
            "0.33",
            Min: 0.0, Max: 0.5, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -1, Group: CloneGroup, FeatureFlag: "rvc_clone_params", IsAdvanced: true));

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
            OrderPriority: -10, Group: AudioProcGroup, FeatureFlag: "audiolab_audioproc"));

        #endregion

        #region FX — Demucs
        Overlap = T2IParamTypes.Register<double>(new("Demucs Overlap",
            "Fractional overlap between processing segments.\nUpstream default is 0.25; its README notes this can be reduced to about 0.1 for a bit more speed.",
            "0.25",
            Min: 0.0, Max: 0.95, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioProcGroup, FeatureFlag: "demucs_fx_params", IsAdvanced: true));

        DemucsSegment = T2IParamTypes.Register<double>(new("Demucs Segment",
            "Length of each processing segment, in seconds.\nLower it if you run out of memory. Hybrid Transformer checkpoints support at most 7.8s, so larger values are clamped.",
            "7.8",
            Min: 1.0, Max: 7.8, Step: 0.1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -4, Group: AudioProcGroup, FeatureFlag: "demucs_fx_params", IsAdvanced: true));

        Shifts = T2IParamTypes.Register<int>(new("Demucs Shifts",
            "Demucs \"shift trick\" repetitions: separate several randomly time-shifted copies and average them.\nUpstream documents this as worth up to 0.2 SDR, and it makes the run exactly this many times slower. 0 = off.",
            "0",
            Min: 0, Max: 10, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: AudioProcGroup, FeatureFlag: "demucs_fx_params", IsAdvanced: true));

        #endregion

        #region FX — Resemble Enhance
        EnhanceNFE = T2IParamTypes.Register<int>(new("Enhancement Steps (NFE)",
            "CFM function-evaluation budget.\nUpstream's own interface exposes 1-128 with a default of 64. More = slower, generally cleaner.",
            "64",
            Min: 1, Max: 128, Step: 1, ViewType: ParamViewType.SLIDER,
            OrderPriority: -5, Group: AudioProcGroup, FeatureFlag: "resemble_enhance_fx_params", IsAdvanced: true));

        EnhanceSolver = T2IParamTypes.Register<string>(new("Solver",
            "ODE solver method for enhancement.\nMidpoint is recommended for best quality/speed balance.",
            "midpoint",
            GetValues: _ => [
                "midpoint///Midpoint (Recommended)", "euler///Euler", "rk4///RK4"
            ],
            OrderPriority: -4, Group: AudioProcGroup, FeatureFlag: "resemble_enhance_fx_params", IsAdvanced: true));

        EnhanceLambda = T2IParamTypes.Register<double>(new("Lambda (Denoise Blend)",
            "How much the denoiser output is blended in before enhancement.\nThis is NOT the temperature — that is Tau.",
            "0.1",
            Min: 0.0, Max: 1.0, Step: 0.01, ViewType: ParamViewType.SLIDER,
            OrderPriority: -3, Group: AudioProcGroup, FeatureFlag: "resemble_enhance_fx_params", IsAdvanced: true));

        EnhanceTau = T2IParamTypes.Register<double>(new("Tau (Prior Temperature)",
            "CFM prior temperature.\nUpstream's interface exposes 0-1 with a default of 0.5.",
            "0.5",
            Min: 0.0, Max: 1.0, Step: 0.05, ViewType: ParamViewType.SLIDER,
            OrderPriority: -2, Group: AudioProcGroup, FeatureFlag: "resemble_enhance_fx_params", IsAdvanced: true));

        #endregion

    }
}
