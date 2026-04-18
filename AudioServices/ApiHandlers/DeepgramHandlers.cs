using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Deepgram Aura TTS handler — text-to-speech via Deepgram API.</summary>
public sealed class DeepgramTTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string model = GetArg(args, "model_id", "aura-asteria-en");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Token {apiKey}" };
        JObject payload = new() { ["text"] = text };
        try
        {
            byte[] audio = await PostForBytesAsync($"https://api.deepgram.com/v1/speak?model={model}", payload, headers, cancel);
            return AudioResult(ToBase64(audio), "mp3", 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"Deepgram TTS request failed: {ex.Message}");
        }
    }
}

/// <summary>Deepgram Nova-3 STT handler — speech-to-text via Deepgram API.</summary>
public sealed class DeepgramSTTHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string model = GetArg(args, "model_id", "nova-3");
        string language = GetArg(args, "language", "en");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Token {apiKey}" };
        JObject result = await PostBytesForJsonAsync(
            $"https://api.deepgram.com/v1/listen?model={model}&language={language}&punctuate=true",
            audioData, headers, "audio/wav", cancel);
        if (IsError(result)) return result;
        string transcript = result["results"]?["channels"]?[0]?["alternatives"]?[0]?["transcript"]?.ToString() ?? "";
        double confidence = result["results"]?["channels"]?[0]?["alternatives"]?[0]?["confidence"]?.Value<double>() ?? 0;
        return SttResult(transcript, language, confidence);
    }
}
