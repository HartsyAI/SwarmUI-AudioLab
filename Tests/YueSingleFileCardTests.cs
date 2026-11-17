using System.IO;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>A YuE variant installed from its Hartsy card is one merged <c>model.safetensors</c>, not three shards and an
/// index. It must read as installed, and finishing the install must not fetch the upstream shards beside it, while a
/// sharded install (from upstream, or an older card) keeps working.</summary>
public class YueSingleFileCardTests
{
    private const string Variant = "en-cot";

    private static readonly string[] _sidecars = ["config.json", "generation_config.json", "tokenizer.model"];

    /// <summary>Runs a test against a throwaway audio models root with the real provider catalog registered.</summary>
    private static void WithRoot(Action<AudioProviderDefinition> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "audiolab-yue-" + Guid.NewGuid().ToString("N"));
        string previous = AudioConfiguration.ModelRoot;
        AudioConfiguration.ModelRoot = root;
        AudioProviderRegistry.Clear();
        AudioProviderDefinitions.RegisterAll();
        try
        {
            test(AudioProviderRegistry.GetById("yue_music"));
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

    private static void Place(AudioProviderDefinition provider, string variant, string fileName)
    {
        string path = Path.Combine(AudioWeights.WeightsDirectory(provider), variant, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, new byte[2_000_000]);
    }

    private static void PlaceSidecarsAndCodec(AudioProviderDefinition provider, string variant)
    {
        foreach (string sidecar in _sidecars)
        {
            Place(provider, variant, sidecar);
        }
        File.WriteAllBytes(Path.Combine(AudioWeights.WeightsDirectory(provider), "xcodec.safetensors"), new byte[2_000_000]);
    }

    private static bool IsShardOrIndex(AudioWeightsRegistry.DownloadSpec spec)
        => spec.FileName.Contains("-of-0000") || spec.FileName.EndsWith(".index.json", StringComparison.Ordinal);

    [Fact]
    public void MergedCheckpoint_SatisfiesEveryShardAndTheIndex_ButNotTheSidecars()
    {
        WithRoot(provider =>
        {
            Place(provider, Variant, "model.safetensors");
            foreach (AudioWeightsRegistry.DownloadSpec spec in AudioWeightsRegistry.SpecsFor("yue_music", Variant))
            {
                string found = AudioWeights.FindPlacedFile(provider, spec);
                if (IsShardOrIndex(spec))
                {
                    Assert.NotNull(found);
                    Assert.EndsWith("model.safetensors", found);
                }
                else
                {
                    Assert.Null(found);
                }
            }
        });
    }

    [Fact]
    public void MergedCheckpoint_WithItsSidecarsAndCodec_IsPresent()
    {
        WithRoot(provider =>
        {
            Place(provider, Variant, "model.safetensors");
            Assert.False(AudioEngineBridge.WeightsPresent("yue_music", Variant));
            PlaceSidecarsAndCodec(provider, Variant);
            Assert.True(AudioEngineBridge.WeightsPresent("yue_music", Variant));
        });
    }

    [Fact]
    public void ShardedCheckpoint_StillCounts()
    {
        WithRoot(provider =>
        {
            foreach (AudioWeightsRegistry.DownloadSpec spec in AudioWeightsRegistry.SpecsFor("yue_music", Variant).Where(IsShardOrIndex))
            {
                Place(provider, Variant, spec.FileName[(Variant.Length + 1)..]);
            }
            PlaceSidecarsAndCodec(provider, Variant);
            Assert.True(AudioEngineBridge.WeightsPresent("yue_music", Variant));
        });
    }

    [Fact]
    public void OneVariantsMergedCheckpoint_DoesNotAnswerForAnotherVariant()
    {
        WithRoot(provider =>
        {
            Place(provider, Variant, "model.safetensors");
            PlaceSidecarsAndCodec(provider, Variant);
            Assert.True(AudioEngineBridge.WeightsPresent("yue_music", Variant));
            Assert.False(AudioEngineBridge.WeightsPresent("yue_music", "zh-cot"));
        });
    }

    [Fact]
    public async Task FinishingAnInstall_FetchesNoShardsBesideAMergedCheckpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "audiolab-yue-" + Guid.NewGuid().ToString("N"));
        string previous = AudioConfiguration.ModelRoot;
        AudioConfiguration.ModelRoot = root;
        AudioProviderRegistry.Clear();
        AudioProviderDefinitions.RegisterAll();
        try
        {
            AudioProviderDefinition provider = AudioProviderRegistry.GetById("yue_music");
            Place(provider, Variant, "model.safetensors");
            PlaceSidecarsAndCodec(provider, Variant);
            // Nothing is missing, so this must return at once. The limit only stops a regression from downloading
            // gigabytes of upstream shards from inside a test.
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(2));
            await AudioWeights.EnsureMissingModelWeightsAsync(provider, Variant, null, limit.Token);
            string folder = Path.Combine(AudioWeights.WeightsDirectory(provider), Variant);
            Assert.Empty(Directory.EnumerateFiles(folder, "model-0000*-of-00003.safetensors"));
            Assert.False(File.Exists(Path.Combine(folder, "model.safetensors.index.json")));
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
