using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="VoiceSelectionPolicy.Decide"/>: whether a requested Kokoro voice can be
/// served by the model set that is (or would be) loaded, given how many sessions are already using it.</summary>
public class VoiceSelectionPolicyTests
{
    [Fact]
    public void NothingLoadedYet_UsesTheRequestedVoice_NoRebuildNoNotice()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide(loadedVoice: null, requestedVoice: "af_heart", activeSessions: 0);
        Assert.Equal("af_heart", selection.VoiceToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Fact]
    public void SameVoiceAlreadyLoaded_NoRebuildEvenWithActiveSessions()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide("af_heart", "af_heart", activeSessions: 3);
        Assert.Equal("af_heart", selection.VoiceToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Fact]
    public void DifferentVoice_NoActiveSessions_RebuildsForTheNewVoice()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide("af_heart", "af_bella", activeSessions: 0);
        Assert.Equal("af_bella", selection.VoiceToUse);
        Assert.True(selection.NeedsRebuild);
        Assert.Null(selection.Notice);
    }

    [Fact]
    public void DifferentVoice_WithActiveSessions_KeepsTheLoadedVoiceAndNotices()
    {
        VoiceSelection selection = VoiceSelectionPolicy.Decide("af_heart", "af_bella", activeSessions: 1);
        Assert.Equal("af_heart", selection.VoiceToUse);
        Assert.False(selection.NeedsRebuild);
        Assert.NotNull(selection.Notice);
        Assert.Contains("af_bella", selection.Notice);
        Assert.Contains("af_heart", selection.Notice);
    }
}
