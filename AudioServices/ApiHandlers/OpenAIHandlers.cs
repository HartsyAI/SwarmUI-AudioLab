using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>OpenAI TTS handler — text-to-speech via OpenAI API (tts-1, tts-1-hd, gpt-4o-mini-tts).</summary>
public sealed class OpenAITTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string model = GetArg(args, "model_id", "tts-1");
        string voice = GetArg(args, "voice", "alloy");
        double speed = GetArgDouble(args, "speed", 1.0);
        string format = GetArg(args, "response_format", "wav");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Bearer {apiKey}" };
        JObject payload = new()
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice,
            ["speed"] = speed,
            ["response_format"] = format
        };
        try
        {
            byte[] audio = await PostForBytesAsync("https://api.openai.com/v1/audio/speech", payload, headers, cancel);
            return AudioResult(ToBase64(audio), format, 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"OpenAI TTS request failed: {ex.Message}");
        }
    }
}

/// <summary>OpenAI STT handler — speech-to-text via OpenAI API (whisper-1, gpt-4o-transcribe).</summary>
public sealed class OpenAISTTHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string model = GetArg(args, "model_id", "whisper-1");
        string language = GetArg(args, "language");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Bearer {apiKey}" };
        using MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent(audioData), "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrEmpty(language))
        {
            content.Add(new StringContent(language), "language");
        }
        JObject result = await PostMultipartForJsonAsync("https://api.openai.com/v1/audio/transcriptions", content, headers, cancel);
        if (IsError(result)) return result;
        string text = result["text"]?.ToString() ?? "";
        return SttResult(text, language);
    }
}
