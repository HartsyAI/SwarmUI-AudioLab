using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Audio.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>A raw HTTP route that speaks text straight down the response body as it is synthesized.
///
/// <para><c>AudioLabSpeakRaw</c> is two round trips and one wait: the whole reply is synthesized, written to
/// disk, and only then does the device fetch it. So the first sample reaches the speaker after the last sample
/// has been generated. On the CPU box this project targets, a four-sentence reply spent five seconds there with
/// nothing to play.</para>
///
/// <para>This is one request, and the body starts arriving as soon as the first sentence exists. Same total
/// work, and the listener stops waiting for the part they have not heard yet.</para>
///
/// <para>It is not a SwarmUI API call, because those return JSON and the point here is to avoid a container and
/// a base64 expansion in front of a microcontroller with a few hundred kilobytes of RAM. It is registered
/// directly on the web application in <see cref="AudioLab.OnPreLaunch"/>, which runs after core has mapped its
/// own routes and before the server starts listening.</para></summary>
public static class SpeakStreamRoute
{
    /// <summary>Where the route lives. Deliberately outside <c>/API/</c>: nothing here is JSON.</summary>
    public const string Path = "/AudioLab/SpeakStream";

    /// <summary>Registers the route. Safe to call once, from <c>OnPreLaunch</c>.</summary>
    public static void Register()
    {
        WebServer.WebApp.MapPost(Path, Handle);
        Logs.Init($"[AudioLab] Streaming speech route at {Path}");
    }

    /// <summary>Synthesizes the posted text and writes 16-bit little-endian mono PCM as it arrives.
    ///
    /// <para>Query: <c>session_id</c> (required), <c>sample_rate</c> (default 16000), <c>voice</c>,
    /// <c>provider_id</c>. Body: the text, either bare or as <c>{"text": "..."}</c>.</para>
    ///
    /// <para>The response carries the format in headers — <c>X-Sample-Rate</c>, <c>X-Channels</c>,
    /// <c>X-Bits-Per-Sample</c> — and no <c>Content-Length</c>, because the length is not known until the last
    /// sentence is done. A client reads until the connection closes.</para></summary>
    private static async Task Handle(HttpContext context)
    {
        string sessionId = context.Request.Query["session_id"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !Program.Sessions.TryGetSession(sessionId, out Session session))
        {
            await Fail(context, StatusCodes.Status401Unauthorized, "A valid session_id query parameter is required.");
            return;
        }
        if (!session.User.HasPermission(AudioLabPermissions.PermProcessAudio))
        {
            await Fail(context, StatusCodes.Status403Forbidden, "This session may not process audio.");
            return;
        }

        string text = await ReadTextAsync(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Fail(context, StatusCodes.Status400BadRequest, "A text body is required.");
            return;
        }
        int targetRate = ParseRate(context.Request.Query["sample_rate"].ToString());
        if (targetRate <= 0)
        {
            await Fail(context, StatusCodes.Status400BadRequest, "sample_rate must be between 8000 and 48000.");
            return;
        }

        string requestedProvider = context.Request.Query["provider_id"].ToString();
        AudioProviderDefinition provider = !string.IsNullOrEmpty(requestedProvider)
            ? AudioProviderRegistry.GetById(requestedProvider)
            : AudioProviderRegistry.GetById("piper_tts") ?? AudioProviderRegistry.GetByCategory(AudioCategory.TTS).FirstOrDefault();
        if (provider is null)
        {
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "No text-to-speech provider is available.");
            return;
        }

        // Same reasoning as AudioLabSpeakRaw: Piper selects its weights by voice, so the "default" sentinel
        // becomes a request for a voice file named after the model and fails the download.
        string voice = context.Request.Query["voice"].ToString();
        if (string.IsNullOrWhiteSpace(voice))
        {
            voice = provider.Id.Equals("piper_tts", StringComparison.OrdinalIgnoreCase)
                ? "en_US-lessac-medium" : AudioConfiguration.DefaultVoice;
        }
        Dictionary<string, object> args = new()
        {
            ["text"] = text,
            ["voice"] = voice,
            ["language"] = AudioConfiguration.DefaultLanguage,
            ["volume"] = 1.0,
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["X-Sample-Rate"] = targetRate.ToString();
        context.Response.Headers["X-Channels"] = "1";
        context.Response.Headers["X-Bits-Per-Sample"] = "16";

        long started = Environment.TickCount64;
        long firstByteAt = -1;
        long samplesOut = 0;
        try
        {
            await foreach (byte[] block in SynthesizeAsync(provider, args, targetRate, session.User, context.RequestAborted)
                .ConfigureAwait(false))
            {
                if (firstByteAt < 0)
                {
                    firstByteAt = Environment.TickCount64 - started;
                }
                await context.Response.Body.WriteAsync(block, context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                samplesOut += block.Length / 2;
            }
        }
        catch (OperationCanceledException)
        {
            // The device hung up mid-reply. Normal — it is how a barge-in looks from here.
            Logs.Debug($"[AudioLab][SpeakStream] Client disconnected after {samplesOut} samples.");
            return;
        }
        catch (Exception ex)
        {
            // The headers are already out, so there is no error envelope left to send: the client sees a body
            // that stops early, which is exactly what it must already handle for a dropped connection.
            Logs.Error($"[AudioLab][SpeakStream] Synthesis failed after {samplesOut} samples: {ex.ReadableString()}");
            return;
        }
        Logs.Debug($"[AudioLab][SpeakStream] {samplesOut / (double)targetRate:0.00}s of audio, "
            + $"first byte at {firstByteAt}ms, complete at {Environment.TickCount64 - started}ms.");
    }

    /// <summary>Yields resampled 16-bit PCM blocks, one per chunk the engine produces.
    ///
    /// <para>Falls back to the whole-clip path for a provider with no streaming support, so the route's contract
    /// does not depend on which model is configured — the client sees the same bytes either way, just later.</para></summary>
    private static async IAsyncEnumerable<byte[]> SynthesizeAsync(AudioProviderDefinition provider,
        Dictionary<string, object> args, int targetRate, User user, [EnumeratorCancellation] CancellationToken cancel)
    {
        if (!AudioEngineBridge.SupportsNativeStreaming(provider.Id))
        {
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user).ConfigureAwait(false);
            if (result["success"]?.Value<bool>() != true)
            {
                throw new InvalidOperationException(result["error"]?.ToString() ?? "The provider returned no audio.");
            }
            byte[] wav = Convert.FromBase64String(result["audio_data"]?.ToString() ?? "");
            (int sourceRate, int channels, int bits) = AudioIo.ReadWavFormat(wav);
            float[] mono = AudioLabAPI.PcmToMono(AudioIo.StripWavHeader(wav), channels, bits);
            yield return Encode(mono, sourceRate, targetRate);
            yield break;
        }

        await foreach (AudioChunk chunk in AudioEngineBridge.ProcessStreamAsync(provider.Id, args, cancel)
            .ConfigureAwait(false))
        {
            if (chunk.Samples is null || chunk.Samples.Length == 0)
            {
                continue;
            }
            float[] mono = chunk.Channels > 1 ? Downmix(chunk.Samples, chunk.Channels) : chunk.Samples;
            yield return Encode(mono, chunk.SampleRate, targetRate);
        }
    }

    /// <summary>Resamples if needed and packs to little-endian signed 16-bit.</summary>
    /// <remarks>Each chunk is resampled on its own. A resampler carries filter state across a boundary and this
    /// one does not, so a sentence boundary gets a few samples of edge effect — inaudible next to the silence
    /// Piper already puts at the end of every sentence, and the alternative is holding state that the
    /// non-streaming fallback would not have.</remarks>
    private static byte[] Encode(float[] mono, int sourceRate, int targetRate)
    {
        if (sourceRate != targetRate)
        {
            mono = HartsyInference.Audio.Io.Resampler.Create(sourceRate, targetRate).Resample(mono);
        }
        byte[] pcm = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            short sample = (short)Math.Clamp(mono[i] * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
        }
        return pcm;
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        float[] mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[i * channels + c];
            }
            mono[i] = sum / channels;
        }
        return mono;
    }

    /// <summary>Reads the body as text, accepting either a bare string or <c>{"text": "..."}</c>.</summary>
    /// <remarks>Bare text is what a microcontroller can send without building JSON; the JSON form is what every
    /// other caller here already speaks. Guessing between them on the first non-space character is cheaper than
    /// making either side care.</remarks>
    private static async Task<string> ReadTextAsync(HttpContext context)
    {
        using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
        string body = (await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false)).Trim();
        if (!body.StartsWith('{'))
        {
            return body;
        }
        try
        {
            return JObject.Parse(body)["text"]?.ToString() ?? "";
        }
        catch (Newtonsoft.Json.JsonException)
        {
            // It started with a brace but is not JSON. Speaking it verbatim would be worse than saying so.
            return "";
        }
    }

    private static int ParseRate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 16000;
        }
        return int.TryParse(raw, out int rate) && rate is >= 8000 and <= 48000 ? rate : -1;
    }

    /// <summary>Writes a plain-text error before any audio has been sent.</summary>
    private static async Task Fail(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message).ConfigureAwait(false);
    }
}
