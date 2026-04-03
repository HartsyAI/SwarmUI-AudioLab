namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Maps audio provider IDs to their C# API handler implementations.
/// Used by AudioServerManager to route API-based providers directly to C# HTTP handlers
/// instead of going through Python.</summary>
public static class ApiHandlerRegistry
{
    private static readonly Dictionary<string, IApiEngineHandler> Handlers = new()
    {
        // ElevenLabs suite (1 API key)
        ["elevenlabs_tts"] = new ElevenLabsTTSHandler(),
        ["elevenlabs_sfx"] = new ElevenLabsSFXHandler(),
        ["elevenlabs_voice_changer"] = new ElevenLabsVCHandler(),
        ["elevenlabs_voice_isolator"] = new ElevenLabsIsolatorHandler(),
        // OpenAI suite
        ["openai_tts"] = new OpenAITTSHandler(),
        ["openai_stt"] = new OpenAISTTHandler(),
        // Google Cloud suite
        ["google_cloud_tts"] = new GoogleCloudTTSHandler(),
        ["google_cloud_stt"] = new GoogleCloudSTTHandler(),
        // Azure suite
        ["azure_tts"] = new AzureTTSHandler(),
        ["azure_stt"] = new AzureSTTHandler(),
        // AWS suite
        ["amazon_polly"] = new AmazonPollyHandler(),
        ["aws_transcribe"] = new AWSTranscribeHandler(),
        // Deepgram suite
        ["deepgram_tts"] = new DeepgramTTSHandler(),
        ["deepgram_stt"] = new DeepgramSTTHandler(),
        // Standalone providers
        ["assemblyai_stt"] = new AssemblyAIHandler(),
        ["cartesia_tts"] = new CartesiaTTSHandler(),
        ["playht_tts"] = new PlayHTTTSHandler(),
        ["suno_music"] = new SunoMusicHandler(),
        ["udio_music"] = new UdioMusicHandler(),
        ["dolby_audioproc"] = new DolbyIOHandler(),
    };

    /// <summary>Gets the C# API handler for a provider, or null if not found.</summary>
    public static IApiEngineHandler GetHandler(string providerId)
        => Handlers.TryGetValue(providerId, out IApiEngineHandler handler) ? handler : null;

    /// <summary>Checks if a provider has a registered C# API handler.</summary>
    public static bool HasHandler(string providerId)
        => Handlers.ContainsKey(providerId);
}
