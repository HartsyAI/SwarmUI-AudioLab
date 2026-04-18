using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Static aggregator of all built-in audio providers.</summary>
public static class AudioProviderDefinitions
{
    /// <summary>All built-in provider sources that should be registered at startup.</summary>
    public static IReadOnlyList<IAudioProviderSource> All =>
    [
        // Local TTS providers
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
        PocketTTSProvider.Instance,
        KyutaiTTSProvider.Instance,
        FishSpeechProvider.Instance,
        Qwen3TTSProvider.Instance,
        PiperProvider.Instance,
        // Local STT providers
        WhisperProvider.Instance,
        KyutaiSTTProvider.Instance,
        RealtimeSTTProvider.Instance,
        MoonshineProvider.Instance,
        DistilWhisperProvider.Instance,
        // Local audio generation providers
        AceStepProvider.Instance,
        MusicGenProvider.Instance,
        AudioGenProvider.Instance,
        YuEProvider.Instance,
        // Local voice conversion providers
        OpenVoiceProvider.Instance,
        RVCProvider.Instance,
        GPTSoVITSProvider.Instance,
        // Local audio processing providers
        DemucsProvider.Instance,
        ResembleEnhanceProvider.Instance,
        HeartLibProvider.Instance,
        // API TTS providers
        ElevenLabsTTSProvider.Instance,
        OpenAITTSProvider.Instance,
        GoogleCloudTTSProvider.Instance,
        AzureTTSProvider.Instance,
        AmazonPollyProvider.Instance,
        DeepgramTTSProvider.Instance,
        CartesiaTTSProvider.Instance,
        PlayHTProvider.Instance,
        // API STT providers
        OpenAISTTProvider.Instance,
        GoogleCloudSTTProvider.Instance,
        AzureSTTProvider.Instance,
        AWSTranscribeProvider.Instance,
        AssemblyAIProvider.Instance,
        DeepgramSTTProvider.Instance,
        // API audio generation providers
        ElevenLabsSFXProvider.Instance,
        SunoMusicProvider.Instance,
        UdioMusicProvider.Instance,
        // API voice conversion providers
        ElevenLabsVoiceChangerProvider.Instance,
        // API audio processing providers
        ElevenLabsVoiceIsolatorProvider.Instance,
        DolbyIOProvider.Instance
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
