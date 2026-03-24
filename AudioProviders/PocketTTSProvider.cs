using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Pocket TTS provider — Kyutai's 100M parameter CPU-capable TTS with built-in voices and voice cloning.</summary>
public sealed class PocketTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Pocket TTS provider.</summary>
    public static PocketTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Pocket TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("pockettts_tts")
        .WithName("Pocket TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_pockettts", "PocketTTSEngine")
        .WithModelPrefix("PocketTTS")
        .WithModelClass("pockettts_tts", "Pocket TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("pockettts_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        new() { Name = "pocket-tts>=1.1.1", InstallName = "pocket-tts>=1.1.1", ImportName = "pocket_tts", Category = "tts", EstimatedInstallTimeMinutes = 3 }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "default",
            Name = "Pocket TTS",
            Description = "100M parameter TTS. 8 built-in voices, voice cloning from audio files. ~6x real-time on CPU, ~200ms to first chunk.",
            SourceUrl = "https://github.com/kyutai-labs/pocket-tts",
            License = "MIT",
            EstimatedSize = "~200MB",
            EstimatedVram = "CPU (no GPU needed)"
        }
    ];

    #endregion
}
