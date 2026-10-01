using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for the bridge mapping (<see cref="AudioEngineBridge"/>'s provider-id lookups) and
/// provider flags (<see cref="AudioProviderDefinitionBuilder"/> output), with no live Engine, no GPU and no
/// model weights. <see cref="AudioProviderRegistry"/> is process-global static state, so every test that reads
/// it registers the real built-in providers itself (<see cref="AudioProviderDefinitions.RegisterAll"/>) in its
/// own try/finally rather than relying on load order, and <c>xunit.runner.json</c> (copied beside this
/// assembly) keeps this collection from running concurrently with a future one that also touches the
/// registry.</summary>
public class AudioEngineBridgeTests
{
    /// <summary>Registers the real built-in providers for the duration of <paramref name="body"/>, then clears
    /// the registry again -- so this test's view of "what's registered" never depends on whether an earlier
    /// test in this run already populated it.</summary>
    private static void WithRegisteredProviders(Action body)
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            body();
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void KokoroProvider_DeclaresTtsStreamingFlag()
    {
        // The flag this PR adds. AudioEngineBridge.SupportsNativeStreaming (the single source of truth that
        // DynamicAudioBackend.GenerateLive reads to pick the native-streaming path over the text-chunk loop)
        // reads it off exactly this provider definition.
        AudioProviderDefinition def = KokoroProvider.Instance.GetProvider();
        Assert.Equal("kokoro_tts", def.Id);
        Assert.Contains("tts_streaming", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportsNativeStreaming_Kokoro_IsTrueAfterTheFlagIsAdded()
    {
        WithRegisteredProviders(() =>
        {
            Assert.True(AudioEngineBridge.SupportsNativeStreaming("kokoro_tts"));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not_a_real_provider_id")]
    public void SupportsNativeStreaming_UnknownOrMissingId_IsFalse(string providerId)
    {
        WithRegisteredProviders(() =>
        {
            Assert.False(AudioEngineBridge.SupportsNativeStreaming(providerId));
        });
    }

    [Fact]
    public void SupportsNativeStreaming_SttBinding_IsFalseEvenIfFlagged()
    {
        // SupportsNativeStreaming is defined for TTS (ISpeechService) only; a Transcribe-bound provider must
        // never report true here even in principle, since AudioEngineBridge.ProcessStreamAsync only ever
        // dispatches to Engine.Speech.
        WithRegisteredProviders(() =>
        {
            Assert.False(AudioEngineBridge.SupportsNativeStreaming("whisper_stt"));
        });
    }

    [Fact]
    public void IsProviderSupported_KnownEngineBoundId_IsTrue()
    {
        Assert.True(AudioEngineBridge.IsProviderSupported("kokoro_tts"));
        Assert.True(AudioEngineBridge.IsProviderSupported("whisper_stt"));
    }

    [Fact]
    public void IsProviderSupported_UnknownOrNullId_IsFalse()
    {
        Assert.False(AudioEngineBridge.IsProviderSupported("not_a_real_provider_id"));
        Assert.False(AudioEngineBridge.IsProviderSupported(null));
    }

    [Fact]
    public void ProviderManagesOwnWeights_Kokoro_IsTrue()
    {
        // The binding table's third field (ManagesOwnWeights) -- Kokoro HF-auto-downloads, so AudioLab must
        // never treat it as a user-placed checkpoint when deciding install state.
        Assert.True(AudioEngineBridge.ProviderManagesOwnWeights("kokoro_tts"));
    }

    [Fact]
    public void RequestKeepResident_TogglingWithNoEngineBuilt_DoesNotThrow()
    {
        // "Keep selected TTS/STT resident": enabling then disabling it must be safe even before any TTS/STT
        // call has ever built the shared Engine (eg right after a fresh backend Init), since ClearResidencyPins
        // runs unconditionally and the pins start out null.
        AudioEngineBridge.RequestKeepResident(true);
        AudioEngineBridge.RequestKeepResident(false);
    }
}
