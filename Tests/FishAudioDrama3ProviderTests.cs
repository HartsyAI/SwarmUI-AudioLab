using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Fish Audio Drama 3 in AudioLab: the provider reaches the engine's <c>fishaudio-drama3</c> descriptor with model ids that
/// are its variant tokens, streams natively, and carries voice references and revision history in and the take's turns out.</summary>
public class FishAudioDrama3ProviderTests
{
    [Fact]
    public void Provider_DeclaresIdPrefixFlagsAndVariantModelIds()
    {
        AudioProviderDefinition def = FishAudioDrama3Provider.Instance.GetProvider();
        Assert.Equal("fishaudio_drama3_tts", def.Id);
        Assert.Equal("FishAudioDrama3", def.ModelPrefix);
        Assert.Contains("tts_voice_ref", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tts_streaming", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("fishaudio_drama3_tts_params", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        // The shared sampling params would replace the model's own defaults on every take.
        Assert.DoesNotContain("tts_sampling", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        // Each id reaches the engine as fishaudio-drama3:{id}, which accepts only build and precision tokens.
        Assert.Equal(new[] { "native", "int4" }, def.Models.Select(m => m.Id).ToArray());
        Assert.Equal("https://huggingface.co/fishaudio/drama-3-int4", def.Models.Single(m => m.Id == "int4").SourceUrl);
    }

    [Fact]
    public void Provider_IsEngineBound_SelfDownloading_AndStreamsNatively()
    {
        Assert.True(AudioEngineBridge.IsProviderSupported("fishaudio_drama3_tts"));
        Assert.True(AudioEngineBridge.ProviderManagesOwnWeights("fishaudio_drama3_tts"));
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            Assert.True(AudioEngineBridge.SupportsNativeStreaming("fishaudio_drama3_tts"));
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void Speech_MapsReferencesAsAudio_AndHistoryAsCodes()
    {
        byte[] clip = [1, 2, 3];
        SpeechRequest request = AudioEngineRequests.Speech(new Dictionary<string, object>
        {
            ["text"] = "<|speaker:1|> [whispering] Come closer.",
            // An API call's JSON array arrives as a JArray; a Swarm param's as its text.
            ["references"] = new JArray(new JObject { ["text"] = "<|speaker:1|> Hello there.", ["audio"] = Convert.ToBase64String(clip) }),
            ["history"] = """[{"text": "Come closer.", "codes": [[5, 6], [1, 2]]}]""",
        });

        SpeechTurn reference = Assert.Single(request.References!);
        Assert.Equal("<|speaker:1|> Hello there.", reference.Text);
        Assert.Equal(clip, reference.Audio!.Data);
        SpeechTurn turn = Assert.Single(request.History!);
        Assert.Equal("Come closer.", turn.Text);
        Assert.Equal(new[] { new[] { 5, 6 }, new[] { 1, 2 } }, turn.Codes);
        Assert.Null(turn.Audio);
    }

    [Fact]
    public void Speech_WithoutTurns_LeavesBothUnset()
    {
        SpeechRequest request = AudioEngineRequests.Speech(new Dictionary<string, object> { ["text"] = "hello", ["history"] = "  " });
        Assert.Null(request.References);
        Assert.Null(request.History);
    }

    [Fact]
    public void ReturnedTurns_ReadBackAsTheSameHistory()
    {
        SpeechTurn[] take = [new() { Text = "First.", Codes = [[7, 8, 9], [3, 4, 5]] }, new() { Text = "Second.", Codes = [[1], [2]] }];
        JArray json = SpeechTurnJson.ToJson(take);
        IReadOnlyList<SpeechTurn> history = SpeechTurnJson.Parse(new Dictionary<string, object> { ["history"] = json.ToString() }, "history")!;
        Assert.Equal(take.Select(static t => t.Text), history.Select(static t => t.Text));
        Assert.Equal(take.Select(static t => t.Codes), history.Select(static t => t.Codes));
    }

    [Fact]
    public async Task ProcessAudio_RefusesAnOversizedClipNestedInTurns()
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            JObject input = new()
            {
                ["provider_id"] = "fishaudio_drama3_tts",
                ["args"] = new JObject
                {
                    ["text"] = "<|speaker:1|> Come closer.",
                    ["references"] = new JArray(new JObject { ["text"] = "<|speaker:1|> Hello there.", ["audio"] = new string('A', AudioLabAPI.MaxArgStringLength + 1) }),
                },
            };
            JObject result = await AudioLabAPI.ProcessAudio(null, input);
            Assert.False(result["success"]!.Value<bool>());
            Assert.Equal("arg_too_large", result["error_code"]!.ToString());
            Assert.StartsWith("args.references[0].audio ", result["error"]!.ToString());
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Theory]
    [InlineData("""{"text": "not an array"}""")]
    [InlineData("""[{"codes": [[1]]}]""")]
    [InlineData("""[{"text": "no sound"}]""")]
    public void MalformedTurns_AreRefused(string json) =>
        Assert.Throws<ArgumentException>(() => SpeechTurnJson.Parse(new Dictionary<string, object> { ["history"] = json }, "history"));
}
