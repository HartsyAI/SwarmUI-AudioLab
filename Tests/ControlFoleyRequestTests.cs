using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>ControlFoley's video, mask-away-CLIP, negative prompt and reference clip reach the Engine's music request.</summary>
public class ControlFoleyRequestTests
{
    [Fact]
    public void Music_MapsVideoReferenceAndNegativePrompt()
    {
        byte[] video = [1, 2, 3, 4];
        byte[] reference = [5, 6, 7];
        Dictionary<string, object> args = new()
        {
            ["prompt"] = "rain",
            ["negative_prompt"] = "music",
            ["video_data"] = Convert.ToBase64String(video),
            ["video_format"] = ".mp4",
            ["mask_away_clip"] = true,
            ["controlfoley_reference_audio"] = Convert.ToBase64String(reference),
        };

        MusicRequest request = AudioEngineRequests.Music(args);

        Assert.Equal("music", request.NegativePrompt);
        Assert.True(request.MaskAwayClip);
        Assert.Equal(video, request.Video?.Data);
        Assert.Equal("mp4", request.Video?.Format);
        Assert.Equal(reference, request.ReferenceAudio?.Data);
    }

    [Fact]
    public void Music_WithoutControlFoleyInputs_LeavesThemUnset()
    {
        MusicRequest request = AudioEngineRequests.Music(new Dictionary<string, object> { ["prompt"] = "rain" });

        Assert.Null(request.Video);
        Assert.Null(request.ReferenceAudio);
        Assert.False(request.MaskAwayClip);
        Assert.Equal("", request.NegativePrompt);
    }
}
