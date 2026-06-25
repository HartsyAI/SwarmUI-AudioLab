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

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "3b-hny",
            Name = "HeartMuLa 3B (Happy New Year)",
            Description = "4B params, latest and best HeartMuLa model. Generates full songs with vocals from lyrics and style tags. Best lyrics controllability and music quality. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-oss-3B-happy-new-year",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-oss-3B-happy-new-year" }
        },
        new()
        {
            Id = "3b-base",
            Name = "HeartMuLa 3B (Base)",
            Description = "4B params, original HeartMuLa release. Solid music generation quality. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-oss-3B",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-oss-3B" }
        },
        new()
        {
            Id = "3b-rl",
            Name = "HeartMuLa 3B (RL-Tuned)",
            Description = "4B params, reinforcement learning optimized variant. Improved output quality via DPO training. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-RL-oss-3B-20260123",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-RL-oss-3B-20260123" }
        },
    ];

    #endregion
}
