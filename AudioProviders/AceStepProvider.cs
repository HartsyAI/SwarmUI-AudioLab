using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ACE-Step 1.5 provider — SOTA music generation with lyrics alignment, 6 DiT variants, and optional LM planner.</summary>
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
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "turbo", Name = "ACE-Step 1.5 Turbo", Description = "Fast turbo model, 8 steps. Supports text2music, cover, repaint.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo" } },
        new() { Id = "turbo-shift1", Name = "ACE-Step 1.5 Turbo Shift1", Description = "Turbo with shift=1 for enhanced diversity, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift1" } },
        new() { Id = "turbo-shift3", Name = "ACE-Step 1.5 Turbo Shift3", Description = "Turbo with shift=3 for high diversity, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift3" } },
        new() { Id = "turbo-continuous", Name = "ACE-Step 1.5 Turbo Continuous", Description = "Turbo with continuous noise schedule, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-continuous" } },
        new() { Id = "sft", Name = "ACE-Step 1.5 SFT", Description = "SFT model with CFG support, 50 steps. Supports text2music, cover, repaint, extract.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-sft" } },
        new() { Id = "base", Name = "ACE-Step 1.5 Base", Description = "Full base model with CFG, 50 steps. Supports all 6 task types.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "Apache 2.0", EstimatedSize = "~4GB", EstimatedVram = "~10GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-base" } }
    ];

    #endregion
}
