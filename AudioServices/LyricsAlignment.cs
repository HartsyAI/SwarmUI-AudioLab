using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>One speech-to-text call the lyrics aligner asks its host to run: which provider and model, whether to time the words, and the known text to time (empty to transcribe).</summary>
public readonly record struct SttCall(string ProviderId, string Model, bool WordTimestamps, string AlignText);

/// <summary>Times the words of a vocal clip, whichever speech models the user chose. The model that transcribes and the model that times words are two independent choices: with lyrics in hand only the aligner runs; without them the chosen speech-to-text model transcribes, and if it cannot time words itself its transcript is handed to the aligner. Pure routing over an injected transcribe call, so it needs no engine.</summary>
public static class LyricsAlignment
{
    /// <summary>The provider that times words when the caller names none: Whisper, the model family whose attention yields word times.</summary>
    public const string DefaultAlignerProviderId = "whisper_stt";

    /// <summary>Runs the alignment. <paramref name="input"/> carries <c>lyrics</c> (optional), <c>stt_provider_id</c> and <c>stt_model</c> (the transcriber, defaulting to the aligner), <c>align_provider_id</c> and <c>align_model</c> (the aligner, defaulting to Whisper's default model). The result has <c>source</c>: <c>aligned</c> (the given lyrics were timed), <c>stt</c> (the transcriber timed its own words) or <c>stt+aligned</c> (the transcriber's text was timed by the aligner). A failed call is returned as it came; an aligner that returns no word times at all is an error (<c>no_word_timing</c>), not an empty success.</summary>
    public static async Task<JObject> RunAsync(JObject input, Func<SttCall, Task<JObject>> transcribe)
    {
        string lyrics = input["lyrics"]?.ToString();
        string alignerId = Pick(input["align_provider_id"]?.ToString(), DefaultAlignerProviderId);
        string alignerModel = input["align_model"]?.ToString();
        string source = "aligned";

        if (string.IsNullOrWhiteSpace(lyrics))
        {
            string transcriberId = Pick(input["stt_provider_id"]?.ToString(), alignerId);
            string transcriberModel = input["stt_model"]?.ToString();
            JObject heard = await transcribe(new SttCall(transcriberId, transcriberModel, true, null));
            if (heard["success"]?.Value<bool>() != true)
            {
                return heard;
            }
            if (heard["words"] is JArray timed && timed.Count > 0)
            {
                return Result(heard, "stt", transcriberId, transcriberModel);
            }
            lyrics = heard["text"]?.ToString();
            if (string.IsNullOrWhiteSpace(lyrics))
            {
                return Result(heard, "stt", transcriberId, transcriberModel);
            }
            source = "stt+aligned";
        }

        JObject aligned = await transcribe(new SttCall(alignerId, alignerModel, true, lyrics));
        if (aligned["success"]?.Value<bool>() != true)
        {
            return aligned;
        }
        if (aligned["words"] is not JArray alignedWords || alignedWords.Count == 0)
        {
            // A model with no word timing (Moonshine, a cloud transcriber) ignores the known text and just
            // transcribes, so "success" with nothing timed would read as if the lyrics had been aligned.
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"'{alignerId}' returned no word times for the text. Pick an aligner that can time words (Whisper or Distil-Whisper).",
                ["error_code"] = "no_word_timing",
            };
        }
        aligned["text"] = lyrics.Trim();
        return Result(aligned, source, alignerId, alignerModel);
    }

    /// <summary>The caller's value, or <paramref name="fallback"/> when it is missing or blank.</summary>
    private static string Pick(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>The response the endpoint returns: the engine's text, words, duration and language, plus where the word times came from and which model produced them.</summary>
    private static JObject Result(JObject engine, string source, string providerId, string model) => new()
    {
        ["success"] = true,
        ["source"] = source,
        ["text"] = engine["text"]?.ToString() ?? "",
        ["words"] = engine["words"] as JArray ?? new JArray(),
        ["duration"] = (double?)engine["duration"] ?? 0,
        ["language"] = engine["language"],
        ["provider_id"] = providerId,
        ["model"] = model,
    };
}
