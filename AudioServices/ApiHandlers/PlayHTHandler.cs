using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Play.ht TTS handler — high-quality text-to-speech with voice cloning.
/// API key format: "api_key|user_id".</summary>
public sealed class PlayHTTTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        (string key, string userId) = ParsePlayHTKey(apiKey);
        if (key == null) return Error("Play.ht API key must be in format: api_key|user_id. Set this in Server > User Settings > API Keys.");
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string voice = GetArg(args, "voice", "s3://voice-cloning-zero-shot/775ae416-49bb-4fb6-bd45-740f205d3559/sadfranksaad/manifest.json");
        string quality = GetArg(args, "quality", "premium");
        double speed = GetArgDouble(args, "speed", 1.0);
        Dictionary<string, string> headers = new()
        {
            ["Authorization"] = $"Bearer {key}",
            ["X-USER-ID"] = userId
        };
        JObject payload = new()
        {
            ["text"] = text,
            ["voice"] = voice,
            ["quality"] = quality,
            ["output_format"] = "mp3",
            ["speed"] = speed,
            ["voice_engine"] = "PlayHT2.0-turbo"
        };
        try
        {
            byte[] audio = await PostForBytesAsync("https://api.play.ht/api/v2/tts/stream", payload, headers, cancel);
            return AudioResult(ToBase64(audio), "mp3", 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"Play.ht TTS request failed: {ex.Message}");
        }
    }

    private static (string key, string userId) ParsePlayHTKey(string apiKey)
    {
        string[] parts = apiKey.Split('|');
        return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
    }
}
