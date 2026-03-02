using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Microsoft VibeVoice TTS provider — long-form multi-speaker synthesis (up to 90 min, 4 speakers).</summary>
public sealed class VibeVoiceProvider : IAudioProviderSource
{
    public static VibeVoiceProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("vibevoice_tts")
        .WithName("VibeVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_vibevoice", "VibeVoiceEngine")
        .WithModelPrefix("VibeVoice")
        .WithModelClass("vibevoice_tts", "VibeVoice TTS")
        .AddFeatureFlag("vibevoice_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_cfg")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "vibevoice", InstallName = "git+https://github.com/microsoft/VibeVoice.git", ImportName = "vibevoice", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 15 }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "realtime-0.5b", Name = "VibeVoice Realtime 0.5B", Description = "Real-time streaming TTS, single speaker, low latency (~3GB VRAM)", EngineConfig = new() { ["model_name"] = "microsoft/VibeVoice-Realtime-0.5B" } },
        new() { Id = "1.5b", Name = "VibeVoice 1.5B", Description = "Long-form multi-speaker TTS, up to 90 min, 4 speakers (~7GB VRAM)", EngineConfig = new() { ["model_name"] = "microsoft/VibeVoice-1.5B" } },
        new() { Id = "large", Name = "VibeVoice Large 7B", Description = "Highest quality TTS, best non-English stability, 4 speakers (~16GB VRAM)", EngineConfig = new() { ["model_name"] = "microsoft/VibeVoice-7B-hf" } }
    ];
}
