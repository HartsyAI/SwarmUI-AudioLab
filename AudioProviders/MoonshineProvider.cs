using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Moonshine STT provider — ultra-fast speech recognition, 5x faster than Whisper.</summary>
public sealed class MoonshineProvider : IAudioProviderSource
{
    public static MoonshineProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("moonshine_stt")
        .WithName("Moonshine STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_moonshine", "MoonshineEngine")
        .WithModelPrefix("Moonshine")
        .WithModelClass("moonshine_stt", "Moonshine STT")
        .AddFeatureFlag("moonshine_stt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "moonshine", InstallName = "moonshine", ImportName = "moonshine", Category = "stt", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "base", Name = "Moonshine Base", Description = "Fast transcription, good accuracy (~1GB VRAM or CPU)", EngineConfig = new() { ["model_name"] = "moonshine/base" } },
        new() { Id = "tiny", Name = "Moonshine Tiny", Description = "Fastest transcription, lighter accuracy (CPU-capable)", EngineConfig = new() { ["model_name"] = "moonshine/tiny" } }
    ];
}
