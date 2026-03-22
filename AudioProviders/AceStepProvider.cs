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
        .WithPythonEngine("music_acestep", "AceStepEngine")
        .WithModelPrefix("AceStep")
        .WithModelClass("acestep_music", "ACE-Step Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("acestep_music_params")
        .AddFeatureFlag("acestep_lm_params")
        .AddFeatureFlag("acestep_task_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.7.1+cu128", InstallName = "torch==2.7.1+cu128", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "torchaudio==2.7.1+cu128", InstallName = "torchaudio==2.7.1+cu128", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "torchvision==0.22.1+cu128", InstallName = "torchvision==0.22.1+cu128", ImportName = "torchvision", Category = "pytorch", EstimatedInstallTimeMinutes = 8, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "ace-step", InstallName = "git+https://github.com/ace-step/ACE-Step.git", ImportName = "acestep", Category = "music", IsGitPackage = true, EstimatedInstallTimeMinutes = 15, CustomInstallArgs = "--no-deps" },
        new() { Name = "transformers>=4.51.0,<4.58.0", InstallName = "transformers>=4.51.0,<4.58.0", ImportName = "transformers", Category = "music" },
        new() { Name = "diffusers", InstallName = "diffusers", ImportName = "diffusers", Category = "music" },
        new() { Name = "einops>=0.8.1", InstallName = "einops>=0.8.1", ImportName = "einops", Category = "music" },
        new() { Name = "accelerate>=1.12.0", InstallName = "accelerate>=1.12.0", ImportName = "accelerate", Category = "music" },
        new() { Name = "peft>=0.18.0", InstallName = "peft>=0.18.0", ImportName = "peft", Category = "music" },
        new() { Name = "numba>=0.63.1", InstallName = "numba>=0.63.1", ImportName = "numba", Category = "music" },
        new() { Name = "torchcodec>=0.9.1", InstallName = "torchcodec>=0.9.1", ImportName = "torchcodec", Category = "music" },
        new() { Name = "vector-quantize-pytorch>=1.27.15", InstallName = "vector-quantize-pytorch>=1.27.15", ImportName = "vector_quantize_pytorch", Category = "music" },
        new() { Name = "safetensors==0.7.0", InstallName = "safetensors==0.7.0", ImportName = "safetensors", Category = "music" },
        new() { Name = "scipy>=1.10.1", InstallName = "scipy>=1.10.1", ImportName = "scipy", Category = "music" },
        new() { Name = "soundfile>=0.13.1", InstallName = "soundfile>=0.13.1", ImportName = "soundfile", Category = "core" },
        new() { Name = "loguru>=0.7.3", InstallName = "loguru>=0.7.3", ImportName = "loguru", Category = "music" },
        new() { Name = "pypinyin", InstallName = "pypinyin", ImportName = "pypinyin", Category = "music" },
        new() { Name = "hangul-romanize", InstallName = "hangul-romanize", ImportName = "hangul_romanize", Category = "music" },
        new() { Name = "num2words", InstallName = "num2words", ImportName = "num2words", Category = "music" },
        new() { Name = "cutlet", InstallName = "cutlet", ImportName = "cutlet", Category = "music" },
        new() { Name = "fugashi", InstallName = "fugashi[unidic-lite]", ImportName = "fugashi", Category = "music" },
        new() { Name = "spacy", InstallName = "spacy", ImportName = "spacy", Category = "music" },
        new() { Name = "librosa", InstallName = "librosa", ImportName = "librosa", Category = "music" },
        new() { Name = "py3langid", InstallName = "py3langid", ImportName = "py3langid", Category = "music" },
        new() { Name = "tqdm", InstallName = "tqdm", ImportName = "tqdm", Category = "core" },
        new() { Name = "opencc-python-reimplemented", InstallName = "opencc-python-reimplemented", ImportName = "opencc", Category = "music" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "turbo", Name = "ACE-Step 1.5 Turbo", Description = "Fast turbo model, 8 steps. Supports text2music, cover, repaint.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo" } },
        new() { Id = "turbo-shift1", Name = "ACE-Step 1.5 Turbo Shift1", Description = "Turbo with shift=1 for enhanced diversity, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift1" } },
        new() { Id = "turbo-shift3", Name = "ACE-Step 1.5 Turbo Shift3", Description = "Turbo with shift=3 for high diversity, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-shift3" } },
        new() { Id = "turbo-continuous", Name = "ACE-Step 1.5 Turbo Continuous", Description = "Turbo with continuous noise schedule, 8 steps.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-turbo-continuous" } },
        new() { Id = "sft", Name = "ACE-Step 1.5 SFT", Description = "SFT model with CFG support, 50 steps. Supports text2music, cover, repaint, extract.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~8GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-sft" } },
        new() { Id = "base", Name = "ACE-Step 1.5 Base", Description = "Full base model with CFG, 50 steps. Supports all 6 task types.", SourceUrl = "https://github.com/ace-step/ACE-Step-1.5", License = "MIT", EstimatedSize = "~4GB", EstimatedVram = "~10GB", EngineConfig = new() { ["dit_model"] = "acestep-v15-base" } }
    ];

    #endregion
}
