using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>VibeVoice TTS provider — long-form multi-speaker synthesis (up to 90 min, 4 speakers).
/// Community-maintained fork after Microsoft removed the original repo.</summary>
public sealed class VibeVoiceProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the VibeVoice TTS provider.</summary>
    public static VibeVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the VibeVoice TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("vibevoice_tts")
        .WithName("VibeVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_vibevoice", "VibeVoiceEngine")
        .WithModelPrefix("VibeVoice")
        .WithModelClass("vibevoice_tts", "VibeVoice TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("vibevoice_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "transformers>=4.51.3", InstallName = "transformers>=4.51.3", ImportName = "transformers", Category = "tts" },
        new() { Name = "accelerate>=1.6.0", InstallName = "accelerate>=1.6.0", ImportName = "accelerate", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "vibevoice", InstallName = "git+https://github.com/vibevoice-community/VibeVoice.git", ImportName = "vibevoice", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 15 }
    ];

    #endregion

    #region Models

    // The in-process C# engine loads VibeVoice-1.5B (VibeVoicePipeline.LoadAsync is fixed to that repo). The
    // 0.5B-realtime and 7B variants are not loadable yet — advertise only 1.5B to avoid silently loading 1.5B.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.5b", Name = "VibeVoice 1.5B", Description = "Long-form multi-speaker TTS, up to 90 min, 4 speakers", SourceUrl = "https://huggingface.co/vibevoice/VibeVoice-1.5B", License = "MIT", EstimatedSize = "~5GB", EstimatedVram = "~7GB", EngineConfig = new() { ["model_name"] = "vibevoice/VibeVoice-1.5B" } }
    ];

    #endregion
}
