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
    public void Build_ReportsVoicesDefaultAndLlmFlag()
    {
        JObject result = VoiceOptionsCatalog.Build(["am_adam///American English: Adam (M)", "af_heart///American English: Heart (F)"], llmAvailable: true);
        Assert.True(result["success"].Value<bool>());
        Assert.True(result["llm_available"].Value<bool>());
        Assert.Equal("af_heart", result["default_voice"].ToString());
        JArray voices = (JArray)result["voices"];
        Assert.Equal(2, voices.Count);
        Assert.Equal("am_adam", voices[0]["id"].ToString());
        Assert.Equal("Adam (M)", voices[0]["name"].ToString());
        Assert.Equal("American English", voices[0]["group"].ToString());
    }

    [Fact]
    public void Build_DefaultVoiceMissing_FallsBackToTheFirstListedVoice()
    {
        JObject result = VoiceOptionsCatalog.Build(["am_adam///American English: Adam (M)"], llmAvailable: false);
        Assert.Equal("am_adam", result["default_voice"].ToString());
        Assert.False(result["llm_available"].Value<bool>());
    }

    [Fact]
    public void Build_NoVoices_StillNamesTheDefault()
    {
        JObject result = VoiceOptionsCatalog.Build([], llmAvailable: false);
        Assert.Empty((JArray)result["voices"]);
        Assert.Equal(VoiceOptionsCatalog.DefaultVoice, result["default_voice"].ToString());
    }
}
