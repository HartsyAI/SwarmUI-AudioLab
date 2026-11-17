using System.Collections.Concurrent;
using CardManifest = Hartsy.Extensions.AudioLab.AudioServices.HartsyInstaller.CardManifest;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>One file a Hartsy card will download.</summary>
/// <param name="Path">The path inside the model folder.</param>
/// <param name="Bytes">The exact size Hartsy stores, or null when the manifest does not carry one.</param>
/// <param name="Sha256">The SHA-256 Hartsy stores, lowercase hex, or null. Every download is checked against it.</param>
public sealed record HartsyCardFile(string Path, long? Bytes, string Sha256);

/// <summary>What a Hartsy card will download, read from the card's live download manifest: every file with the exact size and
/// SHA-256 Hartsy stores, so the text shown before an install names the real files and never a typed estimate.</summary>
/// <param name="CardId">The card's id on Hartsy.</param>
/// <param name="Title">The card's title on Hartsy.</param>
/// <param name="PageUrl">The card's page on Hartsy, for people.</param>
/// <param name="Files">The main file first, then every companion. Empty when <paramref name="ReadError"/> is set.</param>
/// <param name="TotalBytes">The sum of the files' sizes, or null when any size is missing (a partial sum would understate it).</param>
/// <param name="InstallProblem">Why the installer would refuse this card (blocked for this tier, not a safetensors repack, a file not
/// finished uploading), or null when it can install.</param>
/// <param name="ReadError">Why the manifest could not be read (Hartsy unreachable, no such card), or null.</param>
public sealed record HartsyCardInfo(string CardId, string Title, string PageUrl, IReadOnlyList<HartsyCardFile> Files, long? TotalBytes,
    string InstallProblem, string ReadError)
{
    /// <summary>How long a successful read is reused. The route is called each time an install dialog opens.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a failed read is reused, so an unreachable Hartsy is not asked again by every dialog but is retried soon.</summary>
    internal static readonly TimeSpan CacheFailureFor = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, HartsyCardInfo Info)> _cache = new(StringComparer.Ordinal);

    /// <summary>Reads a card's manifest from Hartsy, or returns the recent answer.</summary>
    public static Task<HartsyCardInfo> GetAsync(string cardId, CancellationToken cancel)
        => GetAsync(cardId, HartsyInstaller.ReadManifestAsync, () => DateTimeOffset.UtcNow, cancel);

    /// <summary>The testable form: the manifest reader and the clock are passed in.</summary>
    internal static async Task<HartsyCardInfo> GetAsync(string cardId, Func<string, CancellationToken, Task<(CardManifest Manifest, string Refusal)>> read,
        Func<DateTimeOffset> now, CancellationToken cancel)
    {
        if (_cache.TryGetValue(cardId, out (DateTimeOffset At, HartsyCardInfo Info) cached)
            && now() - cached.At < (cached.Info.ReadError is null ? CacheFor : CacheFailureFor))
        {
            return cached.Info;
        }
        HartsyCardInfo info;
        try
        {
            (CardManifest manifest, string refusal) = await read(cardId, cancel).ConfigureAwait(false);
            info = manifest is null ? Unreadable(cardId, refusal ?? "Hartsy returned no manifest.") : FromManifest(manifest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            info = Unreadable(cardId, ex.Message);
        }
        _cache[cardId] = (now(), info);
        return info;
    }

    /// <summary>Forgets every cached answer.</summary>
    internal static void ClearCache() => _cache.Clear();

    /// <summary>The card as its manifest describes it.</summary>
    internal static HartsyCardInfo FromManifest(CardManifest manifest)
    {
        List<HartsyCardFile> files = [];
        // The manifest names the main file apart from the companions; it is listed first.
        foreach (HartsyInstaller.CardFile file in manifest.Companions.Prepend(manifest.Main).Where(f => f is not null))
        {
            files.Add(new HartsyCardFile(file.RelativePath, file.Size, file.Sha256));
        }
        long? total = files.Count > 0 && files.All(f => f.Bytes is > 0) ? files.Sum(f => f.Bytes.Value) : null;
        return new HartsyCardInfo(manifest.CardId, manifest.Title, HartsyRepacks.PageUrl(manifest.CardId), files, total,
            HartsyInstaller.Problem(manifest), null);
    }

    /// <summary>A card whose manifest could not be read.</summary>
    internal static HartsyCardInfo Unreadable(string cardId, string reason)
        => new(cardId, cardId, HartsyRepacks.PageUrl(cardId), [], null, null, reason);

    /// <summary>A size for people: decimal units, two decimals from a gigabyte up, plain bytes below a kilobyte, and "size not published"
    /// when there is none. The install dialog's <c>audioLabFormatBytes</c> follows the same rules.</summary>
    public static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "size not published";
        }
        double value = bytes.Value;
        if (value >= 1e9)
        {
            return $"{value / 1e9:0.00} GB";
        }
        if (value >= 1e6)
        {
            return $"{value / 1e6:0.0} MB";
        }
        if (value >= 1e4)
        {
            return $"{value / 1e3:0} KB";
        }
        if (value >= 1e3)
        {
            return $"{value / 1e3:0.0} KB";
        }
        return $"{value:0} B";
    }
}
