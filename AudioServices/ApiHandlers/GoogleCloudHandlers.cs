using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Google Cloud TTS handler — text-to-speech via Google Cloud Text-to-Speech API.</summary>
public sealed class GoogleCloudTTSHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string languageCode = GetArg(args, "language_code", "en-US");
        string voiceName = GetArg(args, "voice_name", "en-US-Neural2-F");
        double speakingRate = GetArgDouble(args, "speaking_rate", 1.0);
        double pitch = GetArgDouble(args, "pitch", 0.0);
        Dictionary<string, string> headers = new();
        JObject payload = new()
        {
            ["input"] = new JObject { ["text"] = text },
            ["voice"] = new JObject
            {
                ["languageCode"] = languageCode,
                ["name"] = voiceName
            },
            ["audioConfig"] = new JObject
            {
                ["audioEncoding"] = "LINEAR16",
                ["speakingRate"] = speakingRate,
                ["pitch"] = pitch,
                ["sampleRateHertz"] = 24000
            }
        };
        JObject result = await PostJsonAsync($"https://texttospeech.googleapis.com/v1/text:synthesize?key={apiKey}", payload, headers, cancel);
        if (IsError(result)) return result;
        string audioContent = result["audioContent"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(audioContent)) return Error("Google Cloud TTS returned no audio data.");
        return AudioResult(audioContent, "wav", 24000);
    }
}

/// <summary>Google Cloud STT handler — speech-to-text via Google Cloud Speech-to-Text API.</summary>
public sealed class GoogleCloudSTTHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string languageCode = GetArg(args, "language_code", "en-US");
        string model = GetArg(args, "model_id", "latest_long");
        Dictionary<string, string> headers = new();
        JObject payload = new()
        {
            ["config"] = new JObject
            {
                ["encoding"] = "LINEAR16",
                ["sampleRateHertz"] = 16000,
                ["languageCode"] = languageCode,
                ["model"] = model,
                ["enableAutomaticPunctuation"] = true
            },
            ["audio"] = new JObject { ["content"] = ToBase64(audioData) }
        };
        JObject result = await PostJsonAsync($"https://speech.googleapis.com/v1/speech:recognize?key={apiKey}", payload, headers, cancel);
        if (IsError(result)) return result;
        JArray results = result["results"] as JArray;
        if (results == null || results.Count == 0) return SttResult("", languageCode, 0);
        string transcript = "";
        double confidence = 0;
        foreach (JToken r in results)
        {
            JArray alts = r["alternatives"] as JArray;
            if (alts != null && alts.Count > 0)
            {
                transcript += alts[0]["transcript"]?.ToString() ?? "";
                confidence = alts[0]["confidence"]?.Value<double>() ?? 0;
            }
        }
        return SttResult(transcript.Trim(), languageCode, confidence);
    }
}
