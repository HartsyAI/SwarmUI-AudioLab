namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Categories of audio processing supported by the provider system.</summary>
public enum AudioCategory
{
    /// <summary>Speech-to-text transcription.</summary>
    STT,

    /// <summary>Text-to-speech synthesis.</summary>
    TTS,

    /// <summary>Audio generation from text — music (MusicGen, ACE-Step) and sound effects (AudioGen).</summary>
    AudioGeneration,

    /// <summary>Voice conversion — transforms the voice in existing audio (RVC, OpenVoice) or generates speech in a cloned voice (GPT-SoVITS).</summary>
    VoiceConversion,

    /// <summary>Audio processing — transforms existing audio (stem separation, denoising, enhancement).</summary>
    AudioProcessing
}
