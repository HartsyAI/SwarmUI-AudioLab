using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Static aggregator of all built-in audio providers.</summary>
public static class AudioProviderDefinitions
{
    /// <summary>All built-in provider sources that should be registered at startup.</summary>
    public static IReadOnlyList<IAudioProviderSource> All =>
    [
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
        Qwen3TTSProvider.Instance,
        PiperProvider.Instance,
        WhisperProvider.Instance,
        RealtimeSTTProvider.Instance,
        MoonshineProvider.Instance,
        DistilWhisperProvider.Instance,
        AceStepProvider.Instance,
        MusicGenProvider.Instance,
        OpenVoiceProvider.Instance,
        RVCProvider.Instance,
        GPTSoVITSProvider.Instance,
        DemucsProvider.Instance,
        ResembleEnhanceProvider.Instance,
        AudioGenProvider.Instance,
        YuEProvider.Instance,
        HeartLibProvider.Instance
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
