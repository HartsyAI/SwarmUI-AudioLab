using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceSelectionPolicy.Decide"/>: whether the requested recognizer and speech model can be
/// served by the model set that is (or would be) loaded, given how many sessions are already using it.</summary>
public class VoiceSelectionPolicyTests
{
    private static readonly VoiceModelKey Heart = new("whisper:openai/whisper-small.en", "kokoro:af_heart");
    private static readonly VoiceModelKey Bella = new("whisper:openai/whisper-small.en", "kokoro:af_bella");
    private static readonly VoiceModelKey Moon = new("moonshine:tiny", "kokoro:af_heart");
    private static readonly VoiceModelKey Piper = new("whisper:openai/whisper-small.en", "piper:en_US-lessac-medium");

    [Fact]
    public void NothingLoadedYet_UsesTheRequestedModels_NoRebuildNoNotice()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide(loaded: null, requested: Heart, activeSessions: 0);
        Assert.Equal(Heart, selection.KeyToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Fact]
    public void SameModelsAlreadyLoaded_NoRebuildEvenWithActiveSessions()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide(Heart, Heart, activeSessions: 3);
        Assert.Equal(Heart, selection.KeyToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Theory]
    [InlineData("bella")]
    [InlineData("moonshine")]
    [InlineData("piper")]
    public void DifferentModels_NoActiveSessions_RebuildsForTheNewModels(string which)
    {
        VoiceModelKey requested = which switch { "bella" => Bella, "moonshine" => Moon, _ => Piper };
        VoiceSelection selection = VoiceSelectionPolicy.Decide(Heart, requested, activeSessions: 0);
        Assert.Equal(requested, selection.KeyToUse);
        Assert.True(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Fact]
    public void DifferentVoice_WithActiveSessions_KeepsTheLoadedModelsAndNotices()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide(Heart, Bella, activeSessions: 1);
        Assert.Equal(Heart, selection.KeyToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Contains("af_bella", selection.Notice);
        Assert.Contains("af_heart", selection.Notice);
    }

    [Fact]
    public void DifferentRecognizer_WithActiveSessions_NamesBothRecognizers()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide(Heart, Moon, activeSessions: 2);
        Assert.Equal(Heart, selection.KeyToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Contains("Moonshine tiny", selection.Notice);
        Assert.Contains("Whisper small.en", selection.Notice);
    }

    [Fact]
    public void DifferentLanguage_WhenIdle_Rebuilds_AndWithActiveSessions_KeepsTheLoadedOneAndNames()
    {
        VoiceModelKey english = new("whisper:openai/whisper-small", "kokoro:af_heart");
        VoiceModelKey french = english with { Language = "fr" };

        VoiceSelection idle = VoiceSelectionPolicy.Decide(english, french, activeSessions: 0);
        Assert.Equal(french, idle.KeyToUse);
        Assert.True(idle.NeedsRebuild);

        VoiceSelection busy = VoiceSelectionPolicy.Decide(english, french, activeSessions: 1);
        Assert.Equal(english, busy.KeyToUse);
        Assert.False(busy.NeedsRebuild);
        Assert.Contains("French", busy.Notice);
    }
}
