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
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "audiocraft", InstallName = "audiocraft", ImportName = "audiocraft", Category = "music", EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "small", Name = "MusicGen Small", Description = "300M params, fast generation (~4GB VRAM)", EngineConfig = new() { ["model_name"] = "facebook/musicgen-small" } },
        new() { Id = "medium", Name = "MusicGen Medium", Description = "1.5B params, better quality (~6GB VRAM)", EngineConfig = new() { ["model_name"] = "facebook/musicgen-medium" } },
        new() { Id = "large", Name = "MusicGen Large", Description = "3.3B params, best quality (~10GB VRAM)", EngineConfig = new() { ["model_name"] = "facebook/musicgen-large" } },
        new() { Id = "melody", Name = "MusicGen Melody", Description = "1.5B params with melody conditioning input (~6GB VRAM)", EngineConfig = new() { ["model_name"] = "facebook/musicgen-melody" } }
    ];
}
