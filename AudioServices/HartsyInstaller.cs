using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Audio.Cache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>What became of a <see cref="HartsyInstaller.InstallAsync"/> call.</summary>
public enum HartsyInstallStatus
{
    /// <summary>The model has no Hartsy card.</summary>
    NoCard,
    /// <summary>The model's upstream files were already on disk, so nothing was downloaded.</summary>
    UpstreamPresent,
    /// <summary>Every file of the card is on disk and linked into place.</summary>
    Installed,
    /// <summary>The card exists but cannot be used (refused, incomplete or unplaceable).</summary>
    Unavailable,
}

/// <summary>Outcome of a Hartsy install, with the reason when it did not install.</summary>
public sealed record HartsyInstallResult(HartsyInstallStatus Status, string Message);

/// <summary>Installs a model from its Hartsy card (<see cref="HartsyRepacks"/>).
///
/// <para>The card's manifest lists every file with the path it takes inside the model folder. Weight files are
/// safetensors that name, in <c>hartsy.stands_in_for</c>, the upstream path they replace: they land in the provider's
/// weights folder and the engine links them into place (<see cref="AudioStandIns"/>). Configs and tokenizers are read
/// by the engine at the upstream path itself, so each goes beside the upstream files its folder's weights stand in
/// for. A shard index names the Hartsy shards, so it stays beside them, and a copy naming the upstream shards goes
/// beside the links.</para>
///
/// <para>Each install writes a record of the files the model uses, so removing it deletes them unless another
/// installed model still uses them.</para></summary>
public static class HartsyInstaller
{
    /// <summary>One file a card's manifest lists.</summary>
    internal sealed record CardFile(string RelativePath, long? Size, string Sha256, string DownloadUrl, string UploadStatus)
    {
        /// <summary>A safetensors file: weights the engine reaches through a stand-in link.</summary>
        public bool IsWeights => RelativePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);

        /// <summary>A sharded checkpoint's index.</summary>
        public bool IsShardIndex => RelativePath.EndsWith(".index.json", StringComparison.OrdinalIgnoreCase);

        /// <summary>The folder inside the model folder, "" for the top.</summary>
        public string Folder => RelativePath.Contains('/') ? RelativePath[..RelativePath.LastIndexOf('/')] : "";

        /// <summary>The file name.</summary>
        public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
    }

    /// <summary>What a card's download manifest says to fetch.</summary>
    internal sealed record CardManifest(string CardId, string Title, string BlockedReason, CardFile Main, IReadOnlyList<CardFile> Companions);

    /// <summary>One install's settings, the files of this model's previous install, and the files it uses.</summary>
    private sealed record InstallRun(string Folder, Dictionary<string, string> Headers, Func<string, Task> OnProgress,
        CancellationToken Cancel, HashSet<string> Owned, List<string> Used);

    /// <summary>Where install records live, under the audio models root.</summary>
    private const string RecordFolder = ".hartsy-installs";

    /// <summary>The subfolder of a provider's weights folder that holds its cards' files.</summary>
    public const string HartsyFolder = "hartsy";

    /// <summary>One install at a time: cards share companion files, and two downloads of one file would share its
    /// temporary path.</summary>
    private static readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>For the manifest only; files go through core's downloader.</summary>
    private static readonly HttpClient _http = NetworkBackendUtils.MakeHttpClient(2);

    /// <summary>Cards a first-use install already failed on this run, so a broken card is not retried on every
    /// generation.</summary>
    private static readonly ConcurrentDictionary<string, string> _firstUseFailures = new();

    /// <summary>Downloads a model's card and links it into place, or checks and repairs an earlier Hartsy install of
    /// it. A model with no card returns <see cref="HartsyInstallStatus.NoCard"/>, one whose upstream files are already
    /// on disk <see cref="HartsyInstallStatus.UpstreamPresent"/> (a second copy would only take space), and a card that
    /// cannot be used <see cref="HartsyInstallStatus.Unavailable"/> with the reason; the caller then installs from
    /// upstream as before.</summary>
    /// <param name="apiKey">The user's Hartsy API key, for a card their tier unlocks. Optional.</param>
    public static Task<HartsyInstallResult> InstallAsync(AudioProviderDefinition provider, string modelId, string apiKey,
        Func<string, Task> onProgress, CancellationToken cancel)
        => InstallCoreAsync(provider, modelId, apiKey, onProgress, cancel, recheck: true);

    /// <param name="recheck">False on first use: a model another caller installed while this one waited is left alone.</param>
    private static async Task<HartsyInstallResult> InstallCoreAsync(AudioProviderDefinition provider, string modelId, string apiKey,
        Func<string, Task> onProgress, CancellationToken cancel, bool recheck)
    {
        string card = HartsyRepacks.CardFor(provider.Id, modelId);
        if (card is null)
        {
            return new(HartsyInstallStatus.NoCard, $"{provider.Name} '{modelId}' has no Hartsy card.");
        }
        await _installLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            bool recorded = HasRecord(provider.Id, modelId);
            if (recorded && !recheck)
            {
                return new(HartsyInstallStatus.Installed, "");
            }
            if (!recorded && AudioEngineBridge.WeightsPresent(provider.Id, modelId))
            {
                AdoptSiblingRecord(Path.GetFullPath(AudioConfiguration.ModelRoot), provider.Id, modelId, card);
                return new(HartsyInstallStatus.UpstreamPresent, $"{provider.Name} '{modelId}' is already on disk.");
            }
            (CardManifest manifest, bool keyAccepted, string refusal) = await FetchManifestAsync(card, apiKey, cancel).ConfigureAwait(false);
            string problem = refusal ?? Problem(manifest);
            if (problem is not null)
            {
                return new(HartsyInstallStatus.Unavailable, problem);
            }
            Dictionary<string, string> headers = keyAccepted && !string.IsNullOrEmpty(apiKey)
                ? new() { ["X-API-Key"] = ModelsAPI.TokenTextLimiter.TrimToMatches(apiKey) }
                : null;
            string root = Path.GetFullPath(AudioConfiguration.ModelRoot);
            // A folder of its own: a loader that globs its folder (YuE's variant folders) must see only the links
            // named for it there, never the card's files beside them.
            string folder = Path.Combine(AudioWeights.WeightsDirectory(AudioProviderRegistry.GetById(HartsyRepacks.FolderOwner(provider.Id)) ?? provider), HartsyFolder);
            InstallRun run = new(folder, headers, onProgress, cancel, ReadOwnedFiles(root, provider.Id, modelId), []);
            await AudioWeights.Report(onProgress, $"Downloading {manifest.Title} from Hartsy...");
            Dictionary<string, string> weightPaths = new(StringComparer.Ordinal);
            // Weights first and the main file last, so a main file on disk means its weights are too.
            foreach (CardFile file in manifest.Companions.Where(f => f.IsWeights).Append(manifest.Main))
            {
                cancel.ThrowIfCancellationRequested();
                string path = LocalPath(folder, file.RelativePath);
                await FetchAsync(run, file, path).ConfigureAwait(false);
                weightPaths[file.RelativePath] = path;
                run.Used.Add(path);
            }
            if (AudioStandIns.ReadDeclared(weightPaths[manifest.Main.RelativePath]).Count == 0)
            {
                return new(HartsyInstallStatus.Unavailable, $"'{manifest.Main.RelativePath}' names no upstream path to stand in for, so the engine cannot find it.");
            }
            IReadOnlyList<string> DeclaredFor(CardFile weights) => AudioStandIns.ReadDeclared(weightPaths[weights.RelativePath]);
            foreach (CardFile sidecar in manifest.Companions.Where(f => !f.IsWeights))
            {
                cancel.ThrowIfCancellationRequested();
                List<string> targets = [.. SidecarTargets(manifest, sidecar, DeclaredFor).Select(t => LocalPath(root, t))];
                if (sidecar.IsShardIndex)
                {
                    await PlaceShardIndexAsync(run, manifest, sidecar, targets, DeclaredFor).ConfigureAwait(false);
                }
                else if (targets.Count == 0)
                {
                    // No weights in its folder say where the engine reads it, so keep the card's own layout.
                    string path = LocalPath(folder, sidecar.RelativePath);
                    await FetchAsync(run, sidecar, path).ConfigureAwait(false);
                    run.Used.Add(path);
                }
                else
                {
                    await PlaceSidecarAsync(run, sidecar, targets).ConfigureAwait(false);
                }
            }
            int linked = AudioStandIns.Sync(root);
            WriteRecord(root, provider.Id, modelId, card, run.Used);
            await AudioWeights.Report(onProgress, $"{manifest.Title} installed from Hartsy ({run.Used.Distinct().Count()} file(s), {linked} new link(s)).");
            Logs.Info($"[AudioLab] Installed '{provider.Id}/{modelId}' from Hartsy card {card}.");
            return new(HartsyInstallStatus.Installed, "");
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>Installs an engine-managed model from Hartsy before its first generation, so the engine finds the
    /// repack instead of fetching upstream. Skips models that are on disk already, models with no card, and cards
    /// that failed earlier this run. Never fails the generation: on any problem the engine fetches as before.</summary>
    public static async Task EnsureOnFirstUseAsync(string providerId, string modelId, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(modelId) || !AudioEngineBridge.ProviderManagesOwnWeights(providerId))
        {
            return;
        }
        string card = HartsyRepacks.CardFor(providerId, modelId);
        if (card is null || _firstUseFailures.ContainsKey(card) || HasRecord(providerId, modelId))
        {
            return;
        }
        if (AudioEngineBridge.WeightsPresent(providerId, modelId))
        {
            AdoptSiblingRecord(Path.GetFullPath(AudioConfiguration.ModelRoot), providerId, modelId, card);
            return;
        }
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        if (provider is null)
        {
            return;
        }
        try
        {
            HartsyInstallResult result = await InstallCoreAsync(provider, modelId, null, msg =>
            {
                Logs.Info($"[AudioLab] {msg}");
                return Task.CompletedTask;
            }, cancel, recheck: false).ConfigureAwait(false);
            if (result.Status == HartsyInstallStatus.Unavailable)
            {
                _firstUseFailures[card] = result.Message;
                Logs.Warning($"[AudioLab] Hartsy card for '{providerId}/{modelId}' is not usable ({result.Message}); the engine will fetch it upstream.");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _firstUseFailures[card] = ex.Message;
            Logs.Warning($"[AudioLab] Hartsy download for '{providerId}/{modelId}' failed ({ex.Message}); the engine will fetch it upstream.");
        }
    }

    /// <summary>Deletes the files a Hartsy install of this model wrote, except any another installed model still
    /// uses, then drops the engine's links to them. Returns false, doing nothing, when the model was not installed
    /// from Hartsy.</summary>
    public static bool RemoveInstall(string providerId, string modelId)
    {
        string root = Path.GetFullPath(AudioConfiguration.ModelRoot);
        string record = RecordPath(root, providerId, modelId);
        if (!File.Exists(record))
        {
            return false;
        }
        HashSet<string> keep = new(StringComparer.Ordinal);
        foreach (string other in AllRecords(root))
        {
            if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(record), StringComparison.Ordinal))
            {
                keep.UnionWith(ReadRecordFiles(other));
            }
        }
        foreach (string relative in ReadRecordFiles(record))
        {
            if (keep.Contains(relative))
            {
                continue;
            }
            string path = LocalPath(root, relative);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logs.Info($"[AudioLab] Deleted Hartsy file: {path}");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not delete '{path}': {ex.Message}");
            }
        }
        try
        {
            File.Delete(record);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Could not delete install record '{record}': {ex.Message}");
        }
        AudioStandIns.Sync(root);
        return true;
    }

    /// <summary>Whether this model was installed from its Hartsy card.</summary>
    public static bool HasRecord(string providerId, string modelId)
        => File.Exists(RecordPath(Path.GetFullPath(AudioConfiguration.ModelRoot), providerId, modelId));

    /// <summary>Reads a card's manifest, retrying without the key when Hartsy rejects it. Returns the refusal reason
    /// instead of a manifest when the card cannot be read.</summary>
    private static async Task<(CardManifest Manifest, bool KeyAccepted, string Refusal)> FetchManifestAsync(string card, string apiKey, CancellationToken cancel)
    {
        bool withKey = !string.IsNullOrEmpty(apiKey);
        (HttpStatusCode status, string body) = await GetAsync(HartsyRepacks.ManifestUrl(card), withKey ? apiKey : null, cancel).ConfigureAwait(false);
        if (withKey && status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Logs.Debug($"[AudioLab] Hartsy rejected the API key for card {card} ({(int)status}); retrying as public.");
            withKey = false;
            (status, body) = await GetAsync(HartsyRepacks.ManifestUrl(card), null, cancel).ConfigureAwait(false);
        }
        if (status != HttpStatusCode.OK)
        {
            return (null, false, $"Hartsy answered {(int)status} for card {card}.");
        }
        try
        {
            return (ParseManifest(card, JObject.Parse(body)), withKey, null);
        }
        catch (JsonException ex)
        {
            return (null, false, $"Hartsy's manifest for card {card} is not readable: {ex.Message}");
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, string apiKey, CancellationToken cancel)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("X-API-Key", ModelsAPI.TokenTextLimiter.TrimToMatches(apiKey));
            }
            using HttpResponseMessage response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            Logs.Debug($"[AudioLab] Hartsy request to '{url}' failed: {ex.Message}");
            return (HttpStatusCode.ServiceUnavailable, "");
        }
    }

    /// <summary>Reads the JSON <c>GET /download/{id}/manifest</c> answers.</summary>
    internal static CardManifest ParseManifest(string cardId, JObject json)
    {
        List<CardFile> companions = [];
        if (json["files"] is JArray files)
        {
            foreach (JToken token in files)
            {
                if (token is JObject file)
                {
                    companions.Add(ParseFile(file, "uploadStatus"));
                }
            }
        }
        return new CardManifest(cardId, json.Value<string>("title") ?? cardId, json.Value<string>("blockedReason"),
            json["main"] is JObject main ? ParseFile(main, null) : null, companions);
    }

    private static CardFile ParseFile(JObject file, string statusKey) => new(
        (file.Value<string>("relativePath") ?? "").Replace('\\', '/'),
        file.Value<long?>("fileSize"),
        NormalizeSha(file.Value<string>("sha256")),
        file.Value<string>("downloadUrl"),
        statusKey is null ? "completed" : file.Value<string>(statusKey) ?? "");

    /// <summary>Lowercase hex, no prefix, or null.</summary>
    private static string NormalizeSha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }
        sha = sha.Trim().ToLowerInvariant();
        return sha.StartsWith("0x") ? sha[2..] : sha;
    }

    /// <summary>Why a manifest cannot be installed, or null when it can.</summary>
    internal static string Problem(CardManifest manifest)
    {
        if (manifest?.Main is null)
        {
            return $"Hartsy's manifest for card {manifest?.CardId} lists no main file.";
        }
        if (!string.IsNullOrEmpty(manifest.BlockedReason) || manifest.Main.DownloadUrl is null)
        {
            return $"'{manifest.Title}' cannot be downloaded: {manifest.BlockedReason ?? "no download route"}.";
        }
        if (!manifest.Main.IsWeights)
        {
            return $"'{manifest.Title}' ships '{manifest.Main.Name}', not a safetensors repack, so it cannot stand in for the upstream files.";
        }
        foreach (CardFile file in manifest.Companions.Append(manifest.Main))
        {
            if (SafeRelativePath(file.RelativePath) is null)
            {
                return $"'{manifest.Title}' names an unsafe path '{file.RelativePath}'.";
            }
            if (AbsoluteUrl(file.DownloadUrl) is null)
            {
                return $"'{file.RelativePath}' of '{manifest.Title}' has no Hartsy download route.";
            }
            if (!string.Equals(file.UploadStatus, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{file.RelativePath}' of '{manifest.Title}' is {file.UploadStatus}, not completed.";
            }
        }
        return null;
    }

    /// <summary>A relative path held to the same rules the engine applies to stand-in paths: no root, no empty,
    /// "." or ".." segment. Returns it with '/' separators, or null when unsafe.</summary>
    internal static string SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string relative = path.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(s => s is "" or "." or ".."))
        {
            return null;
        }
        return relative;
    }

    /// <summary>The absolute URL for a manifest route. Only Hartsy's own routes are accepted.</summary>
    internal static string AbsoluteUrl(string route)
    {
        if (string.IsNullOrEmpty(route))
        {
            return null;
        }
        if (route.StartsWith('/') && !route.StartsWith("//"))
        {
            return HartsyRepacks.SiteUrl + route;
        }
        return route.StartsWith(HartsyRepacks.SiteUrl + "/", StringComparison.OrdinalIgnoreCase) ? route : null;
    }

    /// <summary>Where the engine reads a config or tokenizer: beside the upstream files that every weights file in its
    /// own folder of the card stands in for, the main file's first. MeloTTS reads its <c>vocab.txt</c> beside the BERT
    /// encoder, not beside its own checkpoint, and both sit in the card's top folder. A card that needs a file beside
    /// only one of its components keeps that component in a folder of its own. Paths are relative to the audio models
    /// root; empty when no weights in that folder declare a stand-in.</summary>
    internal static IReadOnlyList<string> SidecarTargets(CardManifest manifest, CardFile sidecar, Func<CardFile, IReadOnlyList<string>> declaredFor)
    {
        List<string> targets = [];
        foreach (CardFile weights in WeightsInFolder(manifest, sidecar.Folder))
        {
            foreach (string declared in declaredFor(weights))
            {
                string target = Combine(Folder(declared), sidecar.Name);
                if (!targets.Contains(target))
                {
                    targets.Add(target);
                }
            }
        }
        return targets;
    }

    /// <summary>The weights in one folder of the card, main file first.</summary>
    private static IEnumerable<CardFile> WeightsInFolder(CardManifest manifest, string folder)
    {
        if (manifest.Main.Folder == folder)
        {
            yield return manifest.Main;
        }
        foreach (CardFile file in manifest.Companions)
        {
            if (file.IsWeights && file.Folder == folder)
            {
                yield return file;
            }
        }
    }

    /// <summary>Rewrites a shard index whose map names the card's shard files so it names the upstream shard names
    /// they stand in for. Returns null unless every entry then names one of those upstream names; an index that
    /// already names them comes back unchanged.</summary>
    internal static JObject TranslateShardIndex(JObject index, IReadOnlyDictionary<string, string> cardToUpstream)
    {
        if (index["weight_map"] is not JObject map || cardToUpstream.Count == 0)
        {
            return null;
        }
        HashSet<string> upstream = new(cardToUpstream.Values, StringComparer.Ordinal);
        JObject translated = (JObject)index.DeepClone();
        JObject translatedMap = (JObject)translated["weight_map"];
        foreach (JProperty entry in map.Properties())
        {
            string shard = entry.Value?.ToString() ?? "";
            string name = cardToUpstream.TryGetValue(shard, out string renamed) ? renamed : shard;
            if (!upstream.Contains(name))
            {
                return null;
            }
            translatedMap[entry.Name] = name;
        }
        return translated;
    }

    /// <summary>Places a shard index: the card's copy beside the card's shards, and a copy naming the upstream shard
    /// names beside their links, where the engine reads it.</summary>
    private static async Task PlaceShardIndexAsync(InstallRun run, CardManifest manifest, CardFile index, IReadOnlyList<string> targets,
        Func<CardFile, IReadOnlyList<string>> declaredFor)
    {
        string cardCopy = LocalPath(run.Folder, index.RelativePath);
        await FetchAsync(run, index, cardCopy).ConfigureAwait(false);
        run.Used.Add(cardCopy);
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (CardFile weights in WeightsInFolder(manifest, index.Folder))
        {
            foreach (string declared in declaredFor(weights))
            {
                names[weights.Name] = declared[(declared.LastIndexOf('/') + 1)..];
            }
        }
        JObject translated = TranslateShardIndex(JObject.Parse(await File.ReadAllTextAsync(cardCopy, run.Cancel).ConfigureAwait(false)), names);
        if (translated is null)
        {
            Logs.Debug($"[AudioLab] '{index.RelativePath}' of '{manifest.Title}' does not map onto its shards' upstream names; kept beside the card's shards only.");
            return;
        }
        string text = translated.ToString(Formatting.Indented);
        foreach (string target in targets)
        {
            run.Used.Add(target);
            if (File.Exists(target) && !(run.Owned.Contains(target) && await File.ReadAllTextAsync(target, run.Cancel).ConfigureAwait(false) != text))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            await File.WriteAllTextAsync(target, text, run.Cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Puts a config or tokenizer at each target. A file already there is kept, since an upstream copy is the
    /// file the engine has been reading, unless this model's earlier Hartsy install wrote it and the card has changed
    /// it since.</summary>
    private static async Task PlaceSidecarAsync(InstallRun run, CardFile sidecar, IReadOnlyList<string> targets)
    {
        run.Used.AddRange(targets);
        List<string> pending = [.. targets.Where(t => !File.Exists(t) || IsStale(run, t, sidecar.Sha256))];
        if (pending.Count == 0)
        {
            return;
        }
        // Replaces a stale copy itself: the download fails its hash check and is fetched again.
        await FetchAsync(run, sidecar, pending[0]).ConfigureAwait(false);
        foreach (string other in pending.Skip(1))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(other));
            File.Copy(pending[0], other, overwrite: true);
        }
    }

    /// <summary>True for a file this model's earlier Hartsy install wrote whose content no longer matches the card.</summary>
    private static bool IsStale(InstallRun run, string path, string sha256)
    {
        if (sha256 is null || !run.Owned.Contains(path))
        {
            return false;
        }
        using FileStream stream = File.OpenRead(path);
        return !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads one file to <paramref name="path"/>, checked against the card's hash; a file already there
    /// with that hash is kept.</summary>
    private static async Task FetchAsync(InstallRun run, CardFile file, string path)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        AudioWeightsRegistry.DownloadSpec spec = new(AbsoluteUrl(file.DownloadUrl), Path.GetFileName(path), file.Sha256 ?? "");
        await AudioWeights.EnsureWeightAsync(spec, directory, run.OnProgress, run.Cancel, run.Headers).ConfigureAwait(false);
    }

    /// <summary>A '/'-separated relative path under <paramref name="baseDirectory"/>, as a full local path.</summary>
    private static string LocalPath(string baseDirectory, string relative)
        => Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Folder(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    #region Install records

    private static string RecordPath(string root, string providerId, string modelId)
        => Path.Combine(root, RecordFolder, RecordName(providerId), $"{RecordName(modelId)}.json");

    /// <summary>An id as a file name: letters, digits, '.', '-' and '_' kept (model ids like "1.7B-Base" carry dots),
    /// anything else replaced.</summary>
    internal static string RecordName(string id)
    {
        string name = new([.. (id ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return name.Trim('.').Length == 0 ? "_" : name;
    }

    /// <summary>The files this model's previous Hartsy install recorded, as full paths.</summary>
    private static HashSet<string> ReadOwnedFiles(string root, string providerId, string modelId)
    {
        string record = RecordPath(root, providerId, modelId);
        return File.Exists(record) ? new(ReadRecordFiles(record).Select(f => LocalPath(root, f)), StringComparer.Ordinal) : new(StringComparer.Ordinal);
    }

    /// <summary>Records this model as a user of the files another model's install of the same card placed, so that
    /// removing the other model keeps them. Without this a model whose files are already on disk has no record, and
    /// removing the other model deletes files this one still uses. Writes nothing when no sibling record exists.</summary>
    internal static void AdoptSiblingRecord(string root, string providerId, string modelId, string card)
    {
        if (HasRecord(providerId, modelId))
        {
            return;
        }
        foreach (string other in AllRecords(root))
        {
            if (!string.Equals(RecordCard(other), card, StringComparison.Ordinal))
            {
                continue;
            }
            WriteRecord(root, providerId, modelId, card, ReadRecordFiles(other).Select(f => LocalPath(root, f)).ToList());
            Logs.Info($"[AudioLab] '{providerId}/{modelId}' shares Hartsy card {card} with an installed model; recorded its files too.");
            return;
        }
    }

    /// <summary>The card id a record names, or null when the record cannot be read.</summary>
    private static string RecordCard(string recordPath)
    {
        try
        {
            return JObject.Parse(File.ReadAllText(recordPath)).Value<string>("card");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> AllRecords(string root)
    {
        string folder = Path.Combine(root, RecordFolder);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories) : [];
    }

    /// <summary>Records the files a model uses, relative to the audio models root.</summary>
    internal static void WriteRecord(string root, string providerId, string modelId, string card, IEnumerable<string> files)
    {
        string path = RecordPath(root, providerId, modelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        JObject record = new()
        {
            ["card"] = card,
            ["provider_id"] = providerId,
            ["model_id"] = modelId,
            ["files"] = new JArray(files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)),
        };
        File.WriteAllText(path, record.ToString(Formatting.Indented));
    }

    private static IEnumerable<string> ReadRecordFiles(string recordPath)
    {
        try
        {
            JObject record = JObject.Parse(File.ReadAllText(recordPath));
            return record["files"] is JArray files ? [.. files.Select(f => f.ToString()).Where(f => SafeRelativePath(f) is not null)] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logs.Warning($"[AudioLab] Could not read install record '{recordPath}': {ex.Message}");
            return [];
        }
    }

    #endregion
}
