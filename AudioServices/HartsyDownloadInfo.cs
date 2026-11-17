using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Shapes what installing a model downloads, and from where, for the install dialog. Everything about size and hash comes
/// from the Hartsy card's live manifest. When Hartsy cannot be read the answer says so and carries no size, never the typed
/// <see cref="AudioModelDefinition.EstimatedSize"/>.</summary>
public static class HartsyDownloadInfo
{
    /// <summary>One model's download description.</summary>
    /// <param name="model">The catalog row.</param>
    /// <param name="providerId">The provider the row belongs to.</param>
    /// <param name="info">The model's card as Hartsy describes it now, or null when the model has no card.</param>
    /// <param name="sharedWith">The other rows of the provider that install the same card.</param>
    /// <param name="pendingUpload">Whether the model is waiting for a card to be published.</param>
    public static JObject Build(AudioModelDefinition model, string providerId, HartsyCardInfo info, IReadOnlyList<string> sharedWith, bool pendingUpload)
    {
        string card = HartsyRepacks.CardFor(providerId, model.Id);
        JObject result = new()
        {
            ["source"] = card is null ? "upstream" : "hartsy",
            ["card_id"] = card,
            ["hartsy_url"] = card is null ? null : HartsyRepacks.PageUrl(card),
            ["pending_upload"] = pendingUpload,
            ["shares_card_with"] = new JArray(sharedWith ?? []),
            ["upstream"] = Upstream(model.SourceUrl),
            ["upstream_extras"] = new JArray(HartsyRepacks.UpstreamExtrasFor(providerId, model.Id).Select(f => new JObject
            {
                ["host"] = f.Host,
                ["repo"] = f.Repo,
                ["path"] = f.Path,
                ["url"] = f.Url,
            })),
        };
        if (card is null || info is null)
        {
            return result;
        }
        result["title"] = info.Title;
        result["read_error"] = info.ReadError;
        result["install_problem"] = info.InstallProblem;
        if (info.ReadError is null)
        {
            result["total_bytes"] = info.TotalBytes;
            result["files"] = new JArray(info.Files.Select(f => new JObject
            {
                ["path"] = f.Path,
                ["bytes"] = f.Bytes,
                ["sha256"] = f.Sha256,
            }));
        }
        return result;
    }

    /// <summary>The other rows of the provider that install the same card, so a quantized or streaming row can say it adds no download.</summary>
    public static string[] SharedWith(AudioProviderDefinition provider, AudioModelDefinition model)
    {
        string card = HartsyRepacks.CardFor(provider.Id, model.Id);
        return card is null ? [] : [.. provider.Models
            .Where(other => other.Id != model.Id && HartsyRepacks.CardFor(provider.Id, other.Id) == card).Select(other => other.Id)];
    }

    /// <summary>The host and address of a model's upstream source, or null when it has none.</summary>
    private static JObject Upstream(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }
        return new JObject { ["host"] = uri.Host, ["url"] = sourceUrl };
    }
}
