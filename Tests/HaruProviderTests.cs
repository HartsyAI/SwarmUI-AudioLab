using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioBackends;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Haru-4B in AudioLab: a speech-to-speech provider that reaches the engine's <c>haru</c> descriptor through its duplex entry points, takes only
/// the Q8 build, answers a recording, and reports what it offers over the API.</summary>
public class HaruProviderTests
{
    private static Dictionary<string, object> Args(params (string Key, object Value)[] extra)
    {
        Dictionary<string, object> args = new() { ["user_audio"] = Convert.ToBase64String([1, 2, 3]) };
        foreach ((string key, object value) in extra)
        {
            args[key] = value;
        }
        return args;
    }

    [Fact]
    public void Provider_DeclaresIdCategoryFlagsAndOnlyTheQ8Model()
    {
        AudioProviderDefinition def = HaruProvider.Instance.GetProvider();
        Assert.Equal("haru_s2s", def.Id);
        Assert.Equal("Haru", def.ModelPrefix);
        Assert.Equal(AudioCategory.SpeechToSpeech, def.Category);
        Assert.Contains("tts_voice_ref", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("haru_s2s_params", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        // The shared sampling params would replace the model's own defaults.
        Assert.DoesNotContain("tts_sampling", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        // Lower-bit builds are unintelligible and native is unmeasured, so q8 is the only model; it reaches the engine as haru:q8.
        Assert.Equal(["q8"], def.Models.Select(m => m.Id).ToArray());
        Assert.Equal("https://huggingface.co/higurashi-ai/Haru-4B", def.Models.Single().SourceUrl);
    }

    [Fact]
    public void Category_HasItsFlagItsFolderAndItsShortCode()
    {
        Assert.Equal("audiolab_s2s", DynamicAudioBackend.CategoryFlags[AudioCategory.SpeechToSpeech]);
        // The engine caches it with the speech models.
        Assert.Equal("tts", AudioWeights.CategorySubfolder(AudioCategory.SpeechToSpeech));
    }

    [Fact]
    public void Provider_IsEngineBound_SelfDownloading_AndNotAStreamingSpeechModel()
    {
        Assert.True(AudioEngineBridge.IsProviderSupported("haru_s2s"));
        Assert.True(AudioEngineBridge.ProviderManagesOwnWeights("haru_s2s"));
        Assert.Equal("haru", AudioEngineBridge.EngineIdFor("haru_s2s"));
        Assert.True(AudioEngineBridge.NeedsPrepare("haru_s2s"));
        Assert.False(AudioEngineBridge.NeedsPrepare("kokoro_tts"));
        Assert.False(AudioEngineBridge.SupportsNativeStreaming("haru_s2s"));
    }

    [Fact]
    public void ModelId_DefaultsToQ8_AndAnythingElseIsRefused()
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            Assert.Equal("q8", AudioEngineBridge.WithDuplexModel("haru_s2s", new Dictionary<string, object>())["__model_id"]);
            Assert.Equal("q8", AudioEngineBridge.WithDuplexModel("haru_s2s", new Dictionary<string, object> { ["__model_id"] = " Q8 " })["__model_id"]);
            // A bare engine id would load the native checkpoint, and a path-like string is a local model path to the engine.
            Assert.Throws<ArgumentException>(() => AudioEngineBridge.WithDuplexModel("haru_s2s", new Dictionary<string, object> { ["__model_id"] = "native" }));
            Assert.Throws<ArgumentException>(() => AudioEngineBridge.WithDuplexModel("haru_s2s", new Dictionary<string, object> { ["__model_id"] = "../../etc" }));
            // The caller's other args and the original dictionary are left alone.
            Dictionary<string, object> original = new() { ["card"] = "Ana" };
            IReadOnlyDictionary<string, object> copy = AudioEngineBridge.WithDuplexModel("haru_s2s", original);
            Assert.Equal("Ana", copy["card"]);
            Assert.DoesNotContain("__model_id", original.Keys);
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void DuplexRespond_MapsEveryKnob()
    {
        DuplexRespondArgs mapped = AudioEngineRequests.DuplexRespond(Args(
            ("card", "You are Ana."), ("reference_audio", Convert.ToBase64String([9, 9])), ("guidance", 1.8), ("temperature", 0.5), ("top_k", 40L),
            ("top_p", 0.9), ("repetition_penalty", false), ("eop_bias", 1.5), ("speak_first", true), ("max_frames", 900L), ("seed", 7L),
            ("tail_seconds", 12L), ("stereo", true)));

        Assert.Equal("You are Ana.", mapped.Request.Card);
        Assert.Equal(new byte[] { 9, 9 }, mapped.Request.Reference!.Data);
        Assert.Equal(1.8f, mapped.Request.Guidance);
        Assert.Equal(0.5f, mapped.Request.Temperature);
        Assert.Equal(40, mapped.Request.TopK);
        Assert.Equal(0.9f, mapped.Request.TopP);
        Assert.False(mapped.Request.RepetitionPenalty);
        Assert.Equal(1.5f, mapped.Request.EopBias);
        Assert.True(mapped.Request.SpeakFirst);
        Assert.Equal(900, mapped.Request.MaxFrames);
        Assert.Equal(7, mapped.Request.Seed);
        Assert.Equal(12, mapped.TailSeconds);
        Assert.True(mapped.Stereo);
        Assert.Equal(new byte[] { 1, 2, 3 }, mapped.User.Data);
    }

    [Fact]
    public void DuplexRespond_UsesTheEnginesDefaults_AndClampsWhatCannotBeRight()
    {
        DuplexSpeechRequest defaults = new();
        DuplexRespondArgs plain = AudioEngineRequests.DuplexRespond(Args());
        Assert.Equal(defaults.Guidance, plain.Request.Guidance);
        Assert.Equal(defaults.TopK, plain.Request.TopK);
        Assert.Equal(defaults.RepetitionPenalty, plain.Request.RepetitionPenalty);
        Assert.Equal(defaults.MaxFrames, plain.Request.MaxFrames);
        Assert.Null(plain.Request.Reference);
        Assert.Equal(0, plain.TailSeconds);
        // Swarm's -1 ("random") is not a seed: the engine's own default stands in.
        Assert.Equal(defaults.Seed, AudioEngineRequests.DuplexRespond(Args(("seed", -1L))).Request.Seed);

        DuplexRespondArgs wild = AudioEngineRequests.DuplexRespond(Args(("guidance", 9.0), ("top_k", 0L), ("top_p", 3.0), ("temperature", -1.0), ("eop_bias", 99.0),
            ("max_frames", 999_999_999L), ("tail_seconds", 600L)));
        Assert.Equal(2f, wild.Request.Guidance);
        Assert.Equal(1, wild.Request.TopK);
        Assert.Equal(1f, wild.Request.TopP);
        Assert.Equal(0f, wild.Request.Temperature);
        Assert.Equal(8f, wild.Request.EopBias);
        Assert.Equal(defaults.MaxFrames, wild.Request.MaxFrames);
        Assert.Equal(AudioEngineRequests.MaxTailSeconds, wild.TailSeconds);
    }

    [Fact]
    public void DuplexRespond_RefusesAMissingRecordingAndATooLongCard()
    {
        Assert.Throws<ArgumentException>(() => AudioEngineRequests.DuplexRespond(new Dictionary<string, object> { ["card"] = "Ana" }));
        string tooLong = new('x', AudioEngineRequests.MaxCardCharacters + 1);
        Assert.Throws<ArgumentException>(() => AudioEngineRequests.DuplexRespond(Args(("card", tooLong))));
    }

    [Fact]
    public void Route_ReadsTheFields_AndRefusesWhatItCannotRun()
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            Assert.True(DuplexRequestArgs.TryParse(JObject.Parse("""
                {"user_audio": "AQID", "card": "Ana", "temperature": 0.4, "top_k": 30, "speak_first": true, "model": "q8", "ignored": 1}
                """), out Dictionary<string, object> args, out _, out _));
            Assert.Equal("AQID", args["user_audio"]);
            Assert.Equal("Ana", args["card"]);
            Assert.Equal(0.4, args["temperature"]);
            Assert.Equal(30L, args["top_k"]);
            Assert.Equal(true, args["speak_first"]);
            Assert.Equal("q8", args["__model_id"]);
            Assert.DoesNotContain("ignored", args.Keys);

            Assert.False(DuplexRequestArgs.TryParse(JObject.Parse("""{"card": "Ana"}"""), out _, out _, out string missing));
            Assert.Equal("missing_audio", missing);
            Assert.False(DuplexRequestArgs.TryParse(JObject.Parse("""{"user_audio": "AQID", "model": "native"}"""), out _, out _, out string unknown));
            Assert.Equal("unknown_model", unknown);
            Assert.False(DuplexRequestArgs.TryParse(JObject.Parse("""{"user_audio": "AQID", "model": "C:/models/x"}"""), out _, out _, out string path));
            Assert.Equal("unknown_model", path);
            Assert.False(DuplexRequestArgs.TryParse(JObject.Parse($$"""{"user_audio": "AQID", "card": "{{new string('x', 8001)}}"}"""), out _, out _, out string card));
            Assert.Equal("card_too_long", card);
            Assert.False(DuplexRequestArgs.TryParse(null, out _, out _, out string none));
            Assert.Equal("missing_input", none);
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void Options_ListTheModelWithItsInstallState_AndEveryKnobWithTheEnginesDefault()
    {
        DuplexSpeechRequest defaults = new();
        JObject installed = DuplexOptionsCatalog.Build(HaruProvider.Instance.GetProvider(), (provider, model) => provider == "haru_s2s" && model == "q8");
        Assert.True(installed["success"]!.Value<bool>());
        Assert.Equal("q8", installed["default_model"]!.ToString());
        JObject model = (JObject)Assert.Single((JArray)installed["models"]!);
        Assert.True(model["installed"]!.Value<bool>());
        Assert.Equal(24000, installed["sample_rate"]!.Value<int>());
        Assert.Equal(3200, installed["frame_samples"]!.Value<int>());
        Assert.Equal(133.33, installed["frame_ms"]!.Value<double>());

        JObject knobs = (JObject)installed["knobs"]!;
        Assert.Equal(defaults.Guidance, knobs["guidance"]!["default"]!.Value<float>());
        Assert.Equal(defaults.TopK, knobs["top_k"]!["default"]!.Value<int>());
        Assert.Equal(defaults.RepetitionPenalty, knobs["repetition_penalty"]!["default"]!.Value<bool>());
        Assert.Equal(defaults.MaxFrames, knobs["max_frames"]!["max"]!.Value<int>());
        Assert.Equal(1, knobs["guidance"]!["min"]!.Value<int>());
        Assert.Equal(2, knobs["guidance"]!["max"]!.Value<int>());
        Assert.NotEmpty((JArray)installed["limitations"]!);

        JObject missing = DuplexOptionsCatalog.Build(HaruProvider.Instance.GetProvider(), (_, _) => false);
        Assert.False(((JArray)missing["models"]!)[0]["installed"]!.Value<bool>());
    }

    [Fact]
    public void DuplexAudio_ReturnsTheModelChannelOrBothChannels_WithTheWordsAndTiming()
    {
        float[] model = new float[2400];
        float[] user = new float[2400];
        Array.Fill(model, 0.25f);
        Array.Fill(user, -0.25f);
        DuplexResult result = new(model, user, "hello there", 24000, 9, 0.059, 0.082);

        JObject mono = AudioEngineBridge.DuplexAudio(result, stereo: false);
        Assert.True(mono["success"]!.Value<bool>());
        Assert.Equal("hello there", mono["text"]!.ToString());
        Assert.Equal(0.1, mono["duration"]!.Value<double>(), 3);
        Assert.Equal(59.0, mono["median_frame_ms"]!.Value<double>());
        Assert.Equal(82.0, mono["max_frame_ms"]!.Value<double>());
        byte[] monoWav = Convert.FromBase64String(mono["audio_data"]!.ToString());
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(monoWav, 0, 4));
        Assert.Equal(1, BitConverter.ToInt16(monoWav, 22));

        byte[] stereoWav = Convert.FromBase64String(AudioEngineBridge.DuplexAudio(result, stereo: true)["audio_data"]!.ToString());
        Assert.Equal(2, BitConverter.ToInt16(stereoWav, 22));
    }
}
