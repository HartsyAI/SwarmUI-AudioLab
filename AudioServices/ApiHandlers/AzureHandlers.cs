using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Azure Neural TTS handler — text-to-speech via Azure Cognitive Services.
/// API key format: "subscription_key|region" (e.g. "abc123|eastus").</summary>
public sealed class AzureTTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        (string key, string region) = ParseAzureKey(apiKey);
        if (key == null) return Error("Azure API key must be in format: subscription_key|region (e.g. abc123|eastus). Set this in Server > User Settings > API Keys.");
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string voiceName = GetArg(args, "voice_name", "en-US-JennyNeural");
        string language = GetArg(args, "language", "en-US");
        string ssml = $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{language}'>
            <voice name='{voiceName}'>{System.Security.SecurityElement.Escape(text)}</voice>
        </speak>";
        Dictionary<string, string> headers = new()
        {
            ["Ocp-Apim-Subscription-Key"] = key,
            ["Content-Type"] = "application/ssml+xml",
            ["X-Microsoft-OutputFormat"] = "riff-24khz-16bit-mono-pcm"
        };
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
            req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
            foreach (KeyValuePair<string, string> h in headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            HttpResponseMessage resp = await Http.SendAsync(req, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(cancel);
                return Error($"Azure TTS HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            }
            byte[] audio = await resp.Content.ReadAsByteArrayAsync(cancel);
            return AudioResult(ToBase64(audio), "wav", 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"Azure TTS request failed: {ex.Message}");
        }
    }

    private static (string key, string region) ParseAzureKey(string apiKey)
    {
        string[] parts = apiKey.Split('|');
        return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
    }
}

/// <summary>Azure Speech STT handler — speech-to-text via Azure Cognitive Services.
/// API key format: "subscription_key|region" (e.g. "abc123|eastus").</summary>
public sealed class AzureSTTHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        (string key, string region) = ParseAzureKey(apiKey);
        if (key == null) return Error("Azure API key must be in format: subscription_key|region (e.g. abc123|eastus). Set this in Server > User Settings > API Keys.");
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string language = GetArg(args, "language", "en-US");
        string url = $"https://{region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={language}&format=detailed";
        Dictionary<string, string> headers = new()
        {
            ["Ocp-Apim-Subscription-Key"] = key
        };
        JObject result = await PostBytesForJsonAsync(url, audioData, headers, "audio/wav", cancel);
        if (IsError(result)) return result;
        string status = result["RecognitionStatus"]?.ToString();
        if (status != "Success") return SttResult("", language, 0);
        string text = result["DisplayText"]?.ToString() ?? "";
        double confidence = result["NBest"]?[0]?["Confidence"]?.Value<double>() ?? 0;
        return SttResult(text, language, confidence);
    }

    private static (string key, string region) ParseAzureKey(string apiKey)
    {
        string[] parts = apiKey.Split('|');
        return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
    }
}
