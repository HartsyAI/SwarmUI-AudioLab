using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>RealtimeSTT provider — real-time streaming speech-to-text with wake word detection.</summary>
public sealed class RealtimeSTTProvider : IAudioProviderSource
{
    public static RealtimeSTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("realtimestt_stt")
        .WithName("RealtimeSTT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_realtimestt", "RealtimeSTTEngine")
        .WithModelPrefix("RealtimeSTT")
        .WithModelClass("realtimestt_stt", "RealtimeSTT")
        .AddFeatureFlag("realtimestt_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .WithRequiresDocker()
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "scipy==1.14.1", InstallName = "scipy==1.14.1", ImportName = "scipy", Category = "core" },
        new() { Name = "soundfile==0.13.1", InstallName = "soundfile==0.13.1", ImportName = "soundfile", Category = "core" },
        new() { Name = "numpy<2.0.0", InstallName = "numpy<2.0.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "RealtimeSTT", InstallName = "RealtimeSTT", ImportName = "RealtimeSTT", Category = "stt" },
        new() { Name = "PyAudio==0.2.14", InstallName = "PyAudio==0.2.14", ImportName = "pyaudio", Category = "stt" },
        new() { Name = "faster-whisper==1.1.0", InstallName = "faster-whisper==1.1.0", ImportName = "faster_whisper", Category = "stt" },
        new() { Name = "pvporcupine==1.9.5", InstallName = "pvporcupine==1.9.5", ImportName = "pvporcupine", Category = "stt" },
        new() { Name = "webrtcvad-wheels==2.0.14", InstallName = "webrtcvad-wheels==2.0.14", ImportName = "webrtcvad", Category = "stt" },
        new() { Name = "openwakeword>=0.4.0", InstallName = "openwakeword>=0.4.0", ImportName = "openwakeword", Category = "stt" },
        new() { Name = "halo==0.0.31", InstallName = "halo==0.0.31", ImportName = "halo", Category = "stt", EstimatedInstallTimeMinutes = 8 }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "RealtimeSTT Default", Description = "Real-time streaming transcription with wake word detection" }
    ];
}
