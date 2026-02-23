using Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;

namespace Hartsy.Extensions.VoiceAssistant.AudioProviders;

/// <summary>Static aggregator of all built-in audio providers.</summary>
public static class AudioProviderDefinitions
{
    /// <summary>All built-in provider sources that should be registered at startup.</summary>
    public static IReadOnlyList<IAudioProviderSource> All =>
    [
        ChatterboxProvider.Instance,
        BarkProvider.Instance,
        WhisperProvider.Instance,
        RealtimeSTTProvider.Instance,
        FallbackTTSProvider.Instance,
        FallbackSTTProvider.Instance
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
