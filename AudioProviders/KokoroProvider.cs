using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Kokoro TTS provider — ultra-fast lightweight TTS (82M params, CPU-capable).</summary>
public sealed class KokoroProvider : IAudioProviderSource
{
    public static KokoroProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("kokoro_tts")
        .WithName("Kokoro TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_kokoro", "KokoroEngine")
        .WithModelPrefix("Kokoro")
        .WithModelClass("kokoro_tts", "Kokoro TTS")
        .AddFeatureFlag("kokoro_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("core")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "kokoro>=0.3.0", InstallName = "kokoro>=0.3.0", ImportName = "kokoro", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Kokoro Default", Description = "82M param model, 96x real-time on GPU, CPU-capable (~1GB VRAM or CPU)" }
    ];
}
