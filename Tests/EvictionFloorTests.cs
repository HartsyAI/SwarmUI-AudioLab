using System.Reflection;
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

    /// <summary>The engine loads its settings file on the first knob read, and that load overwrites whatever was set
    /// before it. Here the file says 30, the setting says 20, and the file has not been loaded yet when the setting is
    /// applied, as at the start of a SwarmUI process: the setting has to come out on top.</summary>
    [Fact]
    public void Setting_WinsOverTheEngineSettingsFile_ThatLoadsOnFirstRead()
    {
        string file = Path.Combine(Path.GetTempPath(), $"audiolab-engine-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{\"settings\":{\"vram.audioEvictBelowGb\":30}}");
        string previousPath = KnobFile.ExplicitPath;
        try
        {
            KnobFile.ExplicitPath = file;
            ForgetLoadedSettingsFile();

            DynamicAudioBackend.ApplyEvictionFloor(20);

            Assert.Equal(20L, EngineKnobs.AudioEvictBelowGb.Value);
        }
        finally
        {
            KnobFile.ExplicitPath = previousPath;
            ForgetLoadedSettingsFile();
            File.Delete(file);
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

    /// <summary>Puts the engine's settings back to never loaded, as at process start, so the next knob read loads the
    /// settings file again. The engine exposes no public reset that leaves the file unloaded, hence the reflection.</summary>
    private static void ForgetLoadedSettingsFile()
    {
        FieldInfo loaded = typeof(KnobFile).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(loaded is not null, "KnobFile has no private static _loaded flag any more; reset its lazy load another way.");
        loaded.SetValue(null, false);
        KnobStore.ResetOverrides();
    }
}
