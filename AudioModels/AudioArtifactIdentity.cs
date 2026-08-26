using System.IO;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>Stamps a freshly installed artifact with the identity <see cref="AudioArtifactIndex"/> admits on.
///
/// <para>Weights fetched from an upstream repo carry no identity of their own — an OpenAI Whisper checkpoint
/// has no idea it is AudioLab's <c>Audio Models/Whisper/base</c>. Without this the whole flow stops one step
/// short: the install succeeds, the files land, the scanner sees them, and nothing becomes selectable.</para>
///
/// <para>Written as a <c>.swarm.json</c> sidecar rather than into the header: the file belongs to whoever
/// published it, rewriting a multi-gigabyte header to add metadata would cost a full copy, and core reads the
/// sidecar first anyway. Artifacts republished by Hartsy carry the same keys embedded, at which point the
/// sidecar is redundant rather than wrong.</para></summary>
public static class AudioArtifactIdentity
{
    /// <summary>Writes the identity sidecar next to <paramref name="primaryPath"/>. No-ops when the path is
    /// missing or the provider is unknown, since a missing sidecar means "not selectable", which is the safe
    /// outcome.</summary>
    public static void WriteSidecar(string primaryPath, string providerId, string modelId, string engineId)
    {
        if (string.IsNullOrEmpty(primaryPath) || !File.Exists(primaryPath))
        {
            Logs.Debug($"[AudioLab] No primary artifact to stamp for '{providerId}/{modelId}'.");
            return;
        }
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        if (provider is null)
        {
            Logs.Warning($"[AudioLab] Cannot stamp '{primaryPath}': no provider '{providerId}'.");
            return;
        }
        AudioModelDefinition row = null;
        foreach (AudioModelDefinition candidate in provider.Models)
        {
            if (candidate.Id == modelId)
            {
                row = candidate;
                break;
            }
        }
        // Per-variant class overrides are the whole reason this reads the row rather than the provider: the
        // JS parameter-gating map is keyed on class id, and several families override it per variant.
        string classId = row?.ModelClassId ?? provider.ModelClassId;
        JObject identity = new()
        {
            ["modelspec.sai_model_spec"] = "1.0.1",
            ["modelspec.architecture"] = classId,
            ["modelspec.implementation"] = "https://github.com/HartsyAI/HartsyInference",
            ["modelspec.title"] = row?.Name ?? $"{provider.Name} {modelId}",
            ["modelspec.author"] = ResolveAuthor(row, provider),
            ["modelspec.description"] = row?.Description ?? "",
            ["modelspec.license"] = string.IsNullOrEmpty(row?.License) ? "Open Source" : row.License,
            ["modelspec.usage_hint"] = $"Audio processing via {provider.Name}",
            ["modelspec.tags"] = $"audiolab,{provider.Category.ToString().ToLowerInvariant()},{provider.EngineGroup}",
            [AudioArtifactIndex.ArtifactSchemaKey] = "1",
            [AudioArtifactIndex.ProviderKey] = providerId,
            [AudioArtifactIndex.ModelKey] = modelId ?? "",
            [AudioArtifactIndex.EngineKey] = engineId,
            [AudioArtifactIndex.ComponentKey] = AudioArtifactIndex.PrimaryComponent,
        };
        // modelspec.resolution is deliberately absent — a value disagreeing with the class's declared standard
        // makes SwarmUI clone the class with its matcher disabled.
        string sidecarPath = Path.ChangeExtension(primaryPath, ".swarm.json");
        try
        {
            File.WriteAllText(sidecarPath, identity.ToString());
            // Core caches model metadata keyed on the WEIGHTS file's write time, so a sidecar appearing beside
            // an unchanged file would be read for identity but never for title/author/license. The model's
            // metadata really did just change, so say so.
            File.SetLastWriteTimeUtc(primaryPath, DateTime.UtcNow);
            Logs.Info($"[AudioLab] Stamped '{Path.GetFileName(primaryPath)}' as {classId} ({providerId}/{modelId}).");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Could not write '{sidecarPath}': {ex.Message}");
        }
    }

    /// <summary>Who published the weights, taken from the source repo's owner rather than the provider name.
    ///
    /// <para>A provider is AudioLab's integration ("Whisper STT"); the author is whoever released the model
    /// ("openai"). Getting this from the URL keeps a stamp written here agreeing with the one the repack tool
    /// embeds, instead of the two disagreeing about the same artifact.</para></summary>
    private static string ResolveAuthor(AudioModelDefinition row, AudioProviderDefinition provider)
    {
        string url = row?.SourceUrl ?? "";
        const string HfPrefix = "https://huggingface.co/";
        if (url.StartsWith(HfPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string owner = url[HfPrefix.Length..].Split('/')[0];
            if (!string.IsNullOrWhiteSpace(owner))
            {
                return owner;
            }
        }
        return provider.Name;
    }

    /// <summary>Removes the identity sidecar, so an uninstalled model stops being admitted even if some other
    /// file of its bundle survives.</summary>
    public static void RemoveSidecar(string primaryPath)
    {
        if (string.IsNullOrEmpty(primaryPath))
        {
            return;
        }
        string sidecarPath = Path.ChangeExtension(primaryPath, ".swarm.json");
        try
        {
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Could not delete '{sidecarPath}': {ex.Message}");
        }
    }
}
