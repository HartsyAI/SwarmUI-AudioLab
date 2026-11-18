using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Word-timing plumbing without an engine: the request mapping, the shape of the words in a transcription result,
/// and how <see cref="LyricsAlignment"/> routes between the model that transcribes and the model that times words.</summary>
public class LyricsAlignmentTests
{
    private static readonly JArray OneWord = [new JObject { ["word"] = "hi", ["start"] = 0.0, ["end"] = 0.4 }];

    private static JObject Ok(string text, JArray words = null) => new()
    {
        ["success"] = true,
        ["text"] = text,
        ["words"] = words ?? new JArray(),
        ["duration"] = 3.5,
        ["language"] = "en",
    };

    [Fact]
    public void TranscribeRequest_MapsWordTimestampsAndAlignText_AndTreatsBlankTextAsNone()
    {
        Dictionary<string, object> args = new() { ["audio_data"] = Convert.ToBase64String(new byte[] { 1 }), ["word_timestamps"] = true, ["align_text"] = "hold on\nlet go" };
        AudioRequest asked = AudioEngineRequests.Transcribe(args);
        Assert.True(asked.WordTimestamps);
        Assert.Equal("hold on\nlet go", asked.AlignText);

        args["align_text"] = "   ";
        args.Remove("word_timestamps");
        AudioRequest plain = AudioEngineRequests.Transcribe(args);
        Assert.False(plain.WordTimestamps);
        Assert.Null(plain.AlignText);
    }

    [Fact]
    public void TranscriptionResult_ListsWordsAsWordStartEnd_AndAnEmptyArrayWithout()
    {
        JObject timed = AudioIo.TranscriptionResult("hi", "en", [new WordSegment { Word = "hi", Start = 0.0, End = 0.4 }], 3.5);
        Assert.Equal("hi", (string)timed["words"][0]["word"]);
        Assert.Equal(0.4, (double)timed["words"][0]["end"]);
        Assert.Equal(3.5, (double)timed["duration"]);

        JObject untimed = AudioIo.TranscriptionResult("hi", "en");
        Assert.Empty((JArray)untimed["words"]);
    }

    [Fact]
    public async Task GivenLyrics_OnlyTheAlignerRuns_WithTheLyricsAsKnownText()
    {
        List<SttCall> calls = [];
        JObject input = new() { ["lyrics"] = "hold on", ["align_model"] = "base" };

        JObject result = await LyricsAlignment.RunAsync(input, call => { calls.Add(call); return Task.FromResult(Ok("ignored", OneWord)); });

        SttCall only = Assert.Single(calls);
        Assert.Equal(new SttCall("whisper_stt", "base", true, "hold on"), only);
        Assert.Equal("aligned", (string)result["source"]);
        Assert.Equal("hold on", (string)result["text"]);
        Assert.Equal("base", (string)result["model"]);
    }

    [Fact]
    public async Task WithoutLyrics_ATranscriberThatTimesWordsIsUsedAlone()
    {
        List<SttCall> calls = [];
        JObject input = new() { ["stt_provider_id"] = "distilwhisper_stt", ["stt_model"] = "large-v3" };

        JObject result = await LyricsAlignment.RunAsync(input, call => { calls.Add(call); return Task.FromResult(Ok("hi", OneWord)); });

        Assert.Equal(new SttCall("distilwhisper_stt", "large-v3", true, null), Assert.Single(calls));
        Assert.Equal("stt", (string)result["source"]);
        Assert.Equal("distilwhisper_stt", (string)result["provider_id"]);
    }

    [Fact]
    public async Task WithoutLyrics_ATranscriberWithNoWordTiming_HandsItsTextToTheAligner()
    {
        List<SttCall> calls = [];
        JObject input = new() { ["stt_provider_id"] = "moonshine_stt", ["align_provider_id"] = "whisper_stt" };

        JObject result = await LyricsAlignment.RunAsync(input, call =>
        {
            calls.Add(call);
            return Task.FromResult(call.ProviderId == "moonshine_stt" ? Ok(" hold on ") : Ok("ignored", OneWord));
        });

        Assert.Equal(2, calls.Count);
        Assert.Equal("moonshine_stt", calls[0].ProviderId);
        Assert.Equal(new SttCall("whisper_stt", null, true, " hold on "), calls[1]);
        Assert.Equal("stt+aligned", (string)result["source"]);
        Assert.Equal("hold on", (string)result["text"]);
        Assert.Single((JArray)result["words"]);
    }

    [Fact]
    public async Task WithoutLyrics_NoSpeechReturnsEmptyWordsWithoutCallingTheAligner()
    {
        int calls = 0;

        JObject result = await LyricsAlignment.RunAsync(new JObject(), _ => { calls++; return Task.FromResult(Ok("  ")); });

        Assert.Equal(1, calls);
        Assert.True((bool)result["success"]);
        Assert.Empty((JArray)result["words"]);
    }

    [Fact]
    public async Task AFailedCallIsReturnedAsItCame()
    {
        JObject failure = new() { ["success"] = false, ["error"] = "'x' is not a model of 'whisper_stt'." };

        JObject result = await LyricsAlignment.RunAsync(new JObject { ["lyrics"] = "hi", ["align_model"] = "x" }, _ => Task.FromResult(failure));

        Assert.Same(failure, result);
    }

    [Fact]
    public async Task GivenLyrics_AnAlignerThatTimesNothingIsAnErrorNotAnEmptySuccess()
    {
        JObject result = await LyricsAlignment.RunAsync(new JObject { ["lyrics"] = "hold on", ["align_provider_id"] = "moonshine_stt" },
            _ => Task.FromResult(Ok("hold on")));

        Assert.False((bool)result["success"]);
        Assert.Equal("no_word_timing", (string)result["error_code"]);
    }

    [Fact]
    public async Task AResultWithNullWordsAndNullText_IsTreatedAsNoWordsAndNoText()
    {
        JObject nullWords = new() { ["success"] = true, ["text"] = null, ["words"] = JValue.CreateNull(), ["duration"] = null };
        int calls = 0;

        JObject result = await LyricsAlignment.RunAsync(new JObject(), _ => { calls++; return Task.FromResult(nullWords); });

        Assert.Equal(1, calls);
        Assert.True((bool)result["success"]);
        Assert.Equal("", (string)result["text"]);
        Assert.Empty((JArray)result["words"]);
        Assert.Equal(0.0, (double)result["duration"]);
    }

    [Fact]
    public async Task ATranscriberWithNullWords_StillHandsItsTextToTheAligner()
    {
        List<SttCall> calls = [];
        JObject untimed = new() { ["success"] = true, ["text"] = "hold on", ["words"] = JValue.CreateNull() };

        JObject result = await LyricsAlignment.RunAsync(new JObject { ["stt_provider_id"] = "moonshine_stt" }, call =>
        {
            calls.Add(call);
            return Task.FromResult(call.AlignText is null ? untimed : Ok("x", OneWord));
        });

        Assert.Equal(2, calls.Count);
        Assert.Equal("stt+aligned", (string)result["source"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    public void ReadFlag_AcceptsBoolStringAndNumberForms(string text, bool expected)
    {
        Assert.Equal(expected, AudioIo.ReadFlag(JValue.CreateString(text)));
    }

    [Fact]
    public void ReadFlag_ReadsJsonBoolsAndNumbers_AndNeverThrowsOnNullOrMissing()
    {
        Assert.True(AudioIo.ReadFlag(new JValue(true)));
        Assert.False(AudioIo.ReadFlag(new JValue(false)));
        Assert.True(AudioIo.ReadFlag(new JValue(1)));
        Assert.False(AudioIo.ReadFlag(new JValue(0)));
        Assert.True(AudioIo.ReadFlag(new JValue(1.5)));
        Assert.False(AudioIo.ReadFlag(JValue.CreateNull()));
        Assert.False(AudioIo.ReadFlag(null));
        Assert.False(AudioIo.ReadFlag(new JArray()));
    }

    [Fact]
    public void TranscriptionResult_WithNullTextAndNoWords_IsStillWellFormed()
    {
        JObject result = AudioIo.TranscriptionResult(null, "en", null);

        Assert.Equal("", (string)result["text"]);
        Assert.Empty((JArray)result["words"]);
        Assert.Equal(0.0, (double)result["duration"]);
    }
}
