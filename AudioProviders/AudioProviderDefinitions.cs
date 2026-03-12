using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Static aggregator of all built-in audio providers.</summary>
public static class AudioProviderDefinitions
{
    /// <summary>All built-in provider sources that should be registered at startup.</summary>
    public static IReadOnlyList<IAudioProviderSource> All =>
    [
        // TTS providers
        ChatterboxProvider.Instance,
        BarkProvider.Instance,
        VibeVoiceProvider.Instance,
        OrpheusTTSProvider.Instance,
        KokoroProvider.Instance,
        DiaTTSProvider.Instance,
        F5TTSProvider.Instance,
        CSMProvider.Instance,
        ZonosProvider.Instance,
        CosyVoiceProvider.Instance,
        NeuTTSProvider.Instance,
        FishSpeechProvider.Instance,
        PiperProvider.Instance,
        // STT providers
        WhisperProvider.Instance,
        RealtimeSTTProvider.Instance,
        MoonshineProvider.Instance,
        DistilWhisperProvider.Instance,
        // Music generation providers
        AceStepProvider.Instance,
        MusicGenProvider.Instance,
        // Voice cloning providers
        OpenVoiceProvider.Instance,
        RVCProvider.Instance,
        GPTSoVITSProvider.Instance,
        // Audio FX providers
        DemucsProvider.Instance,
        ResembleEnhanceProvider.Instance,
        // Sound FX providers
        AudioGenProvider.Instance
    ];

    /// <summary>Registers all built-in providers with the AudioProviderRegistry.</summary>
    public static void RegisterAll()
    {
        foreach (IAudioProviderSource source in All)
        {
            AudioProviderRegistry.Register(source);
        }
    }
}
