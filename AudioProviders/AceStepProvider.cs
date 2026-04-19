using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ACE-Step 1.5 provider — SOTA music generation via acestep.cpp native binary with GGUF quantized models.
/// Supports 2B standard and 4B XL DiT variants, optional LM planner, and cover/repaint/lego tasks.</summary>
public sealed class AceStepProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the ACE-Step provider.</summary>
    public static AceStepProvider Instance { get; } = new();

    /// <summary>Builds and returns the ACE-Step music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("acestep_music")
        .WithName("ACE-Step Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithNativeBinary()
        .WithModelPrefix("AceStep")
        .WithModelClass("acestep_music", "ACE-Step Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("acestep_music_params")
        .AddFeatureFlag("acestep_lm_params")
        .AddFeatureFlag("acestep_task_params")
        .AddFeatureFlag("text2audio")
        .AddModels(Models)
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        // 2B Standard DiT variants
        new() { Id = "turbo", Name = "ACE-Step 1.5 Turbo", Description = "Fast turbo model, 8 steps. Best for quick generation.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo" } },
        new() { Id = "turbo-shift1", Name = "ACE-Step 1.5 Turbo Shift1", Description = "Turbo with shift=1 for enhanced diversity, 8 steps.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift1" } },
        new() { Id = "turbo-shift3", Name = "ACE-Step 1.5 Turbo Shift3", Description = "Turbo with shift=3 for high diversity, 8 steps.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift3" } },
        new() { Id = "turbo-continuous", Name = "ACE-Step 1.5 Turbo Continuous", Description = "Turbo with continuous noise schedule, 8 steps.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-continuous" } },
        new() { Id = "sft", Name = "ACE-Step 1.5 SFT", Description = "SFT model with CFG support, 50 steps. Higher quality.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-sft" } },
        new() { Id = "base", Name = "ACE-Step 1.5 Base", Description = "Full base model with CFG, 50 steps. Supports all task types.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~2.5GB (Q8_0)", EstimatedVram = "~6GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-base" } },
        // 4B XL DiT variants
        new() { Id = "xl-turbo", Name = "ACE-Step 1.5 XL Turbo", Description = "4B XL turbo model, best quality + speed. Requires ~12GB VRAM.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~5.3GB (Q8_0)", EstimatedVram = "~12GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-xl-turbo" } },
        new() { Id = "xl-sft", Name = "ACE-Step 1.5 XL SFT", Description = "4B XL SFT model, highest quality. Requires ~12GB VRAM.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~5.3GB (Q8_0)", EstimatedVram = "~12GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-xl-sft" } },
        new() { Id = "xl-base", Name = "ACE-Step 1.5 XL Base", Description = "4B XL base model, highest quality + all tasks. Requires ~12GB VRAM.", SourceUrl = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF", License = "MIT", EstimatedSize = "~5.3GB (Q8_0)", EstimatedVram = "~12GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-xl-base" } }
    ];

    #endregion
}
