using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Speech turns (voice references and conversation history) as AudioLab carries them in request args and returns them in
/// results: a JSON array of <c>{ "text": …, "audio": base64 }</c> or <c>{ "text": …, "codes": [[…], …] }</c> entries, codes one row
/// per codebook. <see cref="ToJson"/> writes the second form, so the turns a take returned go straight back in as history.</summary>
public static class SpeechTurnJson
{
    /// <summary>The turns under <paramref name="key"/>: a JSON array, or its text (as a Swarm param carries it). Null when the arg is
    /// absent or empty.</summary>
    /// <exception cref="ArgumentException">The value is not an array of turns, or a turn has no text or no sound.</exception>
    public static IReadOnlyList<SpeechTurn> Parse(IReadOnlyDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out object value) || value is null)
        {
            return null;
        }
        JToken token = value switch
        {
            JToken json => json,
            string text when string.IsNullOrWhiteSpace(text) => null,
            string text => JToken.Parse(text),
            _ => JToken.FromObject(value),
        };
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token is not JArray entries)
        {
            throw new ArgumentException($"'{key}' must be a JSON array of turns, each with its text and its audio or codes.");
        }
        List<SpeechTurn> turns = new(entries.Count);
        foreach (JToken entry in entries)
        {
            string text = entry["text"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException($"Every '{key}' turn needs its text (a reference's exact transcript).");
            }
            int[][] codes = entry["codes"]?.ToObject<int[][]>();
            string audio = entry["audio"]?.ToString();
            if (codes is { Length: > 0 })
            {
                turns.Add(new SpeechTurn { Text = text, Codes = codes });
            }
            else if (!string.IsNullOrEmpty(audio))
            {
                turns.Add(new SpeechTurn { Text = text, Audio = new AudioClip { Data = Convert.FromBase64String(audio) } });
            }
            else
            {
                throw new ArgumentException($"The '{key}' turn \"{text}\" has neither audio nor codes.");
            }
        }
        return turns.Count == 0 ? null : turns;
    }

    /// <summary>The turns as text and codes, the form <see cref="Parse"/> reads back as history.</summary>
    public static JArray ToJson(IReadOnlyList<SpeechTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        JArray entries = [];
        foreach (SpeechTurn turn in turns)
        {
            JObject entry = new() { ["text"] = turn.Text };
            if (turn.Codes is not null)
            {
                entry["codes"] = JArray.FromObject(turn.Codes);
            }
            entries.Add(entry);
        }
        return entries;
    }
}
