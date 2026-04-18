using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>ElevenLabs TTS handler — text-to-speech via ElevenLabs API.</summary>
public sealed class ElevenLabsTTSHandler : ApiEngineHandlerBase
{
    private const string BaseUrl = "https://api.elevenlabs.io/v1";

    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string voiceId = GetArg(args, "voice_id", "21m00Tcm4TlvDq8ikWAM");
        string modelId = GetArg(args, "model_id", "eleven_multilingual_v2");
        double stability = GetArgDouble(args, "stability", 0.5);
        double similarity = GetArgDouble(args, "similarity_boost", 0.75);
        Dictionary<string, string> headers = new() { ["xi-api-key"] = apiKey };
        JObject payload = new()
        {
            ["text"] = text,
            ["model_id"] = modelId,
            ["voice_settings"] = new JObject { ["stability"] = stability, ["similarity_boost"] = similarity }
        };
        try
        {
            byte[] audio = await PostForBytesAsync($"{BaseUrl}/text-to-speech/{voiceId}", payload, headers, cancel);
            return AudioResult(ToBase64(audio), "mp3", 44100);
        }
        catch (HttpRequestException ex)
        {
            return Error($"ElevenLabs TTS request failed: {ex.Message}");
        }
    }
}

/// <summary>ElevenLabs Sound Effects handler — text-to-SFX generation.</summary>
public sealed class ElevenLabsSFXHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No prompt provided.");
        double duration = GetArgDouble(args, "duration_seconds", 5.0);
        double influence = GetArgDouble(args, "prompt_influence", 0.3);
        Dictionary<string, string> headers = new() { ["xi-api-key"] = apiKey };
        JObject payload = new()
        {
            ["text"] = text,
            ["duration_seconds"] = duration,
            ["prompt_influence"] = influence
        };
        try
        {
            byte[] audio = await PostForBytesAsync("https://api.elevenlabs.io/v1/sound-generation", payload, headers, cancel);
            return AudioResult(ToBase64(audio), "mp3", 44100);
        }
        catch (HttpRequestException ex)
        {
            return Error($"ElevenLabs SFX request failed: {ex.Message}");
        }
    }
}

/// <summary>ElevenLabs Voice Changer handler — speech-to-speech voice conversion.</summary>
public sealed class ElevenLabsVCHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string voiceId = GetArg(args, "voice_id", "21m00Tcm4TlvDq8ikWAM");
        string modelId = GetArg(args, "model_id", "eleven_english_sts_v2");
        Dictionary<string, string> headers = new() { ["xi-api-key"] = apiKey };
        using MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent(audioData), "audio", "audio.wav");
        content.Add(new StringContent(modelId), "model_id");
        try
        {
            byte[] result = await PostMultipartForBytesAsync($"https://api.elevenlabs.io/v1/speech-to-speech/{voiceId}", content, headers, cancel);
            return AudioResult(ToBase64(result), "mp3", 44100);
        }
        catch (HttpRequestException ex)
        {
            return Error($"ElevenLabs voice conversion failed: {ex.Message}");
        }
    }
}

/// <summary>ElevenLabs Voice Isolator handler — isolate vocals from audio.</summary>
public sealed class ElevenLabsIsolatorHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        Dictionary<string, string> headers = new() { ["xi-api-key"] = apiKey };
        using MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent(audioData), "audio", "audio.wav");
        try
        {
            byte[] result = await PostMultipartForBytesAsync("https://api.elevenlabs.io/v1/audio-isolation", content, headers, cancel);
            return AudioResult(ToBase64(result), "mp3", 44100);
        }
        catch (HttpRequestException ex)
        {
            return Error($"ElevenLabs voice isolation failed: {ex.Message}");
        }
    }
}
