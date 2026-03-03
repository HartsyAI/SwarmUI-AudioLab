using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Dia TTS provider — ultra-realistic dialogue generation with nonverbal sounds.</summary>
public sealed class DiaTTSProvider : IAudioProviderSource
{
    public static DiaTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("dia_tts")
        .WithName("Dia TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_dia", "DiaEngine")
        .WithModelPrefix("Dia")
        .WithModelClass("dia_tts", "Dia TTS")
        .AddFeatureFlag("dia_tts_params")
        .AddFeatureFlag("tts_sampling")
        .AddFeatureFlag("tts_cfg")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "dia", InstallName = "git+https://github.com/nari-labs/dia.git", ImportName = "dia", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 10 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.6b", Name = "Dia 1.6B", Description = "Ultra-realistic dialogue, 2 speakers in one pass, nonverbal sounds", SourceUrl = "https://huggingface.co/nari-labs/Dia-1.6B-0626", License = "Apache 2.0", EstimatedSize = "~3.5GB", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "nari-labs/Dia-1.6B-0626" } }
    ];
}
