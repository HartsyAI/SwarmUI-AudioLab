using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Interface for cloud API engine handlers that process audio requests directly in C#.</summary>
public interface IApiEngineHandler
{
    /// <summary>Process a request using the cloud API. Returns a standard result JObject.</summary>
    Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default);
}

/// <summary>Base class for cloud API engine handlers with shared HTTP utilities.</summary>
public abstract class ApiEngineHandlerBase : IApiEngineHandler
{
    /// <summary>Shared HTTP client created via SwarmUI's standard factory.</summary>
    protected static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient(timeoutMinutes: 5);

    public abstract Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default);

    /// <summary>Gets a string arg value with a fallback default.</summary>
    protected static string GetArg(Dictionary<string, object> args, string key, string defaultValue = "")
        => args.TryGetValue(key, out object val) ? val?.ToString() ?? defaultValue : defaultValue;

    /// <summary>Gets a double arg value with a fallback default.</summary>
    protected static double GetArgDouble(Dictionary<string, object> args, string key, double defaultValue = 0)
        => args.TryGetValue(key, out object val) && double.TryParse(val?.ToString(), out double result) ? result : defaultValue;

    /// <summary>POST JSON and return the parsed response.</summary>
    protected async Task<JObject> PostJsonAsync(string url, JObject payload, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        string body = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            if (code == 401) return Error("Authentication failed. Check your API key in SwarmUI: Server tab > User Settings > API Keys.");
            if (code == 403) return Error("Access denied. Your API key may lack required permissions.");
            return Error($"API returned HTTP {code}: {body[..Math.Min(body.Length, 300)]}");
        }
        return JObject.Parse(body);
    }

    /// <summary>POST JSON and return raw audio bytes.</summary>
    protected async Task<byte[]> PostForBytesAsync(string url, JObject payload, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
        return await resp.Content.ReadAsByteArrayAsync(cancel);
    }

    /// <summary>POST raw bytes (for multipart/audio uploads) and return parsed JSON.</summary>
    protected async Task<JObject> PostBytesForJsonAsync(string url, byte[] data, Dictionary<string, string> headers, string contentType, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        string body = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            if (code == 401) return Error("Authentication failed. Check your API key.");
            return Error($"API returned HTTP {code}: {body[..Math.Min(body.Length, 300)]}");
        }
        return JObject.Parse(body);
    }

    /// <summary>POST multipart form data (for file uploads) and return raw bytes.</summary>
    protected async Task<byte[]> PostMultipartForBytesAsync(string url, MultipartFormDataContent content, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = content;
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
        return await resp.Content.ReadAsByteArrayAsync(cancel);
    }

    /// <summary>GET a URL and return parsed JSON.</summary>
    protected async Task<JObject> GetJsonAsync(string url, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        string body = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
        {
            return Error($"API returned HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
        return JObject.Parse(body);
    }

    /// <summary>GET a URL and return raw bytes.</summary>
    protected async Task<byte[]> GetBytesAsync(string url, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cancel);
    }

    /// <summary>Create a standard audio success response.</summary>
    protected static JObject AudioResult(string audioBase64, string format = "wav", int sampleRate = 24000, double duration = 0)
        => new()
        {
            ["success"] = true,
            ["audio_data"] = audioBase64,
            ["output_format"] = format,
            ["duration"] = duration,
            ["metadata"] = new JObject { ["sample_rate"] = sampleRate }
        };

    /// <summary>Create a standard STT success response.</summary>
    protected static JObject SttResult(string text, string language = "", double confidence = 1.0)
        => new()
        {
            ["success"] = true,
            ["text"] = text,
            ["confidence"] = confidence,
            ["metadata"] = new JObject { ["language"] = language }
        };

    /// <summary>Create a standard error response.</summary>
    protected static JObject Error(string message)
        => new() { ["success"] = false, ["error"] = message };

    /// <summary>Convert raw bytes to base64.</summary>
    protected static string ToBase64(byte[] data) => Convert.ToBase64String(data);

    /// <summary>POST multipart form data and return parsed JSON.</summary>
    protected async Task<JObject> PostMultipartForJsonAsync(string url, MultipartFormDataContent content, Dictionary<string, string> headers, CancellationToken cancel)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = content;
        foreach (KeyValuePair<string, string> h in headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        HttpResponseMessage resp = await Http.SendAsync(req, cancel);
        string body = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            if (code == 401) return Error("Authentication failed. Check your API key in SwarmUI: Server tab > User Settings > API Keys.");
            return Error($"API returned HTTP {code}: {body[..Math.Min(body.Length, 300)]}");
        }
        return JObject.Parse(body);
    }

    /// <summary>Check if a result JObject is an error response from the base helpers.</summary>
    protected static bool IsError(JObject result) => result["success"]?.Value<bool>() == false;

    /// <summary>Gets an int arg value with a fallback default.</summary>
    protected static int GetArgInt(Dictionary<string, object> args, string key, int defaultValue = 0)
        => args.TryGetValue(key, out object val) && int.TryParse(val?.ToString(), out int result) ? result : defaultValue;

    /// <summary>Gets a bool arg value with a fallback default.</summary>
    protected static bool GetArgBool(Dictionary<string, object> args, string key, bool defaultValue = false)
        => args.TryGetValue(key, out object val) && bool.TryParse(val?.ToString(), out bool result) ? result : defaultValue;

    /// <summary>Decode base64 audio data from args.</summary>
    protected static byte[] DecodeAudioArg(Dictionary<string, object> args)
    {
        string b64 = GetArg(args, "audio_data");
        return string.IsNullOrEmpty(b64) ? null : Convert.FromBase64String(b64);
    }
}
