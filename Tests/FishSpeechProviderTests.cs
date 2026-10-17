using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Fish Speech in AudioLab: the engine cannot clone with it, so no voice reference is offered.</summary>
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
}
