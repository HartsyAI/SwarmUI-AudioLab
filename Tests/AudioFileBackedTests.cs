using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Which providers list only what is installed, and how an installed artifact reaches the rows it backs.
/// The selector for a file-backed provider is built from <see cref="AudioArtifactIndex"/>, so these tests drive
/// admission from real files and sidecars rather than from a running host.</summary>
public class AudioFileBackedTests
{
    /// <summary>The one carded provider that stays unflipped: its card has no identity and cannot be placed, so the
    /// engine fetches its voices itself and an identity-driven listing would hide every one of them.</summary>
    private const string UnplaceableProvider = "piper_tts";

    [Fact]
    public void FileBacked_IsSetOnEveryCardedProvider_AndOnNoOther()
    {
        HashSet<string> carded = [.. HartsyRepacks.All().Select(r => r.ProviderId)];
        carded.Remove(UnplaceableProvider);
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            HashSet<string> fileBacked = [.. AudioProviderRegistry.All
                .Where(p => !p.IsApiProvider && p.FileBacked)
                .Select(p => p.Id)];
            Assert.True(carded.SetEquals(fileBacked),
                $"Carded but not file-backed: {string.Join(", ", carded.Except(fileBacked))}. " +
                $"File-backed without a card: {string.Join(", ", fileBacked.Except(carded))}.");
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void RowsThatShareACard_NameTheRowTheyRideOn()
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            Dictionary<string, List<(string Key, AudioModelDefinition Row)>> byCard = [];
            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All.Where(p => !p.IsApiProvider && !p.NotImplemented))
            {
                foreach (AudioModelDefinition row in provider.Models)
                {
                    string card = HartsyRepacks.CardFor(provider.Id, row.Id);
                    if (card is null)
                    {
                        continue;
                    }
                    if (!byCard.TryGetValue(card, out List<(string Key, AudioModelDefinition Row)> rows))
                    {
                        byCard[card] = rows = [];
                    }
                    rows.Add(($"{provider.Id}/{row.Id}", row));
                }
            }
            List<string> problems = [];
            foreach ((string card, List<(string Key, AudioModelDefinition Row)> rows) in byCard.Where(c => c.Value.Count > 1))
            {
                List<(string Key, AudioModelDefinition Row)> primary = rows.Where(r => r.Row.BackedByArtifact is null).ToList();
                if (primary.Count != 1)
                {
                    problems.Add($"{card} has {primary.Count} primary rows");
                    continue;
                }
                foreach ((string key, AudioModelDefinition row) in rows.Where(r => r.Row.BackedByArtifact is not null))
                {
                    if (row.BackedByArtifact != primary[0].Key)
                    {
                        problems.Add($"{key} names {row.BackedByArtifact}, not {primary[0].Key}");
                    }
                }
            }
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void Admit_AdmitsAPrimaryFile_WhoseSidecarNamesAKnownRow_AndNothingElse()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            T2IModel kokoro = ScannedFile(dir, "kokoro", "kokoro_tts", "default", "main");
            T2IModel shard = ScannedFile(dir, "kokoro-shard", "kokoro_tts", "default", "shard");
            T2IModel stranger = ScannedFile(dir, "stranger", "no_such_provider", "x", "main");
            AudioArtifactIndex.Admit([kokoro, shard, stranger]);
            Assert.Equal(["Audio Models/Kokoro/default"], AudioArtifactIndex.Admitted.Keys.ToArray());
        }
        finally
        {
            AudioArtifactIndex.Admit([]);
            AudioProviderRegistry.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Admit_OneInstalledCard_AdmitsEveryRowItBacks()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            AudioProviderDefinition heartlib = AudioProviderRegistry.GetById("heartlib_music");
            AudioProviderDefinition whisperStreaming = AudioProviderRegistry.GetById("whisperstreaming_stt");
            AudioProviderDefinition whisper = AudioProviderRegistry.GetById("whisper_stt");
            AudioArtifactIndex.Admit([
                ScannedFile(dir, "heartmula", "heartlib_music", "3b-base", "main"),
                ScannedFile(dir, "whisper", "whisper_stt", "base", "main"),
            ]);
            HashSet<string> admitted = [.. AudioArtifactIndex.Admitted.Keys];
            string[] heartRows = [.. heartlib.Models.Where(m => m.Id.StartsWith("3b-base")).Select(m => heartlib.GetFullModelName(m.Id))];
            Assert.Equal(3, heartRows.Length);
            Assert.All(heartRows, name => Assert.Contains(name, admitted));
            Assert.Contains(whisper.GetFullModelName("base"), admitted);
            Assert.Contains(whisperStreaming.GetFullModelName("base"), admitted);
        }
        finally
        {
            AudioArtifactIndex.Admit([]);
            AudioProviderRegistry.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProjectScannedModels_ListsOnlyWhatIsAdmitted()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            AudioProviderDefinition kokoro = AudioProviderRegistry.GetById("kokoro_tts");
            AudioArtifactIndex.Admit([]);
            Assert.Empty(AudioModelFactory.ProjectScannedModels(kokoro));

            AudioArtifactIndex.Admit([ScannedFile(dir, "kokoro", "kokoro_tts", "default", "main")]);
            Assert.Equal(["Audio Models/Kokoro/default"], AudioModelFactory.ProjectScannedModels(kokoro).Keys.ToArray());
        }
        finally
        {
            AudioArtifactIndex.Admit([]);
            AudioProviderRegistry.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InstalledOnDisk_IsFalse_ForAFileBackedRowWithNoArtifact_WhileTheRowStaysAvailable()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            AudioProviderDefinition kokoro = AudioProviderRegistry.GetById("kokoro_tts");
            Assert.Contains(kokoro.Models, m => m.Id == "default");

            AudioArtifactIndex.Admit([]);
            Assert.False(AudioLabAPI.InstalledOnDisk(kokoro, "default", available: true));

            AudioArtifactIndex.Admit([ScannedFile(dir, "kokoro", "kokoro_tts", "default", "main")]);
            Assert.True(AudioLabAPI.InstalledOnDisk(kokoro, "default", available: true));
        }
        finally
        {
            AudioArtifactIndex.Admit([]);
            AudioProviderRegistry.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AdoptPlacedInstalls_StampsAPreviouslyPlacedFileOnce_AndNeverOverwritesAnIdentity()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string previousRoot = AudioConfiguration.ModelRoot;
        AudioProviderRegistry.Clear();
        try
        {
            AudioConfiguration.ModelRoot = root;
            AudioProviderDefinitions.RegisterAll();
            (AudioProviderDefinition provider, AudioModelDefinition model, AudioWeightsRegistry.DownloadSpec spec) =
                AudioProviderRegistry.All.Where(p => p.FileBacked)
                    .SelectMany(p => p.Models.Select(m => (p, m, AudioWeightsRegistry.SpecsFor(p.Id, m.Id))))
                    .Where(t => t.Item3.Length > 0)
                    .Select(t => (t.p, t.m, t.Item3[0]))
                    .First();
            string folder = Path.Combine(root, AudioWeights.CategorySubfolder(provider.Category), provider.ModelPrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, spec.FileName)));
            string placed = Path.Combine(folder, spec.FileName);
            File.WriteAllBytes(placed, [1]);
            AudioArtifactIndex.Admit([]);

            Assert.Equal(1, AudioArtifactIdentity.AdoptPlacedInstalls([provider]));
            JObject identity = JObject.Parse(File.ReadAllText(Path.ChangeExtension(placed, ".swarm.json")));
            Assert.Equal(provider.Id, identity["hartsy.provider_id"]?.ToString());
            Assert.Equal(model.Id, identity["hartsy.model_id"]?.ToString());

            File.WriteAllText(Path.ChangeExtension(placed, ".swarm.json"), "{\"hartsy.model_id\":\"kept\"}");
            Assert.Equal(0, AudioArtifactIdentity.AdoptPlacedInstalls([provider]));
            Assert.Contains("kept", File.ReadAllText(Path.ChangeExtension(placed, ".swarm.json")));
        }
        finally
        {
            AudioConfiguration.ModelRoot = previousRoot;
            AudioArtifactIndex.Admit([]);
            AudioProviderRegistry.Clear();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A scanned primary file with its identity sidecar, as an install leaves it.</summary>
    private static T2IModel ScannedFile(string dir, string name, string providerId, string modelId, string component)
    {
        string path = Path.Combine(dir, $"{name}.safetensors");
        File.WriteAllBytes(path, []);
        JObject identity = new()
        {
            ["hartsy.provider_id"] = providerId,
            ["hartsy.model_id"] = modelId,
            ["hartsy.component"] = component,
        };
        File.WriteAllText(Path.ChangeExtension(path, ".swarm.json"), identity.ToString());
        return new T2IModel(null, dir, path, name);
    }
}
