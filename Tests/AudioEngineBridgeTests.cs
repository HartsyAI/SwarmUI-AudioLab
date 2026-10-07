using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
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

    [Fact]
    public void AukProvider_DeclaresIdPrefixAndFlags()
    {
        AudioProviderDefinition def = AukProvider.Instance.GetProvider();
        Assert.Equal("auk_tts", def.Id);
        Assert.Equal("AuK", def.ModelPrefix);
        Assert.Contains("auk_tts_params", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tts_voice_ref", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "flash", "base" }, def.Models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void AukProvider_IsEngineBoundAndSelfDownloading()
    {
        Assert.True(AudioEngineBridge.IsProviderSupported("auk_tts"));
        Assert.True(AudioEngineBridge.ProviderManagesOwnWeights("auk_tts"));
    }

    [Fact]
    public void IndexTts2Provider_ListsBothVersionsAndDeclaresTheEmotionFlag()
    {
        AudioProviderDefinition def = IndexTts2Provider.Instance.GetProvider();
        Assert.Equal("indextts2_tts", def.Id);
        Assert.Contains("indextts2_tts_params", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tts_voice_ref", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "v2_0", "v2_5" }, def.Models.Select(m => m.Id).ToArray());
        Assert.Equal("IndexTeam/IndexTTS-2", def.Models.Single(m => m.Id == "v2_0").EngineConfig["model_name"]);
        Assert.Equal("IndexTeam/IndexTTS-2.5", def.Models.Single(m => m.Id == "v2_5").EngineConfig["model_name"]);
    }

    [Fact]
    public void IndexTts2Provider_IsEngineBoundAndSelfDownloading()
    {
        Assert.True(AudioEngineBridge.IsProviderSupported("indextts2_tts"));
        Assert.True(AudioEngineBridge.ProviderManagesOwnWeights("indextts2_tts"));
    }

    [Fact]
    public void Speech_MapsTheIndexTts2EmotionArgs()
    {
        SpeechRequest withEmotion = AudioEngineRequests.Speech(new Dictionary<string, object>
        {
            ["text"] = "hello", ["emotion_text"] = "furious", ["emotion_alpha"] = 0.6,
        });
        Assert.Equal("furious", withEmotion.EmotionText);
        Assert.Equal(0.6, withEmotion.EmotionAlpha);

        SpeechRequest plain = AudioEngineRequests.Speech(new Dictionary<string, object> { ["text"] = "hello" });
        Assert.Null(plain.EmotionText);
        Assert.Null(plain.EmotionAlpha);
    }

    [Fact]
    public void TryFreeMemory_WithNoEngineBuilt_ReportsSuccess()
    {
        // Nothing is resident, so the idle release counts it as done rather than retrying forever.
        Assert.True(AudioEngineBridge.TryFreeMemory());
    }

    [Fact]
    public async Task FreeMemory_SettlesThePendingIdleTimer()
    {
        // A free-memory request from outside leaves nothing for the idle timer to release, so it is cancelled.
        // Uses its own releaser, so no process-wide state is involved.
        IdleModelReleaser idle = new(() => null, () => true);
        idle.Configure(TimeSpan.FromMinutes(3));
        using (await idle.BeginAsync(CancellationToken.None))
        {
        }
        Task pending = idle.PendingTimer;
        Assert.False(pending.IsCompleted);

        AudioEngineBridge.FreeMemory(idle);

        Assert.Same(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30))));
    }

    [Theory]
    [InlineData(int.MaxValue, 43200)]
    [InlineData(43201, 43200)]
    [InlineData(5, 5)]
    public async Task ConfigureIdle_MapsTheSettingToThePeriod_ClampingTheTop(int minutes, int expectedMinutes)
    {
        List<TimeSpan> spans = [];
        IdleModelReleaser idle = new(() => null, () => true, delay: (span, _) =>
        {
            spans.Add(span);
            return new TaskCompletionSource().Task;
        });
        AudioEngineBridge.ConfigureIdle(idle, minutes);

        using (await idle.BeginAsync(CancellationToken.None))
        {
        }

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), Assert.Single(spans));
    }

    [Fact]
    public async Task ConfigureIdle_ZeroOrNegative_TurnsItOff()
    {
        int delays = 0;
        IdleModelReleaser idle = new(() => null, () => true, delay: (_, _) =>
        {
            delays++;
            return new TaskCompletionSource().Task;
        });
        AudioEngineBridge.ConfigureIdle(idle, -5);
        using (await idle.BeginAsync(CancellationToken.None))
        {
        }
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task WakeWordBuildOptions_RoutesTranscriptionThroughTheIdleReleaser()
    {
        IdleModelReleaser idle = new(() => null, () => true, delay: (_, _) => new TaskCompletionSource().Task);
        WakeWordSettings settings = new() { Port = 12345 };

        HartsyInference.Engine.Audio.Wake.WakeServiceOptions options = WakeWordService.BuildOptions(settings, idle, CancellationToken.None);

        Assert.Equal(12345, options.Port);
        Assert.NotNull(options.TranscribeGate);
        int during = -1;
        string result = await options.TranscribeGate(() =>
        {
            during = idle.ActiveCount;
            return Task.FromResult("text");
        });
        Assert.Equal("text", result);
        Assert.Equal(1, during);
        Assert.Equal(0, idle.ActiveCount);
    }
}
