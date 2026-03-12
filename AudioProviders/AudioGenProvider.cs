using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Meta AudioGen provider — text-to-sound-effects generation (AudioCraft family).</summary>
public sealed class AudioGenProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the AudioGen provider.</summary>
    public static AudioGenProvider Instance { get; } = new();

    /// <summary>Builds and returns the AudioGen SFX provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("audiogen_sfx")
        .WithName("AudioGen SFX")
        .WithCategory(AudioCategory.SoundFX)
        .WithPythonEngine("sfx_audiogen", "AudioGenEngine")
        .WithModelPrefix("AudioGen")
        .WithModelClass("audiogen_sfx", "AudioGen SFX")
        .AddFeatureFlag("audiogen_sfx_params")
        .AddFeatureFlag("audiocraft_sampling")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // audiocraft installed with --no-deps to skip spacy (training-only dep, incompatible with Python 3.13)
        new() { Name = "audiocraft", InstallName = "audiocraft", ImportName = "audiocraft", Category = "sound_fx", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--no-deps" },
        // Explicit audiocraft runtime dependencies (inference only, no spacy/thinc/blis needed)
        new() { Name = "encodec", InstallName = "encodec", ImportName = "encodec", Category = "sound_fx" },
        new() { Name = "einops", InstallName = "einops", ImportName = "einops", Category = "sound_fx" },
        new() { Name = "flashy>=0.0.1", InstallName = "flashy>=0.0.1", ImportName = "flashy", Category = "sound_fx" },
        new() { Name = "hydra-core>=1.1", InstallName = "hydra-core>=1.1", ImportName = "hydra", Category = "sound_fx" },
        new() { Name = "hydra_colorlog", InstallName = "hydra_colorlog", ImportName = "hydra_colorlog", Category = "sound_fx" },
        new() { Name = "julius", InstallName = "julius", ImportName = "julius", Category = "sound_fx" },
        new() { Name = "sentencepiece", InstallName = "sentencepiece", ImportName = "sentencepiece", Category = "sound_fx" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "sound_fx" },
        new() { Name = "transformers", InstallName = "transformers", ImportName = "transformers", Category = "sound_fx" },
        new() { Name = "num2words", InstallName = "num2words", ImportName = "num2words", Category = "sound_fx" },
        new() { Name = "av", InstallName = "av", ImportName = "av", Category = "sound_fx" },
        new() { Name = "lameenc", InstallName = "lameenc", ImportName = "lameenc", Category = "sound_fx" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "medium", Name = "AudioGen Medium", Description = "Text-to-sound-effects, 1.5B params", SourceUrl = "https://huggingface.co/facebook/audiogen-medium", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "facebook/audiogen-medium" } }
    ];

    #endregion
}
