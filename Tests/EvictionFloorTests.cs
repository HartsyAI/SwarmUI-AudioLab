using Hartsy.Extensions.AudioLab.AudioBackends;
using HartsyInference.Core.Configuration;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>The Evict Below Gb setting reaches the engine through its knob registry. It used to be exported as the
/// <c>HARTSY_AUDIO_EVICT_BELOW_GB</c> environment variable after the engine had stopped reading the environment, so
/// the setting changed nothing and the engine always evicted at its 14 GB default.</summary>
public class EvictionFloorTests
{
    [Fact]
    public void PositiveSetting_SetsTheEngineKnob()
    {
        try
        {
            DynamicAudioBackend.ApplyEvictionFloor(20);

            Assert.Equal(20L, EngineKnobs.AudioEvictBelowGb.Value);
            Assert.Equal("host", KnobStore.SourceOf(EngineKnobs.AudioEvictBelowGb.Id));
        }
        finally
        {
            KnobStore.Clear(EngineKnobs.AudioEvictBelowGb);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void UnsetSetting_LeavesTheEngineValueAlone(int gb)
    {
        KnobStore.Set(EngineKnobs.AudioEvictBelowGb, 9L);
        try
        {
            DynamicAudioBackend.ApplyEvictionFloor(gb);

            Assert.Equal(9L, EngineKnobs.AudioEvictBelowGb.Value);
        }
        finally
        {
            KnobStore.Clear(EngineKnobs.AudioEvictBelowGb);
        }
    }
}
