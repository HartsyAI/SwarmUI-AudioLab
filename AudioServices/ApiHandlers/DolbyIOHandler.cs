using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Dolby.io Audio Processing handler — professional enhance, noise reduction, and mastering.
/// Uses async upload + process + download pattern.</summary>
public sealed class DolbyIOHandler : ApiEngineHandlerBase
{
    private const string BaseUrl = "https://api.dolby.com/media";

    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string preset = GetArg(args, "preset", "voice_over");
        Dictionary<string, string> headers = new() { ["Authorization"] = $"Bearer {apiKey}" };
        // Step 1: Get presigned upload URL
        JObject uploadReq = new() { ["url"] = "dlb://input.wav" };
        JObject uploadResp = await PostJsonAsync($"{BaseUrl}/input", uploadReq, headers, cancel);
        if (IsError(uploadResp)) return uploadResp;
        string presignedUrl = uploadResp["url"]?.ToString();
        if (string.IsNullOrEmpty(presignedUrl)) return Error("Dolby.io returned no upload URL.");
        // Step 2: Upload audio to presigned URL
        try
        {
            using HttpRequestMessage putReq = new(HttpMethod.Put, presignedUrl);
            putReq.Content = new ByteArrayContent(audioData);
            putReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            HttpResponseMessage putResp = await Http.SendAsync(putReq, cancel);
            if (!putResp.IsSuccessStatusCode)
            {
                return Error($"Dolby.io upload failed: HTTP {(int)putResp.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            return Error($"Dolby.io upload failed: {ex.Message}");
        }
        // Step 3: Start enhance job
        JObject enhancePayload = new()
        {
            ["input"] = "dlb://input.wav",
            ["output"] = "dlb://output.wav",
            ["content"] = new JObject { ["type"] = preset }
        };
        JObject enhanceResp = await PostJsonAsync($"{BaseUrl}/enhance", enhancePayload, headers, cancel);
        if (IsError(enhanceResp)) return enhanceResp;
        string jobId = enhanceResp["job_id"]?.ToString();
        if (string.IsNullOrEmpty(jobId)) return Error("Dolby.io returned no job ID.");
        // Step 4: Poll for completion (max 5 minutes)
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(2000, cancel);
            JObject status = await GetJsonAsync($"{BaseUrl}/enhance?job_id={jobId}", headers, cancel);
            if (IsError(status)) return status;
            string state = status["status"]?.ToString();
            if (state == "Success")
            {
                // Step 5: Get download URL
                JObject outputReq = new() { ["url"] = "dlb://output.wav" };
                JObject outputResp = await PostJsonAsync($"{BaseUrl}/output", outputReq, headers, cancel);
                if (IsError(outputResp)) return outputResp;
                string downloadUrl = outputResp["url"]?.ToString();
                if (string.IsNullOrEmpty(downloadUrl)) return Error("Dolby.io returned no download URL.");
                byte[] result = await GetBytesAsync(downloadUrl, new Dictionary<string, string>(), cancel);
                return AudioResult(ToBase64(result), "wav", 48000);
            }
            if (state == "Failed")
            {
                return Error($"Dolby.io enhance failed: {status["error"]?.ToString() ?? "unknown error"}");
            }
        }
        return Error("Dolby.io enhance timed out after 5 minutes.");
    }
}
