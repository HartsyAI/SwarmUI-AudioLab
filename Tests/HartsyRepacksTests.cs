using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>The Hartsy card table (<see cref="HartsyRepacks"/>) against the real provider catalog: every local model
/// has a card or is listed as waiting for one, and no row names a model that does not exist.</summary>
public class HartsyRepacksTests
{
    private static List<(AudioProviderDefinition Provider, AudioModelDefinition Model)> LocalModels()
    {
        AudioProviderRegistry.Clear();
        try
        {
            AudioProviderDefinitions.RegisterAll();
            return [.. AudioProviderRegistry.All
                .Where(p => !p.IsApiProvider && !p.NotImplemented)
                .SelectMany(p => p.Models.Select(m => (p, m)))];
        }
        finally
        {
            AudioProviderRegistry.Clear();
        }
    }

    [Fact]
    public void EveryLocalModel_HasACard_OrIsPendingUpload()
    {
        List<string> uncovered = [];
        List<string> both = [];
        foreach ((AudioProviderDefinition provider, AudioModelDefinition model) in LocalModels())
        {
            string key = $"{provider.Id}/{model.Id}";
            bool hasCard = HartsyRepacks.CardFor(provider.Id, model.Id) is not null;
            bool pending = HartsyRepacks.PendingUpload.Contains(key);
            if (!hasCard && !pending)
            {
                uncovered.Add(key);
            }
            if (hasCard && pending)
            {
                both.Add(key);
            }
        }
        Assert.True(uncovered.Count == 0, $"No Hartsy card and not pending: {string.Join(", ", uncovered)}");
        Assert.True(both.Count == 0, $"Has a card but is still listed as pending: {string.Join(", ", both)}");
    }

    [Fact]
    public void EveryCardRowAndPendingRow_NamesARealModel()
    {
        HashSet<string> real = [.. LocalModels().Select(x => $"{x.Provider.Id}/{x.Model.Id}")];
        string[] stale = [.. HartsyRepacks.All().Select(r => $"{r.ProviderId}/{r.ModelId}").Concat(HartsyRepacks.PendingUpload).Where(k => !real.Contains(k))];
        Assert.True(stale.Length == 0, $"Rows for models the catalog does not have: {string.Join(", ", stale)}");
    }

    [Fact]
    public void CardIds_AreGuids()
    {
        foreach ((string providerId, string modelId, string card) in HartsyRepacks.All())
        {
            Assert.True(Guid.TryParse(card, out _), $"{providerId}/{modelId} has card id '{card}'.");
        }
    }

    [Theory]
    [InlineData("heartlib_music", "3b-rl", "3b-rl-q8")]
    [InlineData("heartlib_music", "3b-rl", "3b-rl-q4")]
    [InlineData("minimax_music3", "base", "q8")]
    [InlineData("resemble_enhance_fx", "enhance", "denoise")]
    public void RowsThatShareOneDownload_ShareOneCard(string providerId, string baseModel, string sharingModel)
    {
        Assert.Equal(HartsyRepacks.CardFor(providerId, baseModel), HartsyRepacks.CardFor(providerId, sharingModel));
    }

    [Fact]
    public void WhisperStreaming_InstallsTheWhisperBaseCard_IntoWhispersFolder()
    {
        Assert.Equal(HartsyRepacks.CardFor("whisper_stt", "base"), HartsyRepacks.CardFor("whisperstreaming_stt", "base"));
        Assert.Equal("whisper_stt", HartsyRepacks.FolderOwner("whisperstreaming_stt"));
        Assert.Equal("kokoro_tts", HartsyRepacks.FolderOwner("kokoro_tts"));
    }

    [Fact]
    public void WhisperLargeV3AndTurbo_AreWithheld_UntilTheirCardsCarryTheirOwnSidecars()
    {
        Assert.Null(HartsyRepacks.CardFor("whisper_stt", "large-v3"));
        Assert.Null(HartsyRepacks.CardFor("whisper_stt", "turbo"));
        Assert.Contains("whisper_stt/large-v3", HartsyRepacks.PendingUpload);
        Assert.Contains("whisper_stt/turbo", HartsyRepacks.PendingUpload);
    }

    [Fact]
    public void Urls_UseTheCardId()
    {
        string card = HartsyRepacks.CardFor("whisper_stt", "base");
        Assert.Equal($"https://hartsy.ai/Home?type=models&id={card}", HartsyRepacks.PageUrl(card));
        Assert.Equal($"https://hartsy.ai/download/{card}?type=model", HartsyRepacks.DownloadUrl(card));
        Assert.Equal($"https://hartsy.ai/download/{card}/manifest", HartsyRepacks.ManifestUrl(card));
        Assert.Null(HartsyRepacks.PageUrlFor("rvc_clone", "v2"));
    }
}
