using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="AudioConfiguration.ResolveVoice"/> -- the mapping that fixes
/// <c>ProcessTTS</c> (and three sibling callers) sending Piper a bare "piper.onnx" request.
///
/// <para>Root cause this guards against: an unparameterised TTS request carries no voice, so
/// <c>TTSRequest.Voice</c> defaults to the generic <see cref="AudioConfiguration.DefaultVoice"/> ("default")
/// sentinel. <c>AudioEngineRequests.Speech</c> correctly turns that sentinel into <c>null</c> for the Engine's
/// <c>SpeechRequest.Voice</c>, but the Engine's own selector (<c>AudioModelSelector.Parse</c>) separately
/// falls back to the bare catalog token ("piper") whenever no named voice reaches it, and Piper's loader
/// only special-cases the literal string "default" -- not its own id. So "piper" ends up treated as a voice,
/// and the Engine 404s fetching "rhasspy/piper-voices/piper.onnx". The fix is upstream of all of that: never
/// let an unparameterised Piper request leave AudioLab without a real voice name.</para></summary>
public class AudioConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void ResolveVoice_NoRealVoice_Piper_ReturnsTheEngineDefault(string requestedVoice)
    {
        Assert.Equal("en_US-lessac-medium", AudioConfiguration.ResolveVoice(requestedVoice, "piper_tts"));
    }

    [Theory]
    [InlineData("Piper_TTS")]
    [InlineData("PIPER_TTS")]
    public void ResolveVoice_ProviderIdIsCaseInsensitive(string providerId)
    {
        Assert.Equal("en_US-lessac-medium", AudioConfiguration.ResolveVoice(null, providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public void ResolveVoice_NoRealVoice_NonPiperProvider_KeepsTheGenericPlaceholder(string requestedVoice)
    {
        // Zero-shot providers (VibeVoice, Dia, F5, Kokoro, ...) pick their own speaker from the generic
        // placeholder; they must never receive a Piper voice name by accident.
        Assert.Equal(AudioConfiguration.DefaultVoice, AudioConfiguration.ResolveVoice(requestedVoice, "vibevoice_tts"));
        Assert.Equal(AudioConfiguration.DefaultVoice, AudioConfiguration.ResolveVoice(requestedVoice, "kokoro_tts"));
    }

    [Fact]
    public void ResolveVoice_NoRealVoice_NullProviderId_KeepsTheGenericPlaceholder()
    {
        // A missing/unregistered provider id must not crash the comparison.
        Assert.Equal(AudioConfiguration.DefaultVoice, AudioConfiguration.ResolveVoice("default", null));
    }

    [Theory]
    [InlineData("en_GB-vctk-medium")]
    [InlineData("en_US-amy-low")]
    [InlineData("en_US-hfc_female-medium")]
    public void ResolveVoice_NamedVoice_Piper_PassesThroughUnchanged(string requestedVoice)
    {
        Assert.Equal(requestedVoice, AudioConfiguration.ResolveVoice(requestedVoice, "piper_tts"));
    }

    [Fact]
    public void ResolveVoice_NamedVoice_NonPiperProvider_PassesThroughUnchanged()
    {
        Assert.Equal("af_heart", AudioConfiguration.ResolveVoice("af_heart", "kokoro_tts"));
    }

    [Fact]
    public void ResolveVoice_NamedVoiceThatHappensToBeWhitespacePadded_IsStillConsideredNamed()
    {
        // Deliberately NOT trimmed -- a caller-supplied real voice id is passed through byte for byte, since
        // trimming is not this method's job and the Engine/HF path matches exact file names.
        Assert.Equal("en_US-amy-medium", AudioConfiguration.ResolveVoice("en_US-amy-medium", "piper_tts"));
    }
}
