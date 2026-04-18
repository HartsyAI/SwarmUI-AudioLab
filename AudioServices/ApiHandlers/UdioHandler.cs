using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Udio Music handler — AI music generation via Udio API.
/// Uses async generation + polling pattern.</summary>
public sealed class UdioMusicHandler : ApiEngineHandlerBase
{
    private const string BaseUrl = "https://www.udio.com/api";

    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        string prompt = GetArg(args, "text");
        if (string.IsNullOrEmpty(prompt)) return Error("No prompt provided.");
        string style = GetArg(args, "style", "");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Bearer {apiKey}" };
        JObject payload = new()
        {
            ["prompt"] = prompt
        };
        if (!string.IsNullOrEmpty(style))
        {
            payload["tags"] = style;
        }
        // Step 1: Submit generation
        JObject genResult = await PostJsonAsync($"{BaseUrl}/generate-proxy", payload, headers, cancel);
        if (IsError(genResult)) return genResult;
        JArray trackIds = genResult["track_ids"] as JArray;
        if (trackIds == null || trackIds.Count == 0) return Error("Udio returned no track IDs. Check your API key and account status.");
        string trackId = trackIds[0]?.ToString();
        if (string.IsNullOrEmpty(trackId)) return Error("Udio returned no track ID.");
        // Step 2: Poll for completion (max 5 minutes)
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(2000, cancel);
            JObject status = await GetJsonAsync($"{BaseUrl}/songs?songIds={trackId}", headers, cancel);
            if (IsError(status)) return status;
            JArray songs = status["songs"] as JArray;
            if (songs == null || songs.Count == 0) continue;
            JObject song = songs[0] as JObject;
            string state = song?["finished"]?.Value<bool>() == true ? "complete" : "processing";
            if (state == "complete")
            {
                string audioUrl = song["song_path"]?.ToString();
                if (string.IsNullOrEmpty(audioUrl)) continue;
                try
                {
                    byte[] audio = await GetBytesAsync(audioUrl, new Dictionary<string, string>(), cancel);
                    double duration = song["duration"]?.Value<double>() ?? 0;
                    return AudioResult(ToBase64(audio), "mp3", 44100, duration);
                }
                catch
                {
                    continue;
                }
            }
            string errorMsg = song?["error_message"]?.ToString();
            if (!string.IsNullOrEmpty(errorMsg))
            {
                return Error($"Udio generation failed: {errorMsg}");
            }
        }
        return Error("Udio generation timed out after 5 minutes.");
    }
}
