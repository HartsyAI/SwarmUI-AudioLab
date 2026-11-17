using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using Xunit;
using CardFile = Hartsy.Extensions.AudioLab.AudioServices.HartsyInstaller.CardFile;
using CardManifest = Hartsy.Extensions.AudioLab.AudioServices.HartsyInstaller.CardManifest;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>What the install dialog says a card downloads: read from the card's manifest, with exact sizes and hashes, and honest when
/// Hartsy cannot be reached or a model has no card.</summary>
public class HartsyCardInfoTests
{
    private const string Sha = "2da3a9728686ab12cd34ef56ab78cd90ef12ab34cd56ef78ab90cd12ef34ab56";

    private static CardFile File(string path, long? size, string sha = Sha, string status = "completed")
        => new(path, size, sha, $"/download/{Guid.NewGuid()}?type=companion", status);

    private static CardManifest Manifest(string id, CardFile main, params CardFile[] companions) => new(id, "Test Card", null, main, companions);

    private static string NewCard() => Guid.NewGuid().ToString();

    [Fact]
    public void FromManifest_ListsTheMainFileFirst_WithExactSizesAndHashes_AndTheirSum()
    {
        CardManifest manifest = Manifest("card", File("model_bf16.safetensors", 5_920_294_037), File("config.json", 1_200, "ab12"), File("vocoder/v.safetensors", 80_000_000, "cd34"));
        HartsyCardInfo info = HartsyCardInfo.FromManifest(manifest);
        Assert.Equal(new[] { "model_bf16.safetensors", "config.json", "vocoder/v.safetensors" }, info.Files.Select(f => f.Path));
        Assert.Equal(5_920_294_037, info.Files[0].Bytes);
        Assert.Equal(Sha, info.Files[0].Sha256);
        Assert.Equal(5_920_294_037 + 1_200 + 80_000_000, info.TotalBytes);
        Assert.Equal("https://hartsy.ai/Home?type=models&id=card", info.PageUrl);
        Assert.Null(info.InstallProblem);
        Assert.Null(info.ReadError);
    }

    [Fact]
    public void FromManifest_AFileWithoutASize_GivesNoTotal_RatherThanAPartialSum()
    {
        HartsyCardInfo info = HartsyCardInfo.FromManifest(Manifest("card", File("model.safetensors", 4_000_000_000), File("extra.bin", null)));
        Assert.Null(info.TotalBytes);
        Assert.Equal(4_000_000_000, info.Files[0].Bytes);
    }

    [Fact]
    public void FromManifest_ACardTheInstallerWouldRefuse_CarriesTheReason()
    {
        CardManifest blocked = new("card", "Piper", "starter plan required", File("voice.onnx", 60_000_000, null), []);
        HartsyCardInfo info = HartsyCardInfo.FromManifest(blocked);
        Assert.Contains("starter plan required", info.InstallProblem);
        Assert.Null(info.ReadError);
        HartsyCardInfo unfinished = HartsyCardInfo.FromManifest(Manifest("card", File("m.safetensors", 10), File("c.json", 5, status: "uploading")));
        Assert.Contains("uploading", unfinished.InstallProblem);
    }

    [Theory]
    [InlineData(5_920_294_037L, "5.92 GB")]
    [InlineData(740_000_000L, "740.0 MB")]
    [InlineData(1_820_000L, "1.8 MB")]
    [InlineData(72_500_000L, "72.5 MB")]
    [InlineData(12_345L, "12 KB")]
    [InlineData(2_048L, "2.0 KB")]
    [InlineData(142L, "142 B")]
    [InlineData(null, "size not published")]
    public void FormatBytes_UsesDecimalUnits(long? bytes, string expected)
    {
        Assert.Equal(expected, HartsyCardInfo.FormatBytes(bytes));
    }

    [Fact]
    public async Task GetAsync_ReusesAnAnswerForTenMinutes_ThenAsksAgain()
    {
        string card = NewCard();
        int reads = 0;
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        Task<(CardManifest, string)> Read(string id, CancellationToken cancel)
        {
            reads++;
            return Task.FromResult((Manifest(id, File("m.safetensors", 100)), (string)null));
        }
        await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        clock += TimeSpan.FromMinutes(9);
        await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        Assert.Equal(1, reads);
        clock += TimeSpan.FromMinutes(2);
        await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task GetAsync_AnUnreachableHartsy_IsReportedWithNoSize_AndRetriedSoon()
    {
        string card = NewCard();
        int reads = 0;
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        Task<(CardManifest, string)> Read(string id, CancellationToken cancel)
        {
            reads++;
            return Task.FromResult(((CardManifest)null, $"Hartsy answered 503 for card {id}."));
        }
        HartsyCardInfo info = await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        Assert.Contains("503", info.ReadError);
        Assert.Empty(info.Files);
        Assert.Null(info.TotalBytes);
        clock += TimeSpan.FromSeconds(20);
        await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        Assert.Equal(1, reads);
        clock += TimeSpan.FromSeconds(20);
        await HartsyCardInfo.GetAsync(card, Read, () => clock, default);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task GetAsync_AReaderThatThrows_IsReported_NotRaised()
    {
        HartsyCardInfo info = await HartsyCardInfo.GetAsync(NewCard(), (id, cancel) => throw new HttpRequestException("name resolution failed"), () => DateTimeOffset.UtcNow, default);
        Assert.Contains("name resolution failed", info.ReadError);
    }

    /// <summary>Runs a test against the real provider catalog.</summary>
    private static void WithCatalog(Action<Func<string, AudioProviderDefinition>> test)
    {
        AudioProviderRegistry.Clear();
        AudioProviderDefinitions.RegisterAll();
        try
        {
            test(AudioProviderRegistry.GetById);
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    private static AudioModelDefinition Row(AudioProviderDefinition provider, string modelId) => provider.Models.First(m => m.Id == modelId);

    [Fact]
    public void Build_ACardOnHartsy_ListsItsFilesWithExactSizesHashesAndAPageLink()
    {
        WithCatalog(provider =>
        {
            AudioProviderDefinition kyutai = provider("kyutaistt_stt");
            string card = HartsyRepacks.CardFor("kyutaistt_stt", "2.6b-en");
            HartsyCardInfo info = HartsyCardInfo.FromManifest(Manifest(card, File("kyutaistt-2_6b-en_bf16.safetensors", 5_920_294_037)));
            JObject json = HartsyDownloadInfo.Build(Row(kyutai, "2.6b-en"), "kyutaistt_stt", info, [], false);
            Assert.Equal("hartsy", (string)json["source"]);
            Assert.Equal($"https://hartsy.ai/Home?type=models&id={card}", (string)json["hartsy_url"]);
            Assert.Equal(5_920_294_037, (long)json["total_bytes"]);
            Assert.Equal(Sha, (string)json["files"][0]["sha256"]);
            Assert.Null((string)json["read_error"]);
            // The tokenizer the card does not carry is named, so the model is not described as wholly from Hartsy.
            Assert.Equal("tokenizer_en_audio_4000.model", (string)json["upstream_extras"][0]["path"]);
        });
    }

    [Fact]
    public void Build_WhenHartsyCannotBeRead_SaysSo_AndCarriesNoSize()
    {
        WithCatalog(provider =>
        {
            AudioProviderDefinition vibe = provider("vibevoice_tts");
            JObject json = HartsyDownloadInfo.Build(Row(vibe, "1.5b"), "vibevoice_tts", HartsyCardInfo.Unreadable(HartsyRepacks.CardFor("vibevoice_tts", "1.5b"), "Hartsy answered 503."), [], false);
            Assert.Equal("Hartsy answered 503.", (string)json["read_error"]);
            Assert.Null(json["total_bytes"]);
            Assert.Null(json["files"]);
            Assert.NotNull(json["hartsy_url"]);
            Assert.Null(json.Property("estimated_size"));
        });
    }

    [Fact]
    public void Build_AModelWithNoCard_PointsAtItsUpstreamSource_AndPublishesNoSize()
    {
        WithCatalog(provider =>
        {
            AudioProviderDefinition whisper = provider("whisper_stt");
            JObject json = HartsyDownloadInfo.Build(Row(whisper, "large-v3"), "whisper_stt", null, [], HartsyRepacks.PendingUpload.Contains("whisper_stt/large-v3"));
            Assert.Equal("upstream", (string)json["source"]);
            Assert.True((bool)json["pending_upload"]);
            Assert.Equal("huggingface.co", (string)json["upstream"]["host"]);
            Assert.Null((string)json["hartsy_url"]);
            Assert.Null(json["files"]);
            Assert.Null(json["total_bytes"]);
        });
    }

    [Fact]
    public void SharedWith_ANamedQuantRow_SharesItsBaseCard_ButABaseRowSharesOnlyItsQuants()
    {
        WithCatalog(provider =>
        {
            AudioProviderDefinition heart = provider("heartlib_music");
            Assert.Equal(new[] { "3b-base", "3b-base-q8" }, HartsyDownloadInfo.SharedWith(heart, Row(heart, "3b-base-q4")).Order());
            Assert.Equal(new[] { "3b-base-q4", "3b-base-q8" }, HartsyDownloadInfo.SharedWith(heart, Row(heart, "3b-base")).Order());
            AudioProviderDefinition piper = provider("piper_tts");
            Assert.Empty(HartsyDownloadInfo.SharedWith(piper, Row(piper, "default")));
        });
    }

    [Fact]
    public void UpstreamExtras_NameModelsThatExist_AndThatHaveACard()
    {
        WithCatalog(provider =>
        {
            foreach (string key in HartsyRepacks.UpstreamExtras.Keys)
            {
                string[] parts = key.Split('/');
                AudioProviderDefinition p = provider(parts[0]);
                Assert.True(p is not null && p.Models.Any(m => m.Id == parts[1]), $"'{key}' is not a catalog model.");
                Assert.True(HartsyRepacks.CardFor(parts[0], parts[1]) is not null, $"'{key}' has no card, so it has no leftovers to list.");
            }
        });
    }
}
