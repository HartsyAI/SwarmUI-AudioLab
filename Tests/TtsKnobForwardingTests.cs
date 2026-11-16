using Hartsy.Extensions.AudioLab.AudioAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="AudioLabAPI.ForwardTtsKnobs"/>.
///
/// <para>Root cause this guards against: <c>ProcessTTS</c> built its provider args from a fixed list, so a caller's
/// <c>seed</c>, <c>cfg_scale</c> and <c>nfe_step</c> never reached the Engine. The same values sent through
/// <c>ProcessAudio</c> did reach it, so the knobs were lost in this endpoint, not in the Engine.</para></summary>
public class TtsKnobForwardingTests
{
    private static Dictionary<string, object> Forward(string json)
    {
        Dictionary<string, object> args = new() { ["text"] = "hello" };
        AudioLabAPI.ForwardTtsKnobs(JObject.Parse(json), args);
        return args;
    }

    [Fact]
    public void ForwardsTheKnobsTheCallerSent()
    {
        Dictionary<string, object> args = Forward("{\"seed\": 42, \"cfg_scale\": 3.0, \"nfe_step\": 5, \"temperature\": 0.5, \"top_p\": 0.9, \"top_k\": 50}");

        Assert.Equal(42L, args["seed"]);
        Assert.Equal(3.0, args["cfg_scale"]);
        Assert.Equal(5L, args["nfe_step"]);
        Assert.Equal(0.5, args["temperature"]);
        Assert.Equal(0.9, args["top_p"]);
        Assert.Equal(50L, args["top_k"]);
    }

    [Fact]
    public void RequestWithNoKnobsLeavesTheArgsUnchanged()
    {
        Dictionary<string, object> args = Forward("{}");

        Assert.Single(args);
        Assert.Equal("hello", args["text"]);
    }

    [Fact]
    public void NullKnobsAreNotForwarded()
    {
        Dictionary<string, object> args = Forward("{\"seed\": null, \"cfg_scale\": null}");

        Assert.False(args.ContainsKey("seed"));
        Assert.False(args.ContainsKey("cfg_scale"));
    }

    [Fact]
    public void OptionsSpeedIsForwardedOnlyWhenTheCallerSentIt()
    {
        Assert.Equal(1.25, Forward("{\"options\": {\"speed\": 1.25}}")["speed"]);
        Assert.False(Forward("{\"options\": {\"pitch\": 2.0}}").ContainsKey("speed"));
        Assert.False(Forward("{}").ContainsKey("speed"));
    }

    [Fact]
    public void TopLevelSpeedWinsOverOptionsSpeed()
    {
        Assert.Equal(0.9, Forward("{\"speed\": 0.9, \"options\": {\"speed\": 1.25}}")["speed"]);
    }

    [Fact]
    public void InstructionAndNestedClipsAreNotForwardedHere()
    {
        Dictionary<string, object> args = Forward("{\"instruction\": \"warm voice\", \"references\": [], \"history\": [], \"options\": {\"pitch\": 2.0, \"format\": \"wav\"}}");

        Assert.False(args.ContainsKey("instruction"));
        Assert.False(args.ContainsKey("references"));
        Assert.False(args.ContainsKey("history"));
        Assert.False(args.ContainsKey("pitch"));
        Assert.False(args.ContainsKey("format"));
    }
}
