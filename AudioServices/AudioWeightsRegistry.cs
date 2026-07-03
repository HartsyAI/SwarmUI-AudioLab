namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// Maps an AudioLab (providerId, modelId) to the downloadable files the in-process C# engine loads —
/// the C# replacement for the Python install flow's HuggingFace pull. One source of truth for download
/// URL + local filename + hash, so the Install button and the routing gate agree.
///
/// <para>Only entries the engine can actually run end-to-end belong here. Each ACE-Step 1.5 variant is a
/// DISTINCT checkpoint (verified per-repo sha256s) and downloads as a multi-file set: weights + the
/// variant's <c>config.json</c> + the shared <c>silence_latent.pt</c> (byte-identical across all repos, so
/// one cached copy). XL / LM-planner entries arrive with their engine phases. STT providers are NOT here:
/// they self-download via the engine's own model cache.</para>
/// </summary>
public static class AudioWeightsRegistry
{
    /// <summary>One downloadable file: where to fetch it and what to save it as (within the
    /// provider's weights directory).</summary>
    public sealed record DownloadSpec(string Url, string FileName, string Sha256)
    {
        public bool HasHash => !string.IsNullOrEmpty(Sha256);
    }

    private const string HfAce = "https://huggingface.co/ACE-Step/";

    private const string SilenceLatentSha = "a778e9dd942f5e8b2c09c55370782d318834432b03dabbcdf70e6ed49ad6358b";

    /// <summary>The shared 600s@25Hz silence latent (identical file in every ACE-Step 1.5 repo).</summary>
    private static readonly DownloadSpec AceSilenceLatent = new(
        Url: HfAce + "acestep-v15-base/resolve/main/silence_latent.pt",
        FileName: "acestep-v15-silence_latent.pt",
        Sha256: SilenceLatentSha);

    /// <summary>Builds the 3-file set for a single-file 2B ACE-Step variant hosted at
    /// <c>ACE-Step/{repo}</c>: weights (sha-pinned) + config.json (small, non-LFS, no hash) + shared silence latent.</summary>
    private static DownloadSpec[] AceStep15Variant(string repo, string variant, string weightsSha, string subdir = "") =>
    [
        new(Url: $"{HfAce}{repo}/resolve/main/{subdir}model.safetensors",
            FileName: $"acestep-v15-{variant}.safetensors",
            Sha256: weightsSha),
        new(Url: $"{HfAce}{repo}/resolve/main/{subdir}config.json",
            FileName: $"acestep-v15-{variant}.config.json",
            Sha256: ""),
        AceSilenceLatent,
    ];

    /// <summary>XL variants (4B DiT, ~10GB): weights = Comfy-Org's single-file bf16 merge (the official repos
    /// ship 4×5GB shards); config.json = the official repo's (has the encoder_* dims the engine needs).</summary>
    private static DownloadSpec[] AceStep15XlVariant(string variant, string weightsSha) =>
    [
        new(Url: $"https://huggingface.co/Comfy-Org/ace_step_1.5_ComfyUI_files/resolve/main/split_files/diffusion_models/acestep_v1.5_{variant.Replace('-', '_')}_bf16.safetensors",
            FileName: $"acestep-v15-{variant}.safetensors",
            Sha256: weightsSha),
        new(Url: $"{HfAce}acestep-v15-{variant}/resolve/main/config.json",
            FileName: $"acestep-v15-{variant}.config.json",
            Sha256: ""),
        AceSilenceLatent,
    ];

    /// <summary>(providerId → (modelId → file set)). The FIRST spec in each set is the primary checkpoint
    /// (what <see cref="Resolve"/> returns for load-path resolution). Absent entries mean "engine can't run
    /// this variant yet".</summary>
    private static readonly Dictionary<string, Dictionary<string, DownloadSpec[]>> _registry = new()
    {
        ["acestep_music"] = new()
        {
            // Every variant is distinct weights (distinct upstream sha256s) — do NOT alias them.
            // The default "turbo" checkpoint lives inside the Ace-Step1.5 bundle repo's subfolder.
            ["turbo"] = AceStep15Variant("Ace-Step1.5", "turbo",
                "3f6e0797fad420a39bd33979eb6e840e30989e34a3794e843d23b60ec6e422d7", subdir: "acestep-v15-turbo/"),
            ["turbo-shift1"] = AceStep15Variant("acestep-v15-turbo-shift1", "turbo-shift1",
                "6a2ae0d66c957eb659fdb438f05e5d2ee62604c311f05ce7464203136a767dc3"),
            ["turbo-shift3"] = AceStep15Variant("acestep-v15-turbo-shift3", "turbo-shift3",
                "ce57ceb4a82890c87d3d6071c70b2392db91f6c7dcf3f96b32ec7205f0d40457"),
            ["turbo-continuous"] = AceStep15Variant("acestep-v15-turbo-continuous", "turbo-continuous",
                "cffa3e3a44d29cfc686b35d45aab3e4f9dc72c959e5e741d92a1dfc14ac35cd0"),
            ["sft"] = AceStep15Variant("acestep-v15-sft", "sft",
                "d4dd3a93870f06720027965b90771f529ab02094b3d29e2518f1d5e097e1af7e"),
            ["base"] = AceStep15Variant("acestep-v15-base", "base",
                "4177f600501a6d4bd81cadaa0abac557ffd15c54e5c8cb52053cdb24a0844d6b"),
            ["xl-turbo"] = AceStep15XlVariant("xl-turbo",
                "86a1afb0a1f711f0e3304ff65d874df3ae6783db683dcf982513fb9b6d14ae71"),
            ["xl-sft"] = AceStep15XlVariant("xl-sft",
                "3c05ae268353b3540fb1fd7db4fd77ffbda9802ec641b624e15648e030ecf3ce"),
            ["xl-base"] = AceStep15XlVariant("xl-base",
                "56bf816fc9a69a5f45635e867b2ad742e1e648eb51fadb7d124cb8332d2e0940"),
        },
    };

    /// <summary>Resolves the PRIMARY checkpoint spec (the weights file) for a provider/model, or null if
    /// the engine can't run it yet.</summary>
    public static DownloadSpec Resolve(string providerId, string modelId)
    {
        DownloadSpec[] specs = SpecsFor(providerId, modelId);
        return specs.Length > 0 ? specs[0] : null;
    }

    /// <summary>Every file needed to run a specific model (weights, sidecars). Empty when unregistered.</summary>
    public static DownloadSpec[] SpecsFor(string providerId, string modelId)
    {
        if (providerId is null || modelId is null)
        {
            return [];
        }
        return _registry.TryGetValue(providerId, out Dictionary<string, DownloadSpec[]> models)
            && models.TryGetValue(modelId, out DownloadSpec[] specs)
            ? specs
            : [];
    }

    /// <summary>All distinct files across every model of a provider (deduped by filename). Prefer
    /// per-model installs (<see cref="SpecsFor"/>) — the full-provider set is large now that every
    /// variant is distinct weights.</summary>
    public static IReadOnlyCollection<DownloadSpec> DistinctFor(string providerId)
    {
        if (providerId is null || !_registry.TryGetValue(providerId, out Dictionary<string, DownloadSpec[]> models))
        {
            return [];
        }
        Dictionary<string, DownloadSpec> byFile = [];
        foreach (DownloadSpec[] specs in models.Values)
        {
            foreach (DownloadSpec spec in specs)
            {
                byFile[spec.FileName] = spec;
            }
        }
        return byFile.Values;
    }
}
