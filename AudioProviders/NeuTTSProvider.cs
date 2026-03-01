using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>NeuTTS Air provider — on-device TTS with instant voice cloning by Neuphonic.</summary>
public sealed class NeuTTSProvider : IAudioProviderSource
{
    public static NeuTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("neutts_tts")
        .WithName("NeuTTS Air")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_neutts", "NeuTTSEngine")
        .WithModelPrefix("NeuTTS")
        .WithModelClass("neutts_tts", "NeuTTS Air")
        .AddFeatureFlag("neutts_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("transformers")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "neutts", InstallName = "neutts", ImportName = "neutts", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "air", Name = "NeuTTS Air", Description = "On-device TTS with instant voice cloning, 0.5B params (low VRAM / CPU)", EngineConfig = new() { ["model_name"] = "neuphonic/neutts-air" } }
    ];
}
