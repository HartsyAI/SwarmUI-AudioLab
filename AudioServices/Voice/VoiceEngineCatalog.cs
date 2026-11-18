namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>One speech recognizer a call can use. <see cref="Id"/> is the engine's model token, which is also what the
/// browser sends as <c>sttModel</c>.</summary>
internal readonly record struct SttChoice(string Id, string Name, string Note);

/// <summary>One speech engine a call can use: <see cref="Engine"/> is the engine's family id (the part of the model
/// token before the voice), <see cref="ProviderId"/> the AudioLab provider that owns its weights and voice previews.</summary>
internal readonly record struct TtsEngineChoice(string Engine, string Name, string ProviderId, string DefaultVoice, string Note);

/// <summary>The model set a call needs: the recognizer and the speech model, as engine tokens. Two calls can share a loaded
/// <see cref="HartsyInference.Voice.VoiceModelSet"/> only when their keys are equal.</summary>
internal readonly record struct VoiceModelKey(string Stt, string Tts)
{
    /// <summary>The key in words, for the notice a caller gets when another call holds a different set.</summary>
    public string Describe() => $"{VoiceEngineCatalog.NameOfStt(Stt)} with {VoiceEngineCatalog.NameOfTts(Tts)}";
}

/// <summary>What the Voice Agent can run: the recognizers and speech engines that work as a live call, which is a smaller
/// set than everything the engine has. A call speaks one sentence at a time and hears 16 kHz utterances, so a speech model
/// qualifies when it is fast enough for that and needs no voice clip; a recognizer qualifies when the engine's transcriber
/// lease runs it on a whole utterance. Anything else stays out of the list rather than failing on the first call.
///
/// <para>The list is also the allow-list for what a browser may ask for: the values reach the engine as model tokens, and
/// the engine treats a token that names an existing file or folder as a local model path, so an arbitrary string from a
/// client must never get that far.</para></summary>
internal static class VoiceEngineCatalog
{
    /// <summary>What a call uses when the setup frame names no recognizer (the choice before recognizers were selectable).</summary>
    public const string DefaultStt = "whisper:openai/whisper-small.en";

    /// <summary>What a call uses when the setup frame names no speech engine.</summary>
    public const string DefaultTtsEngine = "kokoro";

    /// <summary>The recognizers, smallest first. All of them hear English: the call asks for English (an English-only
    /// checkpoint drops the language token itself, and a multilingual one with none loops).</summary>
    public static IReadOnlyList<SttChoice> Stt { get; } =
    [
        new("whisper:openai/whisper-tiny.en", "Whisper tiny.en", "Fastest Whisper, least accurate. About 75 MB."),
        new("whisper:openai/whisper-base.en", "Whisper base.en", "Fast and light. About 150 MB."),
        new(DefaultStt, "Whisper small.en", "The default: a good balance. About 480 MB."),
        new("whisper:openai/whisper-medium.en", "Whisper medium.en", "More accurate, slower to hear you. About 1.5 GB."),
        new("distilwhisper:large-v3.5", "Distil-Whisper large v3.5", "Near large-model accuracy at a fraction of the time. About 1.5 GB."),
        new("moonshine:tiny", "Moonshine tiny", "Built for short live utterances, very fast. About 200 MB."),
        new("moonshine:base", "Moonshine base", "Moonshine with better accuracy. About 400 MB."),
    ];

    /// <summary>The speech engines. Kokoro has one weights file and many voices; for Piper every voice is its own small
    /// model, so the first call with a new Piper voice downloads it.</summary>
    public static IReadOnlyList<TtsEngineChoice> TtsEngines { get; } =
    [
        new("kokoro", "Kokoro", "kokoro_tts", VoiceOptionsCatalog.DefaultVoice, "Natural and expressive, 54 voices in several languages."),
        new("piper", "Piper", "piper_tts", "en_US-lessac-medium", "Very fast, light enough for a CPU. English voices, each a small download."),
    ];

    /// <summary>Whether <paramref name="id"/> is one of <see cref="Stt"/>.</summary>
    public static bool IsKnownStt(string id) => Stt.Any(s => s.Id == id);

    /// <summary>The engine named <paramref name="engine"/> (any case), or null.</summary>
    public static TtsEngineChoice? FindEngine(string engine)
    {
        foreach (TtsEngineChoice choice in TtsEngines)
        {
            if (string.Equals(choice.Engine, engine, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }
        return null;
    }

    /// <summary>The voices <paramref name="engine"/> offers, as <c>id///Group: Name</c> entries (the format
    /// <see cref="VoiceOptionsCatalog.ParseVoices"/> reads).</summary>
    public static IEnumerable<string> VoiceEntries(string engine) => engine switch
    {
        "kokoro" => AudioLabParams.KokoroVoices,
        "piper" => AudioLabParams.PiperVoices.Select(v => $"{v.Id}///{PiperGroup(v.Id)}: {v.Label}"),
        _ => [],
    };

    /// <summary>Whether <paramref name="voice"/> is one <paramref name="engine"/> can speak. Kokoro also takes the engine's
    /// blend syntax and has always accepted whatever the setup frame held, so only Piper, where the voice picks the weights
    /// file to open, is checked against its list.</summary>
    public static bool IsKnownVoice(string engine, string voice) => engine switch
    {
        "piper" => AudioLabParams.PiperVoices.Any(v => v.Id == voice),
        "kokoro" => !string.IsNullOrWhiteSpace(voice),
        _ => false,
    };

    /// <summary>The key for a call's setup frame, with the defaults filled in. The values are already validated by
    /// <see cref="VoiceSessionStart.TryParse"/>; a request built in code (the satellite path) carries none and gets the defaults.</summary>
    public static VoiceModelKey KeyFor(VoiceSessionStartRequest start)
    {
        string engine = FindEngine(start.TtsEngine ?? DefaultTtsEngine)?.Engine ?? DefaultTtsEngine;
        string voice = string.IsNullOrWhiteSpace(start.Voice) ? FindEngine(engine)!.Value.DefaultVoice : start.Voice;
        return new VoiceModelKey(start.SttModel ?? DefaultStt, $"{engine}:{voice}");
    }

    /// <summary>The engine family of a speech model token (<c>kokoro:af_heart</c> gives <c>kokoro</c>).</summary>
    public static string EngineOf(string ttsToken)
    {
        int colon = ttsToken.IndexOf(':');
        return colon > 0 ? ttsToken[..colon] : ttsToken;
    }

    /// <summary>The voice of a speech model token (<c>kokoro:af_heart</c> gives <c>af_heart</c>).</summary>
    public static string VoiceOf(string ttsToken)
    {
        int colon = ttsToken.IndexOf(':');
        return colon > 0 ? ttsToken[(colon + 1)..] : ttsToken;
    }

    /// <summary>A recognizer token in words, or the token itself when it is not a listed one.</summary>
    public static string NameOfStt(string token) => Stt.FirstOrDefault(s => s.Id == token).Name ?? token;

    /// <summary>A speech token in words: the engine's name and the voice.</summary>
    public static string NameOfTts(string token)
    {
        string name = FindEngine(EngineOf(token))?.Name ?? EngineOf(token);
        return $"{name} {VoiceOf(token)}";
    }

    /// <summary>The language group a Piper voice id is listed under, from its locale (<c>en_US</c>, <c>en_GB</c>).</summary>
    private static string PiperGroup(string voiceId)
    {
        string locale = voiceId.Split('-', 2)[0];
        return locale switch
        {
            "en_US" => "American English",
            "en_GB" => "British English",
            _ => locale,
        };
    }
}
