using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>One admitted audio artifact: a real file on disk whose embedded identity resolves to a catalog model.</summary>
/// <param name="DisplayName">The <c>Audio Models/&lt;Prefix&gt;/&lt;id&gt;</c> name shown in the model selector.</param>
/// <param name="ArtifactPath">Absolute path to the primary file, for engines that take an explicit checkpoint path.</param>
public sealed record AudioArtifact(string DisplayName, string ProviderId, string ModelId, string ArtifactPath,
    string Architecture, T2IModel ScannedModel);

/// <summary>Turns the "Audio" handler's raw file scan into the set of models AudioLab will actually offer.
///
/// <para>This is the admission layer. SwarmUI core has no per-handler admission hook, and adding one would
/// mean patching core — so admission lives here instead: an artifact is admitted only when it carries
/// <c>hartsy.component=main</c>, names a provider and model this build knows, and resolves to a catalog row.
/// Shards, codecs, tokenizers and unknown architectures stay in the Audio inventory (visible via
/// <c>ListModels subtype=Audio</c>) but never reach the generation selector.</para>
///
/// <para>Routing reads this index rather than parsing the display name. The old path split
/// <c>Audio Models/&lt;Prefix&gt;/&lt;id&gt;</c> back apart with a prefix match and a last-segment split, which
/// silently produced a null provider for any name that did not match the expected shape.</para></summary>
public static class AudioArtifactIndex
{
    /// <summary>Metadata key carrying AudioLab's provider id.</summary>
    public const string ProviderKey = "hartsy.provider_id";
    /// <summary>Metadata key carrying the catalog model/variant id.</summary>
    public const string ModelKey = "hartsy.model_id";
    /// <summary>Metadata key naming which piece of a multi-file model this artifact is.</summary>
    public const string ComponentKey = "hartsy.component";
    /// <summary>Metadata value of <see cref="ComponentKey"/> that marks the one selectable entrypoint.</summary>
    public const string PrimaryComponent = "main";
    /// <summary>Metadata key pointing at another family's artifact identity, for catalog rows that share weights.</summary>
    public const string AliasKey = "hartsy.alias_of";

    private static readonly object _lock = new();
    private static Dictionary<string, AudioArtifact> _byDisplayName = [];

    /// <summary>Admitted artifacts keyed by their model-selector display name.</summary>
    public static IReadOnlyDictionary<string, AudioArtifact> Admitted
    {
        get { lock (_lock) { return _byDisplayName; } }
    }

    /// <summary>Resolves a selected model name to its artifact, or null when the name is not file-backed.</summary>
    public static AudioArtifact Lookup(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }
        lock (_lock)
        {
            return _byDisplayName.TryGetValue(displayName, out AudioArtifact artifact) ? artifact : null;
        }
    }

    /// <summary>True when this provider has at least one artifact on disk.</summary>
    public static bool HasArtifacts(string providerId)
    {
        lock (_lock)
        {
            foreach (AudioArtifact artifact in _byDisplayName.Values)
            {
                if (artifact.ProviderId == providerId)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>True when this exact catalog row is backed by a validated artifact on disk.</summary>
    public static bool IsInstalled(string providerId, string modelId)
    {
        lock (_lock)
        {
            foreach (AudioArtifact artifact in _byDisplayName.Values)
            {
                if (artifact.ProviderId == providerId && artifact.ModelId == modelId)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Rebuilds the index from the current contents of the "Audio" model handler. Safe to call on
    /// every refresh; it replaces the previous snapshot atomically.</summary>
    public static void Rebuild()
    {
        T2IModelHandler handler = AudioModelTypeRegistration.Handler;
        if (handler is null)
        {
            return;
        }
        Dictionary<string, AudioArtifact> admitted = [];
        int scanned = 0, unknownArch = 0, nonPrimary = 0;
        foreach (T2IModel model in handler.Models.Values)
        {
            scanned++;
            IReadOnlyDictionary<string, string> meta = ReadIdentity(model);
            if (meta is null)
            {
                continue;
            }
            if (!meta.TryGetValue(ComponentKey, out string component) || component != PrimaryComponent)
            {
                nonPrimary++;
                continue;
            }
            if (!meta.TryGetValue(ProviderKey, out string providerId) || string.IsNullOrEmpty(providerId))
            {
                continue;
            }
            meta.TryGetValue(ModelKey, out string modelId);
            foreach ((AudioProviderDefinition provider, AudioModelDefinition row) in ResolveCatalogRows(providerId, modelId, meta))
            {
                string displayName = provider.GetFullModelName(row.Id);
                admitted[displayName] = new AudioArtifact(displayName, provider.Id, row.Id, model.RawFilePath,
                    model.ModelClass?.ID, model);
            }
            if (model.ModelClass is null)
            {
                unknownArch++;
            }
        }
        lock (_lock)
        {
            _byDisplayName = admitted;
        }
        Logs.Info($"[AudioLab] Audio artifact scan: {scanned} file(s) on disk, {admitted.Count} admitted as selectable " +
                  $"({nonPrimary} component/shard file(s) held back).");
        if (unknownArch > 0)
        {
            Logs.Warning($"[AudioLab] {unknownArch} admitted artifact(s) carry an architecture no model class is registered for — " +
                         "audio parameters will not gate correctly for them until a class is registered.");
        }
    }

    /// <summary>Every catalog row this artifact backs. Usually one, but a row may declare that another
    /// family's artifact backs it (whisper-streaming rides on the Whisper base weights), and such a row can
    /// never match on its own provider id.</summary>
    private static List<(AudioProviderDefinition, AudioModelDefinition)> ResolveCatalogRows(string providerId, string modelId,
        IReadOnlyDictionary<string, string> meta)
    {
        List<(AudioProviderDefinition, AudioModelDefinition)> rows = [];
        AddRow(rows, providerId, modelId);
        foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
        {
            foreach (AudioModelDefinition row in provider.Models)
            {
                if (row.BackedByArtifact is not null && row.BackedByArtifact == $"{providerId}/{modelId}")
                {
                    rows.Add((provider, row));
                }
            }
        }
        return rows;
    }

    private static void AddRow(List<(AudioProviderDefinition, AudioModelDefinition)> rows, string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        if (provider is null)
        {
            Logs.Debug($"[AudioLab] Artifact names provider '{providerId}', which this build does not have; not admitted.");
            return;
        }
        foreach (AudioModelDefinition row in provider.Models)
        {
            // A family whose variants all share one artifact (HeartMuLa's quant rows) has no per-variant id to match.
            if (row.Id == modelId || row.BackedByArtifact == $"{providerId}/{modelId}")
            {
                rows.Add((provider, row));
            }
        }
        if (rows.Count == 0)
        {
            Logs.Debug($"[AudioLab] Artifact '{providerId}/{modelId}' matches no catalog row; not admitted.");
        }
    }

    /// <summary>Reads the hartsy.* identity block a stamped artifact carries. Returns null when the file has
    /// none, which is the normal case for a user-dropped checkpoint.</summary>
    private static IReadOnlyDictionary<string, string> ReadIdentity(T2IModel model)
    {
        Dictionary<string, string> found = [];
        T2IModelHandler.ModelMetadataStore store = model.Metadata;
        if (store?.ModelClassType is not null)
        {
            found["modelspec.architecture"] = store.ModelClassType;
        }
        // SwarmUI keeps only the fields it models on ModelMetadataStore, so the hartsy.* block is read back
        // from the file header (or its .swarm.json sidecar) rather than from the cached store.
        IReadOnlyDictionary<string, string> header = AudioArtifactMetadata.Read(model.RawFilePath);
        if (header is null)
        {
            return null;
        }
        foreach ((string key, string value) in header)
        {
            found[key] = value;
        }
        return found.ContainsKey(ProviderKey) ? found : null;
    }
}
