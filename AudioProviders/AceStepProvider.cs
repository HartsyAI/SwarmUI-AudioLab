using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ACE-Step 1.5 provider — SOTA music generation with lyrics alignment, 2B + XL DiT variants, and optional LM planner.</summary>
public sealed class AceStepProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the ACE-Step provider.</summary>
    public static AceStepProvider Instance { get; } = new();

    /// <summary>Builds and returns the ACE-Step music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("acestep_music")
        .WithName("ACE-Step Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("AceStep")
        .WithModelClass("acestep_music", "ACE-Step Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("acestep_music_params")
        .AddFeatureFlag("acestep_lm_params")
        .AddFeatureFlag("acestep_task_params")
        .AddFeatureFlag("music_instrumental_param")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private const string RepoUrl = "https://github.com/ace-step/ACE-Step-1.5";

    // Every variant is a DISTINCT checkpoint (per-repo sha256s in AudioWeightsRegistry) — sizes are the
    // real file sizes (2B = 4.79GB single file; XL = 4 shards, ~20GB). VRAM notes assume bf16 + the
    // engine's streaming weight cache (larger-than-VRAM models run, slower).
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "turbo", Name = "ACE-Step 1.5 Turbo", Description = "Fast turbo model (8 steps, no CFG). Text2music, cover, repaint.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB",
            ModelClassId = "acestep_music_turbo", ModelClassName = "ACE-Step Music Turbo" },
        new() { Id = "turbo-shift1", Name = "ACE-Step 1.5 Turbo Shift1", Description = "Turbo trained for shift=1 (enhanced diversity), 8 steps.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB",
            ModelClassId = "acestep_music_turbo", ModelClassName = "ACE-Step Music Turbo" },
        new() { Id = "turbo-shift3", Name = "ACE-Step 1.5 Turbo Shift3", Description = "Turbo trained for shift=3 (high quality), 8 steps.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB",
            ModelClassId = "acestep_music_turbo", ModelClassName = "ACE-Step Music Turbo" },
        new() { Id = "turbo-continuous", Name = "ACE-Step 1.5 Turbo Continuous", Description = "Turbo trained on the continuous noise schedule, 8 steps.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB",
            ModelClassId = "acestep_music_turbo", ModelClassName = "ACE-Step Music Turbo" },
        new() { Id = "sft", Name = "ACE-Step 1.5 SFT", Description = "SFT model with CFG (guidance ~7, 50 steps). Highest 2B quality; slower.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB" },
        new() { Id = "base", Name = "ACE-Step 1.5 Base", Description = "Pre-train base model with CFG (guidance ~7, 32 steps). Highest diversity.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "4.8GB", EstimatedVram = "~6GB" },
        new() { Id = "xl-turbo", Name = "ACE-Step 1.5 XL Turbo", Description = "4B XL turbo (8 steps, no CFG). Highest turbo quality; ~10GB download, streams on <12GB VRAM (slower).", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "10GB", EstimatedVram = "~12GB",
            ModelClassId = "acestep_music_turbo", ModelClassName = "ACE-Step Music Turbo" },
        new() { Id = "xl-sft", Name = "ACE-Step 1.5 XL SFT", Description = "4B XL SFT with CFG (guidance ~7, 50 steps). Best quality overall; very slow on <16GB VRAM.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "10GB", EstimatedVram = "~12GB" },
        new() { Id = "xl-base", Name = "ACE-Step 1.5 XL Base", Description = "4B XL pre-train base with CFG (guidance ~7, 32 steps). Highest diversity; very slow on <16GB VRAM.", SourceUrl = RepoUrl, License = "Apache 2.0", EstimatedSize = "10GB", EstimatedVram = "~12GB" },
    ];

    #endregion
}
