using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Meta MusicGen provider — text-to-music with optional melody conditioning (AudioCraft).</summary>
public sealed class MusicGenProvider : IAudioProviderSource
{
    public static MusicGenProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("musicgen_music")
        .WithName("MusicGen")
        .WithCategory(AudioCategory.MusicGen)
        .WithPythonEngine("music_musicgen", "MusicGenEngine")
        .WithModelPrefix("MusicGen")
        .WithModelClass("musicgen_music", "MusicGen")
        .AddFeatureFlag("musicgen_music_params")
        .AddFeatureFlag("audiocraft_sampling")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // audiocraft installed with --no-deps to skip spacy (training-only dep, incompatible with Python 3.13)
        new() { Name = "audiocraft", InstallName = "audiocraft", ImportName = "audiocraft", Category = "music", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--no-deps" },
        // Explicit audiocraft runtime dependencies (inference only, no spacy/thinc/blis needed)
        new() { Name = "encodec", InstallName = "encodec", ImportName = "encodec", Category = "music" },
        new() { Name = "einops", InstallName = "einops", ImportName = "einops", Category = "music" },
        new() { Name = "flashy>=0.0.1", InstallName = "flashy>=0.0.1", ImportName = "flashy", Category = "music" },
        new() { Name = "hydra-core>=1.1", InstallName = "hydra-core>=1.1", ImportName = "hydra", Category = "music" },
        new() { Name = "hydra_colorlog", InstallName = "hydra_colorlog", ImportName = "hydra_colorlog", Category = "music" },
        new() { Name = "julius", InstallName = "julius", ImportName = "julius", Category = "music" },
        new() { Name = "sentencepiece", InstallName = "sentencepiece", ImportName = "sentencepiece", Category = "music" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "music" },
        new() { Name = "transformers", InstallName = "transformers", ImportName = "transformers", Category = "music" },
        new() { Name = "num2words", InstallName = "num2words", ImportName = "num2words", Category = "music" },
        new() { Name = "av", InstallName = "av", ImportName = "av", Category = "music" },
        new() { Name = "lameenc", InstallName = "lameenc", ImportName = "lameenc", Category = "music" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "small", Name = "MusicGen Small", Description = "300M params, fast mono generation", SourceUrl = "https://huggingface.co/facebook/musicgen-small", License = "CC-BY-NC-4.0", EstimatedSize = "~1.2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-small" } },
        new() { Id = "medium", Name = "MusicGen Medium", Description = "1.5B params, better mono quality", SourceUrl = "https://huggingface.co/facebook/musicgen-medium", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~6GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-medium" } },
        new() { Id = "large", Name = "MusicGen Large", Description = "3.3B params, best mono quality", SourceUrl = "https://huggingface.co/facebook/musicgen-large", License = "CC-BY-NC-4.0", EstimatedSize = "~7GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-large" } },
        new() { Id = "melody", Name = "MusicGen Melody", Description = "1.5B params with melody conditioning input", SourceUrl = "https://huggingface.co/facebook/musicgen-melody", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~6GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-melody" } },
        new() { Id = "melody-large", Name = "MusicGen Melody Large", Description = "3.3B params, best melody conditioning quality", SourceUrl = "https://huggingface.co/facebook/musicgen-melody-large", License = "CC-BY-NC-4.0", EstimatedSize = "~7GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-melody-large" } },
        new() { Id = "stereo-small", Name = "MusicGen Stereo Small", Description = "300M params, fast stereo generation", SourceUrl = "https://huggingface.co/facebook/musicgen-stereo-small", License = "CC-BY-NC-4.0", EstimatedSize = "~1.2GB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-stereo-small" } },
        new() { Id = "stereo-medium", Name = "MusicGen Stereo Medium", Description = "1.5B params, stereo output", SourceUrl = "https://huggingface.co/facebook/musicgen-stereo-medium", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~6GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-stereo-medium" } },
        new() { Id = "stereo-large", Name = "MusicGen Stereo Large", Description = "3.3B params, best stereo quality", SourceUrl = "https://huggingface.co/facebook/musicgen-stereo-large", License = "CC-BY-NC-4.0", EstimatedSize = "~7GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-stereo-large" } },
        new() { Id = "stereo-melody", Name = "MusicGen Stereo Melody", Description = "1.5B params, stereo + melody conditioning", SourceUrl = "https://huggingface.co/facebook/musicgen-stereo-melody", License = "CC-BY-NC-4.0", EstimatedSize = "~3.3GB", EstimatedVram = "~6GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-stereo-melody" } },
        new() { Id = "stereo-melody-large", Name = "MusicGen Stereo Melody Large", Description = "3.3B params, best stereo + melody quality", SourceUrl = "https://huggingface.co/facebook/musicgen-stereo-melody-large", License = "CC-BY-NC-4.0", EstimatedSize = "~7GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "facebook/musicgen-stereo-melody-large" } }
    ];
}
