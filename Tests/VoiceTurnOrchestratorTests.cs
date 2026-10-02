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
/// a detection whose Command is null but whose Transcript is not silently sent <c>Done</c> instead of asking
/// the assistant.
///
/// <para>Today's concrete <c>WakeService</c> happens to only null out <see cref="WakeEvent.Command"/> when
/// <see cref="WakeEvent.Transcript"/> is null too, so that exact combination is not something it currently
/// sends -- but <see cref="WakeEvent.Command"/> is declared <c>string?</c>, nothing here guarantees today's
/// engine is the only producer, and <see cref="WakeWordService.ToJson"/> and its consumer both need to handle
/// a null Command correctly regardless of what set it that way.</para></summary>
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

    [Fact]
    public void ResolveTurnText_NullCommand_FallsBackToTheTranscript()
    {
        // Built through the real producer (ToJson), not a hand-built JObject, so this fails for the actual
        // reason -- ToJson mistagging a null Command -- and not just a test fixture's own shape. A null
        // Command alongside a non-empty Transcript is not a combination today's WakeService happens to send
        // (see this file's class remarks), but WakeEvent.Command is string?, so ToJson and its consumer must
        // handle it correctly regardless of which caller produces it.
        JObject payload = WakeWordService.ToJson(Detection(command: null, transcript: "only a transcript"));

        string text = VoiceTurnOrchestrator.ResolveTurnText(payload);

        Assert.Equal("only a transcript", text);
    }

    [Fact]
    public void ResolveTurnText_NonNullCommand_PrefersItOverTheTranscript()
    {
        JObject payload = WakeWordService.ToJson(
            Detection(command: "the real command", transcript: "hey jarvis the real command"));

        string text = VoiceTurnOrchestrator.ResolveTurnText(payload);

        Assert.Equal("the real command", text);
    }

    [Fact]
    public void ResolveTurnText_EmptyCommand_DoesNotFallBackToTheTranscript()
    {
        // An empty command is the user saying the wake word and nothing else -- not "the engine did not
        // separate them" -- so this must stay "", not the transcript. Pinned here because the obvious
        // alternative fix (treating a null-or-empty command as one case) would route this payload's transcript
        // to the assistant, silently changing this documented, already-shipped behavior.
        JObject payload = WakeWordService.ToJson(Detection(command: "", transcript: "hey jarvis"));

        string text = VoiceTurnOrchestrator.ResolveTurnText(payload);

        Assert.Equal("", text);
    }

    [Fact]
    public void ResolveTurnText_AbsentCommandKey_FallsBackToTheTranscript()
    {
        // The other documented "fall back" case (see WakeWordService.ToJson's own remarks): a command key
        // that is missing entirely, from an engine old enough not to separate command from transcript at all --
        // as opposed to a new engine reporting a separated-but-null command. Built by hand, not via ToJson
        // (which always writes the key), since this models the older wire shape directly.
        JObject payload = new() { ["transcript"] = "only a transcript" };

        string text = VoiceTurnOrchestrator.ResolveTurnText(payload);

        Assert.Equal("only a transcript", text);
    }
}
