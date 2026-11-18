using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>The parsed, validated <c>start</c> message every <c>AudioLabVoiceSession</c> connection opens with:
/// <c>{model, assistantId?, voice?, systemPrompt?, bargeIn?, inputRate, sttModel?, ttsEngine?}</c>. Core hands the raw frame to the route
/// handler as a <see cref="JObject"/> (the same shape <c>AudioLabWakeSaveSettings</c> takes it in) -- this is the
/// WebSocket handshake frame itself, not a second message.</summary>
///
/// <para><c>SttModel</c> and <c>TtsEngine</c> are null when the frame names none (the default recognizer and engine are used,
/// see <see cref="VoiceEngineCatalog.KeyFor"/>); when present they have passed <see cref="VoiceEngineCatalog"/>'s allow-list.
/// <c>Voice</c> is null for the engine's default voice.</para>
internal sealed record VoiceSessionStartRequest(string Model, string AssistantId, string Voice, string SystemPrompt, bool BargeIn, int InputRate,
    string SttModel = null, string TtsEngine = null);

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
        string sttModel = NullIfBlank(rawInput["sttModel"]?.ToString())?.Trim();
        if (sttModel is not null && !VoiceEngineCatalog.IsKnownStt(sttModel))
        {
            error = $"'sttModel' must be one of the listed recognizers; '{sttModel}' is not.";
            return false;
        }
        string ttsEngine = NullIfBlank(rawInput["ttsEngine"]?.ToString())?.Trim().ToLowerInvariant();
        if (ttsEngine is not null && VoiceEngineCatalog.FindEngine(ttsEngine) is null)
        {
            error = $"'ttsEngine' must be one of the listed speech engines; '{ttsEngine}' is not.";
            return false;
        }
        string voice = NullIfBlank(rawInput["voice"]?.ToString());
        if (voice is not null && !VoiceEngineCatalog.IsKnownVoice(ttsEngine ?? VoiceEngineCatalog.DefaultTtsEngine, voice))
        {
            error = $"'voice' '{voice}' is not a voice of the '{ttsEngine ?? VoiceEngineCatalog.DefaultTtsEngine}' speech engine.";
            return false;
        }
        request = new VoiceSessionStartRequest(
            model.Trim(),
            NullIfBlank(rawInput["assistantId"]?.ToString()),
            voice,
            NullIfBlank(rawInput["systemPrompt"]?.ToString()),
            bargeIn,
            inputRate,
            sttModel,
            ttsEngine);
        error = null;
        return true;
    }

    private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
