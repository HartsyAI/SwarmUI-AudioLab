using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>Routes for the full-duplex speech-to-speech model (Haru): what it offers, and an answer to a recording. The Generate tab and
/// <c>ProcessAudio</c> reach the same model; these routes take typed fields and return the words the model spoke and its frame timing.</summary>
[API.APIClass("Speech to speech: the full-duplex model's options and an offline answer to a recording.")]
public static class DuplexEndpoints
{
    /// <summary>Registers the routes.</summary>
    public static void Register()
    {
        API.RegisterAPICall(AudioLabDuplexOptions, false, AudioLabPermissions.PermCheckStatus);
        API.RegisterAPICall(AudioLabDuplexRespond, false, AudioLabPermissions.PermProcessAudio);
    }

    /// <summary>What the speech-to-speech model offers: its models and whether each is installed, every knob with its default and range, the frame length,
    /// the limits and what the model cannot do. Nothing here loads a model.</summary>
    public static async Task<JObject> AudioLabDuplexOptions(Session session)
    {
        await Task.CompletedTask;
        return DuplexOptionsCatalog.Build(AudioProviderRegistry.GetById(HaruProvider.ProviderId),
            (providerId, modelId) => AudioEngineBridge.WeightsPresent(providerId, modelId));
    }

    /// <summary>Answers a recording of the user, as the model would in a conversation: the recording is played to it frame by frame and its reply is returned.
    /// Fields: <c>user_audio</c> (base64, required), <c>model</c>, <c>card</c>, <c>reference_audio</c> (base64), <c>guidance</c>, <c>temperature</c>, <c>top_k</c>,
    /// <c>top_p</c>, <c>repetition_penalty</c>, <c>eop_bias</c>, <c>speak_first</c>, <c>max_frames</c>, <c>seed</c>, <c>tail_seconds</c>, <c>stereo</c>. The first call after
    /// an install converts the weights once (about 4 minutes).</summary>
    public static async Task<JObject> AudioLabDuplexRespond(Session session, JObject input)
    {
        try
        {
            AudioProviderDefinition provider = AudioProviderRegistry.GetById(HaruProvider.ProviderId);
            if (provider is null)
            {
                return AudioLab.CreateErrorResponse("The speech-to-speech provider is not registered.", "unknown_provider");
            }
            if (!DuplexRequestArgs.TryParse(input, out Dictionary<string, object> args, out string error, out string errorCode))
            {
                return AudioLab.CreateErrorResponse(error, errorCode);
            }
            return await AudioServerManager.Instance.ProcessAsync(provider, args, session?.User);
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("The speech-to-speech request failed", "processing_error", ex);
        }
    }
}

/// <summary>The fields a client may send to <c>AudioLabDuplexRespond</c>, read into the args dictionary the engine bridge takes. Independent of any socket or
/// session, so the rules are unit-testable.</summary>
internal static class DuplexRequestArgs
{
    /// <summary>The largest base64 string accepted for one clip, as in <c>ProcessAudio</c>.</summary>
    public const int MaxClipBase64 = AudioLabAPI.MaxArgStringLength;

    private static readonly string[] Passed =
        ["card", "guidance", "temperature", "top_k", "top_p", "repetition_penalty", "eop_bias", "speak_first", "max_frames", "seed", "tail_seconds", "stereo"];

    /// <summary>Reads and checks the fields. A clip too large, a missing recording or an unknown model is refused here, before any model is touched.</summary>
    public static bool TryParse(JObject input, out Dictionary<string, object> args, out string error, out string errorCode)
    {
        args = [];
        error = errorCode = null;
        if (input is null)
        {
            error = "No request was sent.";
            errorCode = "missing_input";
            return false;
        }
        foreach (string clip in new[] { "user_audio", "reference_audio" })
        {
            string base64 = input[clip]?.ToString();
            if (string.IsNullOrEmpty(base64))
            {
                continue;
            }
            if (base64.Length > MaxClipBase64)
            {
                error = $"{clip} is {base64.Length / (1024 * 1024)} MB, over the {MaxClipBase64 / (1024 * 1024)} MB limit for a single argument.";
                errorCode = "arg_too_large";
                return false;
            }
            args[clip] = base64;
        }
        if (!args.ContainsKey("user_audio"))
        {
            error = "user_audio is required: the base64 recording the model should answer.";
            errorCode = "missing_audio";
            return false;
        }
        string card = input["card"]?.ToString() ?? "";
        if (card.Length > AudioEngineRequests.MaxCardCharacters)
        {
            error = $"card is {card.Length} characters; the most is {AudioEngineRequests.MaxCardCharacters}.";
            errorCode = "card_too_long";
            return false;
        }
        foreach (string key in Passed)
        {
            if (input[key] is JToken value && value.Type != JTokenType.Null && value.Type != JTokenType.Undefined)
            {
                args[key] = value.ToObject<object>();
            }
        }
        string model = input["model"]?.ToString();
        if (!string.IsNullOrWhiteSpace(model))
        {
            AudioProviderDefinition provider = AudioProviderRegistry.GetById(HaruProvider.ProviderId);
            if (provider is null || !provider.Models.Any(m => string.Equals(m.Id, model.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{model}' is not a model of the speech-to-speech provider; use one of: {string.Join(", ", provider?.Models.Select(m => m.Id) ?? [])}.";
                errorCode = "unknown_model";
                return false;
            }
            args["__model_id"] = model.Trim();
        }
        return true;
    }
}

/// <summary>Builds the <c>AudioLabDuplexOptions</c> answer from the provider definition and the engine's own request defaults.</summary>
internal static class DuplexOptionsCatalog
{
    /// <summary>The model's frame length in samples at 24 kHz (133.3 ms).</summary>
    public const int FrameSamples = 3200;

    /// <summary>The sample rate of the model's audio, in Hz.</summary>
    public const int SampleRate = 24000;

    /// <summary>Builds the answer. <paramref name="weightsPresent"/> says whether a model's files are on disk.</summary>
    public static JObject Build(AudioProviderDefinition provider, Func<string, string, bool> weightsPresent)
    {
        DuplexSpeechRequest defaults = new();
        JArray models = [];
        foreach (AudioModelDefinition model in provider?.Models ?? [])
        {
            models.Add(new JObject
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["description"] = model.Description,
                ["estimated_size"] = model.EstimatedSize,
                ["estimated_vram"] = model.EstimatedVram,
                ["license"] = model.License,
                ["source_url"] = model.SourceUrl,
                ["installed"] = weightsPresent(HaruProvider.ProviderId, model.Id),
            });
        }
        return new JObject
        {
            ["success"] = true,
            ["provider_id"] = HaruProvider.ProviderId,
            ["default_model"] = provider?.Models.FirstOrDefault()?.Id,
            ["models"] = models,
            ["sample_rate"] = SampleRate,
            ["frame_samples"] = FrameSamples,
            ["frame_ms"] = Math.Round(1000.0 * FrameSamples / SampleRate, 2),
            ["knobs"] = new JObject
            {
                ["card"] = Knob("string", "", null, null, $"Who the model is, in plain text; at most {AudioEngineRequests.MaxCardCharacters} characters. May be empty."),
                ["guidance"] = Knob("number", defaults.Guidance, 1, 2, "Audio guidance weight; higher follows the voice reference more closely."),
                ["temperature"] = Knob("number", defaults.Temperature, 0, null, "Sampling temperature of the spoken words; 0 takes the most likely word."),
                ["top_k"] = Knob("integer", defaults.TopK, 1, 1000, "Samples from the K most likely words."),
                ["top_p"] = Knob("number", defaults.TopP, 0, 1, "Nucleus sampling threshold; 1 means no filtering."),
                ["repetition_penalty"] = Knob("boolean", defaults.RepetitionPenalty, null, null, "Penalize repeating a phrase already said."),
                ["eop_bias"] = Knob("number", defaults.EopBias, -8, 8, "Added to the logit that makes the model start speaking; above 0 it answers sooner."),
                ["speak_first"] = Knob("boolean", defaults.SpeakFirst, null, null, "The model opens the conversation."),
                ["max_frames"] = Knob("integer", defaults.MaxFrames, 1, defaults.MaxFrames, "Frames after which the conversation ends; 27,000 is an hour."),
                ["seed"] = Knob("integer", defaults.Seed, null, null, "Sampler seed; the same seed and audio repeat the conversation."),
                ["tail_seconds"] = Knob("number", 0, 0, AudioEngineRequests.MaxTailSeconds, "Silence appended after a recording so the model can finish (AudioLabDuplexRespond only)."),
                ["stereo"] = Knob("boolean", false, null, null, "Return the user on the left channel and the model on the right (AudioLabDuplexRespond only)."),
                ["reference_audio"] = Knob("base64 audio", null, null, null, "The voice the model speaks with: one speaker, 3 seconds to 3 minutes of clean speech you have consent to use. Omit for the model's own voice."),
                ["user_audio"] = Knob("base64 audio", null, null, null, "The recording the model answers (AudioLabDuplexRespond only; required)."),
            },
            ["limitations"] = new JArray(
                "A pretrained base model: it holds a natural conversation but does not follow instructions, call tools or transcribe the user.",
                "No echo cancellation in the engine: a live conversation needs headphones.",
                "Real time needs a free GPU: about 59 ms of compute per 133 ms frame on an RTX 3060, and another GPU job on the card breaks it.",
                "Only the Q8 build is offered: lower-bit builds are unintelligible.",
                "The weights are converted once on first use (about 4 minutes).",
                "The voice reference is subject to the codec licence: no voice impersonation without consent."),
        };
    }

    private static JObject Knob(string type, object defaultValue, double? min, double? max, string description)
    {
        JObject knob = new() { ["type"] = type, ["default"] = defaultValue is null ? JValue.CreateNull() : JToken.FromObject(defaultValue), ["description"] = description };
        if (min.HasValue)
        {
            knob["min"] = min.Value;
        }
        if (max.HasValue)
        {
            knob["max"] = max.Value;
        }
        return knob;
    }
}
