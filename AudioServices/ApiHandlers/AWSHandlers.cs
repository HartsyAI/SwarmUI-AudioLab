using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Amazon Polly handler — text-to-speech via AWS Polly.
/// API key format: "access_key_id|secret_access_key|region" (e.g. "AKIA...|wJalr...|us-east-1").</summary>
public sealed class AmazonPollyHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        (string accessKey, string secretKey, string region) = ParseAwsKey(apiKey);
        if (accessKey == null) return Error("AWS API key must be in format: access_key_id|secret_access_key|region (e.g. AKIA...|wJalr...|us-east-1). Set this in Server > User Settings > API Keys.");
        string text = GetArg(args, "text");
        if (string.IsNullOrEmpty(text)) return Error("No text provided.");
        string voiceId = GetArg(args, "voice_id", "Joanna");
        string engine = GetArg(args, "engine", "neural");
        string format = GetArg(args, "output_format", "pcm");
        JObject payload = new()
        {
            ["Engine"] = engine,
            ["OutputFormat"] = format,
            ["Text"] = text,
            ["VoiceId"] = voiceId,
            ["SampleRate"] = "24000"
        };
        string host = $"polly.{region}.amazonaws.com";
        string url = $"https://{host}/v1/speech";
        try
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
            Dictionary<string, string> headers = AwsSigV4.Sign(accessKey, secretKey, region, "polly", "POST", host, "/v1/speech", "", bodyBytes);
            headers["Content-Type"] = "application/json";
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            foreach (KeyValuePair<string, string> h in headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            HttpResponseMessage resp = await Http.SendAsync(req, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(cancel);
                return Error($"AWS Polly HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            }
            byte[] audio = await resp.Content.ReadAsByteArrayAsync(cancel);
            return AudioResult(ToBase64(audio), format == "pcm" ? "wav" : format, 24000);
        }
        catch (HttpRequestException ex)
        {
            return Error($"AWS Polly request failed: {ex.Message}");
        }
    }

    private static (string accessKey, string secretKey, string region) ParseAwsKey(string apiKey)
    {
        string[] parts = apiKey.Split('|');
        return parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : (null, null, null);
    }
}

/// <summary>AWS Transcribe handler — speech-to-text via AWS Transcribe.
/// API key format: "access_key_id|secret_access_key|region" (e.g. "AKIA...|wJalr...|us-east-1").</summary>
public sealed class AWSTranscribeHandler : ApiEngineHandlerBase
{
    public override async Task<JObject> ProcessAsync(Dictionary<string, object> args, string apiKey, CancellationToken cancel = default)
    {
        (string accessKey, string secretKey, string region) = ParseAwsKey(apiKey);
        if (accessKey == null) return Error("AWS API key must be in format: access_key_id|secret_access_key|region (e.g. AKIA...|wJalr...|us-east-1). Set this in Server > User Settings > API Keys.");
        byte[] audioData = DecodeAudioArg(args);
        if (audioData == null) return Error("No audio data provided.");
        string language = GetArg(args, "language_code", "en-US");
        string host = $"transcribe.{region}.amazonaws.com";
        string url = $"https://{host}/stream-transcription";
        JObject payload = new()
        {
            ["AudioStream"] = ToBase64(audioData),
            ["LanguageCode"] = language,
            ["MediaEncoding"] = "pcm",
            ["MediaSampleRateHertz"] = 16000
        };
        try
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
            Dictionary<string, string> headers = AwsSigV4.Sign(accessKey, secretKey, region, "transcribe", "POST", host, "/stream-transcription", "", bodyBytes);
            headers["Content-Type"] = "application/json";
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            foreach (KeyValuePair<string, string> h in headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            HttpResponseMessage resp = await Http.SendAsync(req, cancel);
            string body = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
            {
                return Error($"AWS Transcribe HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            }
            JObject result = JObject.Parse(body);
            string transcript = result["results"]?["transcripts"]?[0]?["transcript"]?.ToString() ?? "";
            return SttResult(transcript, language);
        }
        catch (HttpRequestException ex)
        {
            return Error($"AWS Transcribe request failed: {ex.Message}");
        }
    }

    private static (string accessKey, string secretKey, string region) ParseAwsKey(string apiKey)
    {
        string[] parts = apiKey.Split('|');
        return parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : (null, null, null);
    }
}

/// <summary>AWS Signature Version 4 signing utility.</summary>
internal static class AwsSigV4
{
    public static Dictionary<string, string> Sign(string accessKey, string secretKey, string region, string service,
        string method, string host, string path, string queryString, byte[] payload)
    {
        DateTime now = DateTime.UtcNow;
        string dateStamp = now.ToString("yyyyMMdd");
        string amzDate = now.ToString("yyyyMMddTHHmmssZ");
        string payloadHash = Sha256Hex(payload);
        string canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
        string signedHeaders = "host;x-amz-date";
        string canonicalRequest = $"{method}\n{path}\n{queryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        string stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";
        byte[] signingKey = GetSignatureKey(secretKey, dateStamp, region, service);
        string signature = HexEncode(HmacSha256(signingKey, stringToSign));
        string authorization = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new Dictionary<string, string>
        {
            ["Host"] = host,
            ["X-Amz-Date"] = amzDate,
            ["Authorization"] = authorization
        };
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string region, string service)
    {
        byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{key}"), dateStamp);
        byte[] kRegion = HmacSha256(kDate, region);
        byte[] kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using HMACSHA256 hmac = new(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(byte[] data)
        => HexEncode(SHA256.HashData(data));

    private static string HexEncode(byte[] data)
        => BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
}
