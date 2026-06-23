namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// Maps an AudioLab (providerId, modelId) to the downloadable <c>.safetensors</c> checkpoint the
/// in-process C# engine loads — the C# replacement for the Python install flow's HuggingFace pull.
/// This is the audio analog of the backend extension's <c>SideModels</c> registry: one source of truth
/// for download URL + local filename + hash, so the Install button and the routing gate agree.
///
/// <para>Only entries the engine can actually run end-to-end belong here. ACE-Step 1.5 in the engine is
/// the <b>turbo</b> path (fixed 8-step, no CFG), and only the 2B (non-XL) checkpoints fit its config — so
/// the four turbo variants map to the one turbo file, while base / sft / XL are intentionally absent until
/// the engine adds those modes. STT providers are NOT here: they self-download via the engine's own model
/// cache (see <c>HartsyAudioService.ProviderManagesOwnWeights</c>).</para>
///
/// <para>Hashes are blank for now (the Comfy-Org repo doesn't publish a canonical sha256 here) — the core
/// downloader logs a no-verification warning, the same as several <c>SideModels</c> entries. Pin them in
/// once a known-good copy is on disk.</para>
/// </summary>
public static class AudioWeightsRegistry
{
    /// <summary>One downloadable checkpoint: where to fetch it and what to save it as (within the
    /// provider's weights directory).</summary>
    public sealed record DownloadSpec(string Url, string FileName, string Sha256)
    {
        public bool HasHash => !string.IsNullOrEmpty(Sha256);
    }

    private const string AceStep15Base =
        "https://huggingface.co/Comfy-Org/ace_step_1.5_ComfyUI_files/resolve/main/split_files/diffusion_models/";

    private static readonly DownloadSpec AceStep15Turbo = new(
        Url: AceStep15Base + "acestep_v1.5_turbo.safetensors",
        FileName: "acestep_v1.5_turbo.safetensors",
        // TODO: pin the canonical SHA-256 once a known-good copy is on disk (Comfy-Org publishes none here).
        // Until then AudioWeights.EnsureWeightAsync falls back to a size-floor truncation check.
        Sha256: "");

    /// <summary>(providerId → (modelId → spec)). Absent entries mean "engine can't run this variant yet".</summary>
    private static readonly Dictionary<string, Dictionary<string, DownloadSpec>> _registry = new()
    {
        ["acestep_music"] = new()
        {
            // All four turbo variants are the SAME checkpoint — the shift / continuous differences are
            // inference-time schedule knobs, not separate weights. base / sft / XL are omitted: the engine's
            // v1.5 path is turbo-only today.
            ["turbo"] = AceStep15Turbo,
            ["turbo-shift1"] = AceStep15Turbo,
            ["turbo-shift3"] = AceStep15Turbo,
            ["turbo-continuous"] = AceStep15Turbo,
        },
    };

    /// <summary>Resolves the checkpoint spec for a provider/model, or null if the engine can't run it yet.</summary>
    public static DownloadSpec Resolve(string providerId, string modelId)
    {
        if (providerId is null || modelId is null)
        {
            return null;
        }
        return _registry.TryGetValue(providerId, out Dictionary<string, DownloadSpec> models)
            && models.TryGetValue(modelId, out DownloadSpec spec)
            ? spec
            : null;
    }

    /// <summary>All distinct checkpoints needed to install a provider (deduped — the turbo variants share
    /// one file). Empty if the provider has no engine-runnable checkpoints registered.</summary>
    public static IReadOnlyCollection<DownloadSpec> DistinctFor(string providerId)
    {
        if (providerId is null || !_registry.TryGetValue(providerId, out Dictionary<string, DownloadSpec> models))
        {
            return [];
        }
        Dictionary<string, DownloadSpec> byFile = [];
        foreach (DownloadSpec spec in models.Values)
        {
            byFile[spec.FileName] = spec;
        }
        return byFile.Values;
    }
}
