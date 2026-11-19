using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>One voice the Voice Agent tab can offer: the id sent in the setup frame, a display name, and the
/// language group it is listed under.</summary>
internal readonly record struct VoiceChoice(string Id, string Name, string Group);

/// <summary>Builds the answer to <c>AudioLabVoiceAgentOptions</c>: what the Voice Agent tab can offer, as plain data.
/// Independent of the engine and of SwarmUI core so the shape is unit-testable; the route only supplies the inputs.</summary>
internal static class VoiceOptionsCatalog
{
    /// <summary>The voice a call uses when none is chosen (see <see cref="VoiceEngineModels.AcquireAsync"/>).</summary>
    public const string DefaultVoice = "af_heart";

    /// <summary>Splits <c>id///Language: Name (F)</c> entries (the format of <c>AudioLabParams.KokoroVoices</c>) into
    /// choices. An entry with no label falls back to its id; one with no language prefix lands in the "Other" group;
    /// blank entries are skipped.</summary>
    public static List<VoiceChoice> ParseVoices(IEnumerable<string> entries)
    {
        List<VoiceChoice> choices = [];
        foreach (string entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            string[] parts = entry.Split("///", 2, StringSplitOptions.TrimEntries);
            string id = parts[0];
            if (id.Length == 0)
            {
                continue;
            }
            string label = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : id;
            int colon = label.IndexOf(':');
            string group = colon > 0 ? label[..colon].Trim() : "Other";
            string name = colon > 0 ? label[(colon + 1)..].Trim() : label;
            choices.Add(new VoiceChoice(id, name.Length > 0 ? name : id, group));
        }
        return choices;
    }

    /// <summary>The route's response: the recognizers, the speech engines with their voices, the defaults, and whether LLMAssistant
    /// is present (<paramref name="llmAvailable"/>; its models and assistants are listed by the tab through its own routes, not
    /// copied here).</summary>
    public static JObject Build(bool llmAvailable)
    {
        JArray stt = new(VoiceEngineCatalog.Stt.Select(s => new JObject { ["id"] = s.Id, ["name"] = s.Name, ["note"] = s.Note, ["multilingual"] = s.Multilingual }));
        JArray tts = [];
        foreach (TtsEngineChoice engine in VoiceEngineCatalog.TtsEngines)
        {
            List<VoiceChoice> voices = ParseVoices(VoiceEngineCatalog.VoiceEntries(engine.Engine));
            string defaultVoice = voices.Any(v => v.Id == engine.DefaultVoice) ? engine.DefaultVoice : voices.FirstOrDefault().Id ?? engine.DefaultVoice;
            tts.Add(new JObject
            {
                ["engine"] = engine.Engine,
                ["name"] = engine.Name,
                ["note"] = engine.Note,
                ["provider_id"] = engine.ProviderId,
                ["default_voice"] = defaultVoice,
                ["voices"] = new JArray(voices.Select(v => new JObject { ["id"] = v.Id, ["name"] = v.Name, ["group"] = v.Group })),
            });
        }
        return new JObject
        {
            ["success"] = true,
            ["stt"] = stt,
            ["default_stt"] = VoiceEngineCatalog.DefaultStt,
            ["languages"] = new JArray(VoiceEngineCatalog.Languages.Select(l => new JObject { ["id"] = l.Id, ["name"] = l.Name })),
            ["default_language"] = VoiceEngineCatalog.DefaultLanguage,
            ["tts"] = tts,
            ["default_tts"] = VoiceEngineCatalog.DefaultTtsEngine,
            ["llm_available"] = llmAvailable,
        };
    }
}
