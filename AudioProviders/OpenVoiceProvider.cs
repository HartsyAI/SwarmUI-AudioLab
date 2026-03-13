using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenVoice v2 provider — transfers the tone/style of a reference voice onto existing audio. Audio in, audio out (no text generation).</summary>
public sealed class OpenVoiceProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the OpenVoice provider.</summary>
    public static OpenVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the OpenVoice V2 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("openvoice_clone")
        .WithName("OpenVoice V2")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithPythonEngine("clone_openvoice", "OpenVoiceEngine")
        .WithModelPrefix("OpenVoice")
        .WithModelClass("openvoice_clone", "OpenVoice V2")
        .AddFeatureFlag("openvoice_clone_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "openvoice", InstallName = "git+https://github.com/myshell-ai/OpenVoice.git", ImportName = "openvoice", Category = "voice_clone", IsGitPackage = true, EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "OpenVoice V2", Description = "Voice tone transfer: takes existing audio + a reference voice clip, outputs the same speech with the reference voice's tone/style applied. Zero-shot, no model training needed.", SourceUrl = "https://github.com/myshell-ai/OpenVoice", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", EngineConfig = new() { ["model_version"] = "v2" } }
    ];

    #endregion
}
