using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>The parsed, validated <c>start</c> message every <c>AudioLabVoiceSession</c> connection opens with:
/// <c>{model, assistantId?, voice?, systemPrompt?, bargeIn?, inputRate}</c>. Core hands the raw frame to the route
/// handler as a <see cref="JObject"/> (the same shape <c>AudioLabWakeSaveSettings</c> takes it in) -- this is the
/// WebSocket handshake frame itself, not a second message.</summary>
internal sealed record VoiceSessionStartRequest(string Model, string AssistantId, string Voice, string SystemPrompt, bool BargeIn, int InputRate);

/// <summary>Parses and validates a <c>start</c> message, independent of any socket -- so the rules (which fields
/// are required, which rates are usable) are unit-testable without a WebSocket at all.</summary>
internal static class VoiceSessionStart
{
    /// <summary>Lowest <c>inputRate</c> accepted. Below this, a 20 ms frame is a few dozen samples and not worth
    /// treating as real speech audio.</summary>
    public const int MinInputRate = 8000;

    /// <summary>Highest <c>inputRate</c> accepted -- generous (actual browsers top out at 48-96 kHz); the real
    /// limit in practice is <see cref="VoiceInboundResampler.MaxFrameSamples"/> below.</summary>
    public const int MaxInputRate = 192000;

    public static bool TryParse(JObject rawInput, out VoiceSessionStartRequest request, out string error)
    {
        request = null;
        if (rawInput is null)
        {
            error = "No start message was sent.";
            return false;
        }
        string model = rawInput["model"]?.ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            error = "'model' is required.";
            return false;
        }
        JToken rateToken = rawInput["inputRate"];
        if (rateToken is null || rateToken.Type == JTokenType.Null || !int.TryParse(rateToken.ToString(), out int inputRate))
        {
            error = "'inputRate' is required and must be an integer.";
            return false;
        }
        if (inputRate < MinInputRate || inputRate > MaxInputRate)
        {
            error = $"'inputRate' must be between {MinInputRate} and {MaxInputRate} Hz; was {inputRate}.";
            return false;
        }
        if (inputRate != 16000
            && !VoiceAudioFraming.TryComputeFrameSize(inputRate, 16000, VoiceInboundResampler.NumTaps, VoiceInboundResampler.TargetFrameMs,
                VoiceInboundResampler.MaxFrameSamples, out _, out string frameError))
        {
            error = frameError;
            return false;
        }
        bool bargeIn = true;
        if (rawInput["bargeIn"] is JToken bargeInToken && bargeInToken.Type != JTokenType.Null
            && !bool.TryParse(bargeInToken.ToString(), out bargeIn))
        {
            error = "'bargeIn' must be a boolean.";
            return false;
        }
        request = new VoiceSessionStartRequest(
            model.Trim(),
            NullIfBlank(rawInput["assistantId"]?.ToString()),
            NullIfBlank(rawInput["voice"]?.ToString()),
            NullIfBlank(rawInput["systemPrompt"]?.ToString()),
            bargeIn,
            inputRate);
        error = null;
        return true;
    }

    private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
