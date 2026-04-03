using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Cartesia Sonic TTS handler — ultra-low latency text-to-speech.</summary>
public sealed class CartesiaTTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string modelId = GetArg(args, "model_id", "sonic-2");
        string voiceId = GetArg(args, "voice_id", "a0e99841-438c-4a64-b679-ae501e7d6091");
        string language = GetArg(args, "language", "en");
        Dictionary<string, string> headers = new()
        {
            ["X-API-Key"] = apiKey,
            ["Cartesia-Version"] = "2024-06-10"
        };
        JObject payload = new()
        {
            ["model_id"] = modelId,
            ["transcript"] = text,
            ["voice"] = new JObject
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["language"] = language,
            ["output_format"] = new JObject
            {
                ["container"] = "wav",
                ["encoding"] = "pcm_s16le",
                ["sample_rate"] = 24000
            }
        };
        try
        {
            byte[] audio = await PostForBytesAsync("https://api.cartesia.ai/tts/bytes", payload, headers, cancel);
            return AudioResult(ToBase64(audio), "wav", 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"Cartesia TTS request failed: {ex.Message}");
        }
    }
}
