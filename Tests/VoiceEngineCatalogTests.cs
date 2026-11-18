using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceEngineCatalog"/>: the recognizers and speech engines a call may name, and the key a
/// setup frame turns into.</summary>
public class VoiceEngineCatalogTests
{
    [Fact]
    public void TheDefaultRecognizerAndEngine_AreListed()
    {
        Assert.True(VoiceEngineCatalog.IsKnownStt(VoiceEngineCatalog.DefaultStt));
        Assert.NotNull(VoiceEngineCatalog.FindEngine(VoiceEngineCatalog.DefaultTtsEngine));
    }

    [Fact]
    public void NoDuplicateRecognizersOrEngines()
    {
        Assert.Equal(VoiceEngineCatalog.Stt.Count, VoiceEngineCatalog.Stt.Select(s => s.Id).Distinct().Count());
        Assert.Equal(VoiceEngineCatalog.TtsEngines.Count, VoiceEngineCatalog.TtsEngines.Select(e => e.Engine).Distinct().Count());
    }

    [Theory]
    [InlineData("whisper:openai/whisper-small.en", true)]
    [InlineData("moonshine:tiny", true)]
    [InlineData("whisper:openai/whisper-large-v3", false)]
    [InlineData("C:/Windows/System32", false)]
    [InlineData("../../model", false)]
    [InlineData("", false)]
    public void IsKnownStt_OnlyTheListedTokens(string token, bool expected)
    {
        Assert.Equal(expected, VoiceEngineCatalog.IsKnownStt(token));
    }

    [Fact]
    public void FindEngine_IgnoresCase_AndUnknownIsNull()
    {
        Assert.Equal("piper", VoiceEngineCatalog.FindEngine("PIPER")?.Engine);
        Assert.Null(VoiceEngineCatalog.FindEngine("bark"));
        Assert.Null(VoiceEngineCatalog.FindEngine(null));
    }

    [Fact]
    public void EveryListedVoice_IsAKnownVoiceOfItsEngine()
    {
        foreach (TtsEngineChoice engine in VoiceEngineCatalog.TtsEngines)
        {
            List<VoiceChoice> voices = VoiceOptionsCatalog.ParseVoices(VoiceEngineCatalog.VoiceEntries(engine.Engine));
            Assert.NotEmpty(voices);
            Assert.All(voices, v => Assert.True(VoiceEngineCatalog.IsKnownVoice(engine.Engine, v.Id), $"{engine.Engine}:{v.Id}"));
            Assert.True(VoiceEngineCatalog.IsKnownVoice(engine.Engine, engine.DefaultVoice));
        }
    }

    [Fact]
    public void Piper_RejectsAVoiceNotInItsList_KokoroKeepsAcceptingAnyName()
    {
        Assert.False(VoiceEngineCatalog.IsKnownVoice("piper", "C:/models/evil"));
        Assert.True(VoiceEngineCatalog.IsKnownVoice("kokoro", "af_heart:0.7,af_bella:0.3"));
        Assert.False(VoiceEngineCatalog.IsKnownVoice("bark", "x"));
    }

    [Fact]
    public void KeyFor_NothingNamed_IsTheOriginalFixedChoice()
    {
        VoiceSessionStartRequest start = new("m", null, null, null, true, 16000);
        Assert.Equal(new VoiceModelKey("whisper:openai/whisper-small.en", "kokoro:af_heart"), VoiceEngineCatalog.KeyFor(start));
    }

    [Fact]
    public void KeyFor_UsesTheNamedRecognizerEngineAndVoice()
    {
        VoiceSessionStartRequest start = new("m", null, "en_US-amy-low", null, true, 16000, "moonshine:base", "piper");
        Assert.Equal(new VoiceModelKey("moonshine:base", "piper:en_US-amy-low"), VoiceEngineCatalog.KeyFor(start));
    }

    [Fact]
    public void KeyFor_EngineWithNoVoice_UsesThatEnginesDefault()
    {
        VoiceSessionStartRequest start = new("m", null, null, null, true, 16000, null, "piper");
        Assert.Equal("piper:en_US-lessac-medium", VoiceEngineCatalog.KeyFor(start).Tts);
    }

    [Fact]
    public void TokenHelpers_SplitEngineAndVoice_AndNameThem()
    {
        Assert.Equal("kokoro", VoiceEngineCatalog.EngineOf("kokoro:af_heart"));
        Assert.Equal("af_heart", VoiceEngineCatalog.VoiceOf("kokoro:af_heart"));
        Assert.Equal("Kokoro af_heart", VoiceEngineCatalog.NameOfTts("kokoro:af_heart"));
        Assert.Equal("Moonshine tiny", VoiceEngineCatalog.NameOfStt("moonshine:tiny"));
        Assert.Equal("unlisted:x", VoiceEngineCatalog.NameOfStt("unlisted:x"));
    }

    private static JObject Frame(string stt, string engine, string voice)
    {
        JObject frame = new() { ["model"] = "m", ["inputRate"] = 16000 };
        if (stt is not null)
        {
            frame["sttModel"] = stt;
        }
        if (engine is not null)
        {
            frame["ttsEngine"] = engine;
        }
        if (voice is not null)
        {
            frame["voice"] = voice;
        }
        return frame;
    }

    [Fact]
    public void ParseStart_AcceptsListedValues()
    {
        Assert.True(VoiceSessionStart.TryParse(Frame("moonshine:tiny", "piper", "en_US-amy-low"), out VoiceSessionStartRequest ok, out string error), error);
        Assert.Equal("moonshine:tiny", ok.SttModel);
        Assert.Equal("piper", ok.TtsEngine);
        Assert.Equal("en_US-amy-low", ok.Voice);
    }

    [Fact]
    public void ParseStart_RejectsUnlistedRecognizerEngineAndVoice()
    {
        Assert.False(VoiceSessionStart.TryParse(Frame("/etc/passwd", null, null), out _, out string sttError));
        Assert.Contains("sttModel", sttError);
        Assert.False(VoiceSessionStart.TryParse(Frame(null, "bark", null), out _, out string engineError));
        Assert.Contains("ttsEngine", engineError);
        Assert.False(VoiceSessionStart.TryParse(Frame(null, "piper", "af_heart"), out _, out string voiceError));
        Assert.Contains("voice", voiceError);
    }

    [Fact]
    public void ParseStart_NoNewFields_LeavesThemNull()
    {
        Assert.True(VoiceSessionStart.TryParse(Frame(null, null, null), out VoiceSessionStartRequest request, out string error), error);
        Assert.Null(request.SttModel);
        Assert.Null(request.TtsEngine);
    }
}
