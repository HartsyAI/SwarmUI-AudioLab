using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Sesame CSM provider -- conversational speech generation model (Llama backbone).</summary>
public sealed class CSMProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the CSM provider.</summary>
    public static CSMProvider Instance { get; } = new();

    /// <summary>Builds and returns the CSM provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("csm_tts")
        .WithName("CSM Conversational")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_csm", "CSMEngine")
        .WithModelPrefix("CSM")
        .WithModelClass("csm_tts", "CSM Conversational")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("csm_tts_params")
        .AddFeatureFlag("tts_sampling")
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
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "huggingface_hub>=0.20.0", InstallName = "huggingface_hub>=0.20.0", ImportName = "huggingface_hub", Category = "tts" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1b", Name = "CSM 1B", Description = "Conversational speech, multi-turn dialogue", SourceUrl = "https://huggingface.co/sesame/csm-1b", License = "CC-BY-NC-4.0", EstimatedSize = "~2GB", EstimatedVram = "~4.5GB", EngineConfig = new() { ["model_name"] = "sesame/csm-1b" } }
    ];

    #endregion
}
