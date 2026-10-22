using System.IO;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Demucs variants share one folder, so presence and installs must be judged per model file.</summary>
public class DemucsPresenceTests
{
    [Fact]
    public void Demucs_EachModelIsItsOwnFile_AndOneDoesNotAnswerForAnother()
    {
        string root = Path.Combine(Path.GetTempPath(), "audiolab-demucs-" + Guid.NewGuid().ToString("N"));
        string previous = AudioConfiguration.ModelRoot;
        AudioConfiguration.ModelRoot = root;
        AudioProviderRegistry.Clear();
        AudioProviderDefinitions.RegisterAll();
        try
        {
            string folder = Path.Combine(root, "fx", "demucs");
            IReadOnlyList<string> sixStem = AudioEngineBridge.GetWeightLocations("demucs_fx", "htdemucs_6s");
            Assert.Single(sixStem);
            Assert.EndsWith(Path.Combine("fx", "demucs", "htdemucs_6s.th"), sixStem[0]);
            Assert.False(AudioEngineBridge.WeightsPresent("demucs_fx", "htdemucs_6s"));

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "htdemucs.th"), new byte[2_000_000]);
            Assert.True(AudioEngineBridge.WeightsPresent("demucs_fx", "htdemucs"));
            Assert.False(AudioEngineBridge.WeightsPresent("demucs_fx", "htdemucs_6s"));
        }
        finally
        {
            AudioProviderRegistry.Clear();
            AudioConfiguration.ModelRoot = previous;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
