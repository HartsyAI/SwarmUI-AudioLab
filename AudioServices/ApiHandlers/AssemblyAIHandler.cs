using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>AssemblyAI STT handler — speech-to-text with diarization and sentiment analysis.
/// Uses async upload + polling pattern.</summary>
public sealed class AssemblyAIHandler : ApiEngineHandlerBase
{
    private const string BaseUrl = "https://api.assemblyai.com/v2";

    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string language = GetArg(args, "language_code", "en");
        Dictionary<string, string> headers = new() { ["Authorization"] = apiKey };
        // Step 1: Upload audio
        JObject uploadResult = await PostBytesForJsonAsync($"{BaseUrl}/upload", audioData, headers, "application/octet-stream", cancel);
        if (IsError(uploadResult)) return uploadResult;
        string uploadUrl = uploadResult["upload_url"]?.ToString();
        if (string.IsNullOrEmpty(uploadUrl)) return Error("AssemblyAI upload failed: no upload URL returned.");
        // Step 2: Create transcript
        JObject transcriptPayload = new()
        {
            ["audio_url"] = uploadUrl,
            ["language_code"] = language,
            ["punctuate"] = true,
            ["format_text"] = true
        };
        JObject transcript = await PostJsonAsync($"{BaseUrl}/transcript", transcriptPayload, headers, cancel);
        if (IsError(transcript)) return transcript;
        string transcriptId = transcript["id"]?.ToString();
        if (string.IsNullOrEmpty(transcriptId)) return Error("AssemblyAI failed: no transcript ID returned.");
        // Step 3: Poll for completion (max 5 minutes)
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(2000, cancel);
            JObject status = await GetJsonAsync($"{BaseUrl}/transcript/{transcriptId}", headers, cancel);
            if (IsError(status)) return status;
            string state = status["status"]?.ToString();
            if (state == "completed")
            {
                string text = status["text"]?.ToString() ?? "";
                double confidence = status["confidence"]?.Value<double>() ?? 0;
                return SttResult(text, language, confidence);
            }
            if (state == "error")
            {
                return Error($"AssemblyAI transcription failed: {status["error"]?.ToString()}");
            }
        }
        return Error("AssemblyAI transcription timed out after 5 minutes.");
    }
}
