using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>HeartLib provider — full-song music generation with vocals from style tags and lyrics (Apache 2.0).</summary>
public sealed class HeartLibProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the HeartLib provider.</summary>
    public static HeartLibProvider Instance { get; } = new();

    /// <summary>Builds and returns the HeartLib music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("heartlib_music")
        .WithName("HeartLib Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("HeartLib")
        .WithModelClass("heartlib_music", "HeartLib Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("heartlib_music_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    // Each base checkpoint is offered at three precisions (like GGUF quant rows for text models): bf16 (full,
    // default), Q8 (~1.4× faster + ~half the VRAM, near-lossless), and Q4 (smallest/fastest, some quality loss).
    // Quant is NOT a separate download — it's a one-time on-device conversion of the base bf16 weights to a local
    // GGUF cache on first generation; the `quant` EngineConfig key + the `-q8`/`-q4` id suffix carry the choice.
    private static AudioModelDefinition[] Models =>
    [
        .. Variants("3b-hny",  "Happy New Year", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "latest and best HeartMuLa model — best lyrics controllability and music quality"),
        .. Variants("3b-base", "Base",           "HeartMuLa/HeartMuLa-oss-3B",                 "original HeartMuLa release — solid music generation quality"),
        .. Variants("3b-rl",   "RL-Tuned",       "HeartMuLa/HeartMuLa-RL-oss-3B-20260123",     "RL-optimized variant — improved output quality via DPO training"),
    ];

    private static IEnumerable<AudioModelDefinition> Variants(string baseId, string label, string repo, string blurb)
    {
        yield return Make(baseId, $"HeartMuLa 3B ({label})", repo, null,
            $"4B params, {blurb}. Full bf16 precision. ~12GB VRAM (lazy load) or ~16GB (full load).", "~12GB", "~12GB (lazy load)");
        yield return Make($"{baseId}-q8", $"HeartMuLa 3B ({label}) — Q8 (faster, ½ VRAM)", repo, "q8_0",
            $"4B params, {blurb}. Q8 quantized: ~1.4× faster + ~half the VRAM, near-lossless. Converted once to a local cache on first use.", "~12GB download / ~4.5GB cache", "~7GB");
        yield return Make($"{baseId}-q4", $"HeartMuLa 3B ({label}) — Q4 (smallest)", repo, "q4_k",
            $"4B params, {blurb}. Q4 quantized: smallest + fastest, some quality loss. Converted once to a local cache on first use.", "~12GB download / ~2.5GB cache", "~4GB");
    }

    private static AudioModelDefinition Make(string id, string name, string repo, string quant, string description, string size, string vram)
    {
        Dictionary<string, object> cfg = new() { ["model_name"] = repo };
        if (quant is not null) { cfg["quant"] = quant; }
        return new AudioModelDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            SourceUrl = $"https://huggingface.co/{repo}",
            License = "Apache-2.0",
            EstimatedSize = size,
            EstimatedVram = vram,
            EngineConfig = cfg,
        };
    }

    #endregion
}
