using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using Xunit;
using CardFile = Hartsy.Extensions.AudioLab.AudioServices.HartsyInstaller.CardFile;
using CardManifest = Hartsy.Extensions.AudioLab.AudioServices.HartsyInstaller.CardManifest;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>The pure parts of <see cref="HartsyInstaller"/>: reading a card's manifest, refusing what cannot be
/// installed, and deciding where each file goes. Manifests are shaped like the live ones Hartsy serves.</summary>
public class HartsyInstallerTests
{
    private static CardFile Companion(string path, string sha = "ab12") => new(path, 10, sha, $"/download/{Guid.NewGuid()}?type=companion", "completed");

    private static CardManifest Manifest(string mainPath, params CardFile[] companions)
        => new("card", "Card", null, new CardFile(mainPath, 10, "cd34", "/download/card?type=model", "completed"), companions);

    [Fact]
    public void ParseManifest_ReadsMainAndCompanions()
    {
        JObject json = JObject.Parse("""
            {
              "modelId": "3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00",
              "title": "HeartMuLa 3B RL",
              "blockedReason": null,
              "main": { "relativePath": "3b-rl/heartmula-3b-rl_fp32-00001-of-00004.safetensors", "fileName": "heartmula-3b-rl_fp32-00001-of-00004.safetensors",
                        "fileSize": 4933188888, "sha256": "79F122DC2A56", "downloadUrl": "/download/3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00?type=model" },
              "sidecar": { "relativePath": "3b-rl/heartmula-3b-rl_fp32-00001-of-00004.swarm.json", "downloadUrl": "/download/3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00/metadata" },
              "files": [
                { "id": "2d10a45d-9c4d-4f00-9cba-0323ac17ac28", "component": "config", "relativePath": "3b-rl/model.safetensors.index.json",
                  "fileName": "model.safetensors.index.json", "fileSize": 26913, "sha256": "e9c464e130", "uploadStatus": "completed",
                  "downloadUrl": "/download/2d10a45d-9c4d-4f00-9cba-0323ac17ac28?type=companion" }
              ]
            }
            """);
        CardManifest manifest = HartsyInstaller.ParseManifest("3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00", json);
        Assert.Equal("HeartMuLa 3B RL", manifest.Title);
        Assert.Null(manifest.BlockedReason);
        Assert.Equal("3b-rl/heartmula-3b-rl_fp32-00001-of-00004.safetensors", manifest.Main.RelativePath);
        Assert.Equal("79f122dc2a56", manifest.Main.Sha256);
        Assert.Equal("completed", manifest.Main.UploadStatus);
        Assert.True(manifest.Main.IsWeights);
        Assert.Equal("3b-rl", manifest.Main.Folder);
        CardFile index = Assert.Single(manifest.Companions);
        Assert.True(index.IsShardIndex);
        Assert.False(index.IsWeights);
        Assert.Equal("model.safetensors.index.json", index.Name);
        Assert.Null(HartsyInstaller.Problem(manifest));
    }

    [Fact]
    public void Problem_RefusesABlockedCard()
    {
        CardManifest blocked = new("piper", "Piper TTS", "starter subscription required",
            new CardFile("en_US-lessac-medium.onnx", 10, null, null, "completed"), []);
        Assert.Contains("starter subscription required", HartsyInstaller.Problem(blocked));
    }

    [Fact]
    public void Problem_RefusesAMainFileThatIsNotSafetensors()
    {
        CardManifest onnx = new("piper", "Piper TTS", null,
            new CardFile("en_US-lessac-medium.onnx", 10, null, "/download/piper?type=model", "completed"), []);
        Assert.Contains("not a safetensors repack", HartsyInstaller.Problem(onnx));
    }

    [Fact]
    public void Problem_RefusesAnUnfinishedCompanion_AndAnEscapingPath()
    {
        CardFile uploading = new("codec.safetensors", 10, "ab", "/download/x?type=companion", "uploading");
        Assert.Contains("uploading", HartsyInstaller.Problem(Manifest("main.safetensors", uploading)));
        Assert.Contains("unsafe path", HartsyInstaller.Problem(Manifest("main.safetensors", Companion("../outside.json"))));
    }

    [Theory]
    [InlineData("config.json", "config.json")]
    [InlineData("3b-rl\\model.safetensors.index.json", "3b-rl/model.safetensors.index.json")]
    [InlineData("../escape.json", null)]
    [InlineData("a/./b.json", null)]
    [InlineData("a//b.json", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("", null)]
    public void SafeRelativePath_KeepsInsideTheFolder(string path, string expected)
    {
        Assert.Equal(expected, HartsyInstaller.SafeRelativePath(path));
    }

    [Theory]
    [InlineData("/download/abc?type=companion", "https://hartsy.ai/download/abc?type=companion")]
    [InlineData("https://hartsy.ai/download/abc?type=model", "https://hartsy.ai/download/abc?type=model")]
    [InlineData("https://example.com/download/abc", null)]
    [InlineData("//example.com/download/abc", null)]
    [InlineData(null, null)]
    public void AbsoluteUrl_AcceptsOnlyHartsyRoutes(string route, string expected)
    {
        Assert.Equal(expected, HartsyInstaller.AbsoluteUrl(route));
    }

    [Fact]
    public void SidecarTargets_ConfigGoesBesideTheUpstreamFileTheMainStandsInFor()
    {
        CardFile config = Companion("config.json");
        CardManifest whisper = Manifest("whisper-base_fp32.safetensors", config);
        IReadOnlyList<string> targets = HartsyInstaller.SidecarTargets(whisper, config, _ => ["stt/openai--whisper-base/model.safetensors"]);
        Assert.Equal(new[] { "stt/openai--whisper-base/config.json" }, targets);
    }

    [Fact]
    public void SidecarTargets_GoBesideEveryComponentInTheFolder_MainFirst()
    {
        // MeloTTS reads vocab.txt beside the BERT encoder, which sits in the card's top folder with the main file.
        CardFile encoder = Companion("melotts-text-encoder_fp32.safetensors");
        CardFile vocab = Companion("vocab.txt");
        CardManifest melo = Manifest("melotts-english-v3_fp32.safetensors", encoder, vocab);
        IReadOnlyList<string> targets = HartsyInstaller.SidecarTargets(melo, vocab, f => f == encoder
            ? ["tts/bert-base-uncased/pytorch_model.bin"]
            : ["tts/myshell-ai--MeloTTS-English-v3/checkpoint.pth"]);
        Assert.Equal(new[] { "tts/myshell-ai--MeloTTS-English-v3/vocab.txt", "tts/bert-base-uncased/vocab.txt" }, targets);
    }

    [Fact]
    public void SidecarTargets_StayWithinTheirFolder()
    {
        // YuE: the variant's config goes beside the variant's shards, the stage-2 config beside stage 2.
        CardFile stage2 = Companion("s2/yue-stage2_bf16.safetensors");
        CardFile stage2Config = Companion("s2/config.json");
        CardFile variantConfig = Companion("en-cot/config.json");
        CardManifest yue = Manifest("en-cot/yue-en-cot_bf16-00001-of-00003.safetensors", stage2, stage2Config, variantConfig);
        Func<CardFile, IReadOnlyList<string>> declared = f => f == stage2
            ? ["music/YuE/s2/model.safetensors"]
            : ["music/YuE/en-cot/model-00001-of-00003.safetensors"];
        Assert.Equal(new[] { "music/YuE/s2/config.json" }, HartsyInstaller.SidecarTargets(yue, stage2Config, declared));
        Assert.Equal(new[] { "music/YuE/en-cot/config.json" }, HartsyInstaller.SidecarTargets(yue, variantConfig, declared));
        Assert.Empty(HartsyInstaller.SidecarTargets(yue, Companion("other/notes.txt"), declared));
    }

    [Fact]
    public void TranslateShardIndex_RenamesTheCardsShardsToTheUpstreamNames()
    {
        JObject index = JObject.Parse("""
            { "metadata": { "total_size": 1 },
              "weight_map": { "a.weight": "heartmula-3b-rl_fp32-00001-of-00002.safetensors", "b.weight": "heartmula-3b-rl_fp32-00002-of-00002.safetensors" } }
            """);
        Dictionary<string, string> names = new()
        {
            ["heartmula-3b-rl_fp32-00001-of-00002.safetensors"] = "model-00001-of-00002.safetensors",
            ["heartmula-3b-rl_fp32-00002-of-00002.safetensors"] = "model-00002-of-00002.safetensors",
        };
        JObject translated = HartsyInstaller.TranslateShardIndex(index, names);
        Assert.Equal("model-00001-of-00002.safetensors", translated["weight_map"]["a.weight"].ToString());
        Assert.Equal("model-00002-of-00002.safetensors", translated["weight_map"]["b.weight"].ToString());
        Assert.Equal(1, translated["metadata"]["total_size"].Value<int>());
        // The card's own copy is left as it was.
        Assert.Equal("heartmula-3b-rl_fp32-00001-of-00002.safetensors", index["weight_map"]["a.weight"].ToString());
    }

    [Fact]
    public void TranslateShardIndex_KeepsAnIndexThatAlreadyNamesTheUpstreamShards()
    {
        JObject index = JObject.Parse("""{ "weight_map": { "a.weight": "model-00001-of-00003.safetensors" } }""");
        Dictionary<string, string> names = new() { ["yue-en-cot_bf16-00001-of-00003.safetensors"] = "model-00001-of-00003.safetensors" };
        Assert.Equal("model-00001-of-00003.safetensors", HartsyInstaller.TranslateShardIndex(index, names)["weight_map"]["a.weight"].ToString());
    }

    [Fact]
    public void TranslateShardIndex_RefusesAnIndexNamingAShardTheCardLacks()
    {
        JObject index = JObject.Parse("""{ "weight_map": { "a.weight": "something-else.safetensors" } }""");
        Dictionary<string, string> names = new() { ["card-00001-of-00001.safetensors"] = "model.safetensors" };
        Assert.Null(HartsyInstaller.TranslateShardIndex(index, names));
        Assert.Null(HartsyInstaller.TranslateShardIndex(JObject.Parse("{}"), names));
    }

    [Theory]
    [InlineData("1.7B-Base", "1.7B-Base")]
    [InlineData("fish-speech-1.5", "fish-speech-1.5")]
    [InlineData("a/b", "a_b")]
    [InlineData("..", "_")]
    [InlineData(null, "_")]
    public void RecordName_KeepsModelIdsReadable(string id, string expected)
    {
        Assert.Equal(expected, HartsyInstaller.RecordName(id));
    }
}
