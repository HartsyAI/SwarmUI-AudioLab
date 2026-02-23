namespace Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;

/// <summary>Categories of audio processing supported by the provider system.</summary>
public enum AudioCategory
{
    /// <summary>Speech-to-text transcription.</summary>
    STT,

    /// <summary>Text-to-speech synthesis.</summary>
    TTS,

    /// <summary>Music generation from text or other inputs.</summary>
    MusicGen,

    /// <summary>Voice cloning and voice style transfer.</summary>
    VoiceClone,

    /// <summary>Audio effects processing (reverb, pitch shift, etc.).</summary>
    AudioFX,

    /// <summary>Sound effects generation from text descriptions.</summary>
    SoundFX
}
