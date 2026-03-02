using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Orpheus TTS provider — LLM-based emotional speech synthesis with emotion tags.</summary>
public sealed class OrpheusTTSProvider : IAudioProviderSource
{
    public static OrpheusTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("orpheus_tts")
        .WithName("Orpheus TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_orpheus", "OrpheusEngine")
        .WithModelPrefix("Orpheus")
        .WithModelClass("orpheus_tts", "Orpheus TTS")
        .AddFeatureFlag("orpheus_tts_params")
        .AddFeatureFlag("tts_sampling")
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
        new() { Name = "snac", InstallName = "snac", ImportName = "snac", Category = "tts", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" }
    ];

    // Only the 3B model has been released by Canopy Labs. Smaller variants (1B, 400M, 150M) are
    // on their roadmap but not yet available on HuggingFace. Add them back when released.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "3b", Name = "Orpheus 3B", Description = "Expressive speech with emotion tags: <laugh>, <sigh>, <gasp>, etc. (~16GB VRAM)", EngineConfig = new() { ["model_name"] = "canopylabs/orpheus-3b-0.1-ft", ["model_size"] = "3b" } }
    ];
}
