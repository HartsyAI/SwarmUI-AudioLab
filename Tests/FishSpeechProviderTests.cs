using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Fish Speech in AudioLab: the engine cannot clone with it, so no voice reference is offered and one sent anyway is
/// refused before the engine is reached.</summary>
public class FishSpeechProviderTests
{
    [Fact]
    public void Provider_DoesNotOfferAVoiceReference()
    {
        AudioProviderDefinition def = FishSpeechProvider.Instance.GetProvider();
        Assert.Equal("fishspeech_tts", def.Id);
        Assert.Contains("fishspeech_tts_params", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("tts_voice_ref", def.FeatureFlags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "fish-speech-1.5" }, def.Models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void ReferenceRejection_RefusesAClipOrAReferencesList()
    {
        Dictionary<string, object> clip = new() { ["text"] = "Hello.", ["reference_audio"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
        Dictionary<string, object> list = new()
        {
            ["text"] = "Hello.",
            ["references"] = new JArray(new JObject { ["text"] = "Hi.", ["audio"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }),
        };
        Assert.Contains("Firefly", AudioEngineBridge.ReferenceRejection("fishspeech_tts", clip));
        Assert.Contains("Firefly", AudioEngineBridge.ReferenceRejection("fishspeech_tts", list));
    }

    [Fact]
    public void ReferenceRejection_LetsAPlainRequestAndCloningProvidersThrough()
    {
        Dictionary<string, object> plain = new() { ["text"] = "Hello.", ["reference_audio"] = "", ["references"] = new JArray() };
        Dictionary<string, object> clip = new() { ["text"] = "Hello.", ["reference_audio"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
        Assert.Null(AudioEngineBridge.ReferenceRejection("fishspeech_tts", plain));
        Assert.Null(AudioEngineBridge.ReferenceRejection("fishaudio_drama3_tts", clip));
        Assert.Null(AudioEngineBridge.ReferenceRejection("f5_tts", clip));
    }
}
