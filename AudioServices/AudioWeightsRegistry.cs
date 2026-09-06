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

        /// <summary>Optional alternate source, tried (recursively) only if the primary URL fails to download.
        /// Used so an install prefers a small pre-converted repack but still succeeds off the canonical
        /// full-size source when the repack host is unreachable / not yet published.</summary>
        public DownloadSpec Fallback { get; init; }
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

    private const string OpenWakeWordRelease = "https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/";

    /// <summary>openWakeWord's shared feature-extraction backbone (melspectrogram + speech embedding),
    /// pinned to the v0.5.1 GitHub release — the same two files HartsyInference.Audio's WakeMelFrontend
    /// and SpeechEmbeddingModel were ported against. Verified (2026-08-19) against the actual downloaded
    /// bytes: melspectrogram.onnx's initializers are named <c>0.stft.conv_real.weight</c> /
    /// <c>0.stft.conv_imag.weight</c> / <c>1.melW</c>, and embedding_model.onnx has the expected 41
    /// <c>model/conv2d_N/Conv2D..._fused_bn</c> tensors — an exact match for what those loaders expect, not
    /// just "a file with the right name". Sha256s below are computed from that same verified download.
    ///
    /// <para>Not a provider/model pair like everything else in this file — wake isn't modeled as an
    /// AudioProviderDefinition (no AudioCategory.Wake), so this is looked up directly by
    /// WakeWordService.InstallBackboneAsync via SpecsFor("wake", "backbone") rather than through the
    /// AudioProviderDefinition-based install flow.</para></summary>
    // Silero VAD v6, from silero-vad's own repository (MIT). Deliberately NOT from openWakeWord's release,
    // which pins an older revision whose learned tensors differ from this one — see the engine's
    // docs/Research/WAKE_WORD_DETECTION.md. The engine reads this ONNX directly, so there is no conversion
    // step and nothing for anyone to host. Hash taken from the downloaded bytes 2026-09-05.
    private static readonly DownloadSpec[] WakeVad =
    [
        new(Url: "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx",
            FileName: "silero_vad.onnx",
            Sha256: "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3"),
    ];

    private static readonly DownloadSpec[] WakeBackbone =
    [
        new(Url: OpenWakeWordRelease + "melspectrogram.onnx",
            FileName: "melspectrogram.onnx",
            Sha256: "ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f"),
        new(Url: OpenWakeWordRelease + "embedding_model.onnx",
            FileName: "embedding_model.onnx",
            Sha256: "70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f"),
    ];

    /// <summary>openWakeWord's pretrained stock heads (v0.5.1 release) — for getting the listener running
    /// end-to-end before a user has trained their own word via AudioLabWakeTrainWord. Saved as
    /// <c>{word}.onnx</c>, not upstream's <c>{word}_v0.1.onnx</c>, because WakeModelSet.Load derives the
    /// wake word's name from the filename (minus extension).</summary>
    private static DownloadSpec StockHead(string word, string sha256) => new(
        Url: $"{OpenWakeWordRelease}{word}_v0.1.onnx",
        FileName: $"{word}.onnx",
        Sha256: sha256);

    private const string HfYue = "https://huggingface.co/m-a-p/";

    /// <summary>The shared X-Codec acoustic decoder. DEFAULT = a small pre-converted <c>xcodec.safetensors</c>
    /// repack (the decode-only tensors, ~0.8 GB) hosted by us — the install downloads it ready-to-load with NO
    /// on-the-fly conversion. FALLBACK (repack host unreachable / not yet published) = the canonical
    /// m-a-p/xcodec_mini_infer torch <c>.pth</c> (1.36 GB), which the YuE loader converts to
    /// <c>xcodec.safetensors</c> ONCE on first load and reuses thereafter. Byte-identical across YuE variants,
    /// so one copy lives beside the per-variant folders (FileName has no variant prefix).</summary>
    private static readonly DownloadSpec YueXCodec = new(
        Url: "https://huggingface.co/HartsyAI/YuE-xcodec-mini-safetensors/resolve/main/xcodec.safetensors",
        FileName: "xcodec.safetensors",
        Sha256: "")
    {
        Fallback = new(
            Url: HfYue + "xcodec_mini_infer/resolve/main/final_ckpt/ckpt_00360000.pth",
            FileName: "xcodec/ckpt_00360000.pth",
            Sha256: ""),
    };

    /// <summary>YuE Stage-1 (7B, ~12.5GB): the m-a-p/YuE-s1-7B-anneal-{variant} folder — 3 sharded safetensors +
    /// index + config + tokenizer, downloaded into a per-variant subfolder — plus the shared X-Codec .pth. The
    /// engine loads the folder (sharded) and converts the .pth to xcodec.safetensors on first load.</summary>
    private static DownloadSpec[] YueVariant(string variant)
    {
        string repo = $"{HfYue}YuE-s1-7B-anneal-{variant}/resolve/main/";
        string[] files = ["model-00001-of-00003.safetensors", "model-00002-of-00003.safetensors",
            "model-00003-of-00003.safetensors", "model.safetensors.index.json", "config.json",
            "generation_config.json", "tokenizer.model"];
        DownloadSpec[] set = new DownloadSpec[files.Length + 1];
        for (int i = 0; i < files.Length; i++)
        {
            set[i] = new(Url: repo + files[i], FileName: $"{variant}/{files[i]}", Sha256: "");
        }
        set[^1] = YueXCodec;
        return set;
    }

    /// <summary>Providers whose checkpoint is a multi-file FOLDER (sharded weights + sidecars), not a single
    /// <c>.safetensors</c>. Their load path resolves to the variant DIRECTORY, and downloads land in a
    /// <c>{variant}/</c> subfolder rather than a single file.</summary>
    public static bool IsFolderCheckpoint(string providerId) => providerId == "yue_music";

    /// <summary>(providerId → (modelId → file set)). The FIRST spec in each set is the primary checkpoint
    /// (what <see cref="Resolve"/> returns for load-path resolution). Absent entries mean "engine can't run
    /// this variant yet".</summary>
    private static readonly Dictionary<string, Dictionary<string, DownloadSpec[]>> _registry = new()
    {
        ["wake"] = new()
        {
            ["backbone"] = WakeBackbone,
            ["vad"] = WakeVad,
        },
        // Only "hey_jarvis" is pinned here — the only stock head actually downloaded and sha256-verified
        // (2026-08-19). openWakeWord's v0.5.1 release also ships alexa_v0.1.onnx and hey_mycroft_v0.1.onnx
        // (confirmed to exist, URL untested-download) — add them the same way once someone's actually
        // pulled and hashed the bytes; don't pin a hash for a file nobody here has verified.
        ["wake_stock_heads"] = new()
        {
            ["hey_jarvis"] = [StockHead("hey_jarvis", "94a13cfe60075b132f6a472e7e462e8123ee70861bc3fb58434a73712ee0d2cb")],
        },
        ["yue_music"] = new()
        {
            ["en-cot"] = YueVariant("en-cot"),
            ["en-icl"] = YueVariant("en-icl"),
            ["zh-cot"] = YueVariant("zh-cot"),
            ["zh-icl"] = YueVariant("zh-icl"),
        },
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

    /// <summary>Model ids registered under <paramref name="providerId"/>, so a UI can offer what is actually
    /// available for one-click install instead of hardcoding a list that drifts from this file.</summary>
    public static IReadOnlyCollection<string> ModelsFor(string providerId)
    {
        if (providerId is null || !_registry.TryGetValue(providerId, out Dictionary<string, DownloadSpec[]> models))
        {
            return [];
        }
        return [.. models.Keys.OrderBy(k => k)];
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
