using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Suno Music handler — AI music generation with vocals and lyrics via Suno API.
/// Uses async generation + polling pattern.</summary>
public sealed class SunoMusicHandler : ApiEngineHandlerBase
{
    private const string BaseUrl = "https://studio-api.suno.ai/api";

    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string prompt = GetArg(args, "text");
        if (string.IsNullOrEmpty(prompt)) return Error("No prompt provided.");
        string style = GetArg(args, "style", "");
        bool instrumental = GetArgBool(args, "instrumental", false);
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Bearer {apiKey}" };
        JObject payload = new()
        {
            ["gpt_description_prompt"] = prompt,
            ["make_instrumental"] = instrumental,
            ["mv"] = "chirp-v4"
        };
        if (!string.IsNullOrEmpty(style))
        {
            payload["tags"] = style;
        }
        // Step 1: Submit generation
        JObject genResult = await PostJsonAsync($"{BaseUrl}/generate/v2/", payload, headers, cancel);
        if (IsError(genResult)) return genResult;
        JArray clips = genResult["clips"] as JArray;
        if (clips == null || clips.Count == 0) return Error("Suno returned no clips. Check your API key and account status.");
        string clipId = clips[0]["id"]?.ToString();
        if (string.IsNullOrEmpty(clipId)) return Error("Suno returned no clip ID.");
        // Step 2: Poll for completion (max 5 minutes)
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(2000, cancel);
            JObject status = await GetJsonAsync($"{BaseUrl}/feed/{clipId}", headers, cancel);
            if (IsError(status)) return status;
            JArray items = status["clips"] as JArray;
            JObject clip = items?[0] as JObject ?? status;
            string state = clip["status"]?.ToString();
            if (state == "complete" || state == "streaming")
            {
                string audioUrl = clip["audio_url"]?.ToString();
                if (string.IsNullOrEmpty(audioUrl)) continue;
                try
                {
                    byte[] audio = await GetBytesAsync(audioUrl, new Dictionary<string, string>(), cancel);
                    double duration = clip["metadata"]?["duration"]?.Value<double>() ?? 0;
                    return AudioResult(ToBase64(audio), "mp3", 44100, duration);
                }
                catch
                {
                    continue;
                }
            }
            if (state == "error")
            {
                return Error($"Suno generation failed: {clip["error_message"]?.ToString() ?? "unknown error"}");
            }
        }
        return Error("Suno generation timed out after 5 minutes.");
    }
}
