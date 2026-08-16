using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>MiniMax Music 3 — full songs up to six minutes at 44.1 kHz stereo, from a music description plus
/// lyrics. An 8B Qwen3 emits one semantic RVQ code per 25 Hz frame, a 0.6B depth decoder fills in the seven
/// residual codebooks, and the two models' hidden states condition a flow-matching transformer whose latents a
/// DAC-style vocoder decodes.</summary>
public sealed class MiniMaxMusic3Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the MiniMax Music 3 provider.</summary>
    public static MiniMaxMusic3Provider Instance { get; } = new();

    /// <summary>Builds and returns the MiniMax Music 3 generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("minimax_music3")
        .WithName("MiniMax Music 3")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("MiniMaxMusic3")
        .WithModelClass("minimax_music3", "MiniMax Music 3")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("minimax_music3_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    // One released checkpoint, offered at three precisions. Quant is not a separate download: the language model's
    // projections are converted once to a local GGUF cache on first generation, and the quantized rows also cast
    // the flow transformer to BF16 and the KV cache to F16, which is what brings this under 12 GB.
    private const string Repo = "MiniMaxAI/MiniMax-Music3";

    private static AudioModelDefinition[] Models =>
    [
        Make("base", "MiniMax Music 3", null,
            "8B autoregressive + 2.4B flow-matching, 44.1 kHz stereo, songs to six minutes. Full checkpoint "
            + "precision. This is the parity baseline and wants a 24GB card.", "~28GB", "~22GB"),
        Make("q8", "MiniMax Music 3 Q8 (recommended)", "q8_0",
            "8B autoregressive + 2.4B flow-matching, 44.1 kHz stereo. Q8 language model, BF16 transformer, F16 KV "
            + "cache. Near-lossless and the practical default. Converted once to a local cache on first use.",
            "~28GB download / ~8GB cache", "~12GB"),
        Make("q4", "MiniMax Music 3 Q4 (smallest)", "q4_k",
            "8B autoregressive + 2.4B flow-matching, 44.1 kHz stereo. Q4 language model, smallest and fastest with "
            + "some quality loss. Converted once to a local cache on first use.",
            "~28GB download / ~5GB cache", "~10GB"),
    ];

    private static AudioModelDefinition Make(string id, string name, string quant, string description, string size, string vram)
    {
        Dictionary<string, object> cfg = new() { ["model_name"] = Repo };
        if (quant is not null) { cfg["quant"] = quant; }
        return new AudioModelDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            SourceUrl = $"https://huggingface.co/{Repo}",
            License = "CC-BY-NC-4.0",
            EstimatedSize = size,
            EstimatedVram = vram,
            EngineConfig = cfg,
        };
    }

    #endregion
}
