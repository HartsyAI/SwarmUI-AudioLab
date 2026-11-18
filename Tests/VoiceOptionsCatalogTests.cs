using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceOptionsCatalog"/> -- the data behind the Voice Agent tab's setup panel.</summary>
public class VoiceOptionsCatalogTests
{
    [Fact]
    public void ParseVoices_SplitsIdLanguageAndName()
    {
        List<VoiceChoice> voices = VoiceOptionsCatalog.ParseVoices(["af_heart///American English: Heart (F)"]);
        VoiceChoice voice = Assert.Single(voices);
        Assert.Equal("af_heart", voice.Id);
        Assert.Equal("Heart (F)", voice.Name);
        Assert.Equal("American English", voice.Group);
    }

    [Fact]
    public void ParseVoices_NoLabel_UsesTheIdAndTheOtherGroup()
    {
        VoiceChoice voice = Assert.Single(VoiceOptionsCatalog.ParseVoices(["bare_voice"]));
        Assert.Equal("bare_voice", voice.Id);
        Assert.Equal("bare_voice", voice.Name);
        Assert.Equal("Other", voice.Group);
    }

    [Fact]
    public void ParseVoices_LabelWithoutLanguage_KeepsTheWholeLabel()
    {
        VoiceChoice voice = Assert.Single(VoiceOptionsCatalog.ParseVoices(["x///Plain label"]));
        Assert.Equal("Plain label", voice.Name);
        Assert.Equal("Other", voice.Group);
    }

    [Fact]
    public void ParseVoices_SkipsBlankAndIdlessEntries()
    {
        Assert.Empty(VoiceOptionsCatalog.ParseVoices(["", "   ", "///label only", null]));
        Assert.Empty(VoiceOptionsCatalog.ParseVoices(null));
    }

    [Fact]
    public void RealKokoroList_ParsesEveryEntry_WithNoDuplicateIds()
    {
        List<VoiceChoice> voices = VoiceOptionsCatalog.ParseVoices(AudioLabParams.KokoroVoices);
        Assert.Equal(AudioLabParams.KokoroVoices.Count, voices.Count);
        Assert.Equal(voices.Count, voices.Select(v => v.Id).Distinct().Count());
        Assert.All(voices, v => Assert.NotEqual("Other", v.Group));
        Assert.Contains(voices, v => v.Id == VoiceOptionsCatalog.DefaultVoice);
    }

    [Fact]
    public void Build_ListsRecognizersAndEnginesWithTheirVoices()
    {
        JObject result = VoiceOptionsCatalog.Build(llmAvailable: true);
        Assert.True(result["success"].Value<bool>());
        Assert.True(result["llm_available"].Value<bool>());
        Assert.Equal(VoiceEngineCatalog.DefaultStt, result["default_stt"].ToString());
        Assert.Equal(VoiceEngineCatalog.DefaultTtsEngine, result["default_tts"].ToString());
        Assert.Equal(VoiceEngineCatalog.Stt.Count, ((JArray)result["stt"]).Count);
        JArray engines = (JArray)result["tts"];
        Assert.Equal(VoiceEngineCatalog.TtsEngines.Count, engines.Count);
        JToken kokoro = engines.First(e => e["engine"].ToString() == "kokoro");
        Assert.Equal("af_heart", kokoro["default_voice"].ToString());
        Assert.Equal(AudioLabParams.KokoroVoices.Count, ((JArray)kokoro["voices"]).Count);
        JToken piper = engines.First(e => e["engine"].ToString() == "piper");
        Assert.Equal("en_US-lessac-medium", piper["default_voice"].ToString());
        Assert.Equal(AudioLabParams.PiperVoices.Length, ((JArray)piper["voices"]).Count);
        Assert.Equal("piper_tts", piper["provider_id"].ToString());
    }

    [Fact]
    public void Build_LlmFlagFollowsTheArgument()
    {
        Assert.False(VoiceOptionsCatalog.Build(llmAvailable: false)["llm_available"].Value<bool>());
    }

    [Fact]
    public void Build_EveryEngineDefaultVoiceIsOneOfItsVoices()
    {
        JArray engines = (JArray)VoiceOptionsCatalog.Build(llmAvailable: false)["tts"];
        foreach (JToken engine in engines)
        {
            string defaultVoice = engine["default_voice"].ToString();
            Assert.Contains(((JArray)engine["voices"]).Select(v => v["id"].ToString()), id => id == defaultVoice);
        }
    }
}
