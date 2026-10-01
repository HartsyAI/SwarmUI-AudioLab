using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Audio.Wake;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for the wake-word "null command" bug: <see cref="WakeWordService.ToJson"/> builds the
/// wire payload's <c>command</c> field via <c>["command"] = evt.Command</c>, and when
/// <see cref="WakeEvent.Command"/> is a genuine <see langword="null"/>, Newtonsoft's implicit
/// <see langword="string"/> conversion tags the resulting token <see cref="JTokenType.String"/> with a
/// <see langword="null"/> <c>Value</c> -- never <see cref="JTokenType.Null"/>. The consumer used to fall back
/// to the transcript by checking <c>Type == JTokenType.Null</c>, which that mistagged token never matches, so
/// a detection where the engine did not separate command from transcript silently sent <c>Done</c> instead of
/// asking the assistant.</summary>
public class VoiceTurnOrchestratorTests
{
    private static WakeEvent Detection(string command, string transcript) => new()
    {
        DeviceId = "sat-1",
        Word = "hey jarvis",
        Score = 0.95f,
        Command = command,
        Transcript = transcript,
    };

    [Fact]
    public void ToJson_NullCommand_ProducesARealJsonNull_NotAStringTypedNull()
    {
        JObject payload = WakeWordService.ToJson(Detection(command: null, transcript: "only a transcript"));

        JToken commandToken = payload["command"];

        Assert.Equal(JTokenType.Null, commandToken.Type);
    }
}
