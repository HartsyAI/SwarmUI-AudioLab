using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>YuE2 provider — lyrics- and style-conditioned full songs at 48 kHz stereo, up to six minutes.</summary>
/// <remarks>Shares only its name with <see cref="YuEProvider">YuE v1</see>: v1 is a 7B LLaMA stage-1 plus a 1B
/// stage-2 over an xcodec RVQ, v2 is an autoregressive score planner feeding a flow-matched acoustic stack and an
/// Oobleck VAE. Different weights, tokenizer, codec and sample rate, so it gets its own provider rather than a
/// variant row under v1. Weights are CC BY-NC 4.0 — non-commercial use only, unlike v1's Apache-2.0.</remarks>
public sealed class YuE2Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the YuE2 provider.</summary>
    public static YuE2Provider Instance { get; } = new();

    /// <summary>Builds and returns the YuE2 music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("yue2_music")
        .WithName("YuE2 Music")
        .WithCategory(AudioCategory.AudioGeneration)
        // The trailing slash in the selector prefix is what keeps these rows out of v1's "YuE" namespace.
        .WithModelPrefix("YuE2")
        .WithModelClass("yue2_music", "YuE2 Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("yue2_music_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "bf16",
            Name = "YuE2 3B (BF16)",
            Description = "3B autoregressive score planner plus a flow-matched acoustic stack. Writes an editable "
                + "ABC score from your style tags and lyrics, then renders it to 48 kHz stereo. One file: the "
                + "Comfy-Org repack carries both stacks, the VAE and the tokenizer, so there are no side assets.",
            SourceUrl = "https://huggingface.co/Comfy-Org/YuE2",
            License = "CC-BY-NC-4.0",
            EstimatedSize = "~7.8GB",
            EstimatedVram = "~9GB (bf16)",
            EngineConfig = new() { ["model_name"] = "Comfy-Org/YuE2" }
        },
    ];

    #endregion
}
