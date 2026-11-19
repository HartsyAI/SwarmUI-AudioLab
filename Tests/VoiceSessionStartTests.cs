using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceSessionStart.TryParse"/> -- the validation of a client's <c>start</c>
/// handshake frame, independent of any socket.</summary>
public class VoiceSessionStartTests
{
    private static JObject FullValid() => new()
    {
        ["model"] = "qwen3",
        ["assistantId"] = "assistant-1",
        ["voice"] = "af_heart",
        ["systemPrompt"] = "Be terse.",
        ["bargeIn"] = false,
        ["inputRate"] = 48000,
    };

    [Fact]
    public void ValidFullMessage_ParsesEveryField()
    {
        Assert.True(VoiceSessionStart.TryParse(FullValid(), out VoiceSessionStartRequest request, out string error), error);
        Assert.Equal("qwen3", request.Model);
        Assert.Equal("assistant-1", request.AssistantId);
        Assert.Equal("af_heart", request.Voice);
        Assert.Equal("Be terse.", request.SystemPrompt);
        Assert.False(request.BargeIn);
        Assert.Equal(48000, request.InputRate);
    }

    [Fact]
    public void MinimalMessage_OnlyModelAndInputRate_DefaultsTheRest()
    {
        JObject minimal = new() { ["model"] = "qwen3", ["inputRate"] = 16000 };
        Assert.True(VoiceSessionStart.TryParse(minimal, out VoiceSessionStartRequest request, out string error), error);
        Assert.Null(request.AssistantId);
        Assert.Null(request.Voice);
        Assert.Null(request.SystemPrompt);
        Assert.True(request.BargeIn); // defaults to true
    }

    [Fact]
    public void NullRawInput_Fails()
    {
        Assert.False(VoiceSessionStart.TryParse(null, out _, out string error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrMissingModel_Fails(string model)
    {
        JObject input = FullValid();
        if (model is null)
        {
            input.Remove("model");
        }
        else
        {
            input["model"] = model;
        }
        Assert.False(VoiceSessionStart.TryParse(input, out _, out string error));
        Assert.Contains("model", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingInputRate_Fails()
    {
        JObject input = FullValid();
        input.Remove("inputRate");
        Assert.False(VoiceSessionStart.TryParse(input, out _, out string error));
        Assert.Contains("inputRate", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(999999)]
    [InlineData(0)]
    [InlineData(-16000)]
    public void OutOfRangeInputRate_Fails(int rate)
    {
        JObject input = FullValid();
        input["inputRate"] = rate;
        Assert.False(VoiceSessionStart.TryParse(input, out _, out string error));
    }

    [Fact]
    public void APathologicalButInRangeInputRate_FailsOnTheFrameSizeCap()
    {
        JObject input = FullValid();
        input["inputRate"] = 16001; // in [8000,192000] but coprime-ish with 16000.
        Assert.False(VoiceSessionStart.TryParse(input, out _, out string error));
        Assert.NotNull(error);
    }

    [Fact]
    public void NonBooleanBargeIn_Fails()
    {
        JObject input = FullValid();
        input["bargeIn"] = "not-a-bool";
        Assert.False(VoiceSessionStart.TryParse(input, out _, out string error));
        Assert.Contains("bargeIn", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOptionalFields_BecomeNull(string blank)
    {
        JObject input = FullValid();
        input["assistantId"] = blank;
        input["voice"] = blank;
        input["systemPrompt"] = blank;
        Assert.True(VoiceSessionStart.TryParse(input, out VoiceSessionStartRequest request, out string error), error);
        Assert.Null(request.AssistantId);
        Assert.Null(request.Voice);
        Assert.Null(request.SystemPrompt);
    }

    private static JObject LanguageFrame(string stt, string language)
    {
        JObject input = FullValid();
        input["voice"] = "af_heart";
        if (stt is not null)
        {
            input["sttModel"] = stt;
        }
        input["language"] = language;
        return input;
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("en_US", "en")]
    public void ParseStart_MultilingualRecognizer_TakesALanguageAndNormalizesIt(string sent, string expected)
    {
        Assert.True(VoiceSessionStart.TryParse(LanguageFrame("whisper:openai/whisper-small", sent), out VoiceSessionStartRequest request, out string error), error);
        Assert.Equal(expected, request.Language);
    }

    [Fact]
    public void ParseStart_NoLanguage_LeavesItNull()
    {
        Assert.True(VoiceSessionStart.TryParse(FullValid(), out VoiceSessionStartRequest request, out string error), error);
        Assert.Null(request.Language);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("zz")]
    [InlineData("../en")]
    public void ParseStart_UnlistedLanguage_Fails(string language)
    {
        Assert.False(VoiceSessionStart.TryParse(LanguageFrame("whisper:openai/whisper-small", language), out _, out string error));
        Assert.Contains("language", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("moonshine:base")]
    [InlineData("whisper:openai/whisper-small.en")]
    public void ParseStart_EnglishOnlyRecognizer_RefusesAnotherLanguageButAcceptsEnglish(string stt)
    {
        Assert.False(VoiceSessionStart.TryParse(LanguageFrame(stt, "fr"), out _, out string error));
        Assert.Contains("multilingual", error);
        Assert.True(VoiceSessionStart.TryParse(LanguageFrame(stt, "en"), out VoiceSessionStartRequest request, out string okError), okError);
        Assert.Equal("en", request.Language);
    }
}
