using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Meta AudioGen provider — text-to-sound-effects generation (AudioCraft family).</summary>
public sealed class AudioGenProvider : IAudioProviderSource
{
    public static AudioGenProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("audiogen_sfx")
        .WithName("AudioGen SFX")
        .WithCategory(AudioCategory.SoundFX)
        .WithPythonEngine("sfx_audiogen", "AudioGenEngine")
        .WithModelPrefix("AudioGen")
        .WithModelClass("audiogen_sfx", "AudioGen SFX")
        .AddFeatureFlag("audiogen_sfx_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("audiocraft")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "audiocraft", InstallName = "audiocraft", ImportName = "audiocraft", Category = "sound_fx", EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "medium", Name = "AudioGen Medium", Description = "Text-to-sound-effects, 1.5B params (~4GB VRAM)", EngineConfig = new() { ["model_name"] = "facebook/audiogen-medium" } }
    ];
}
