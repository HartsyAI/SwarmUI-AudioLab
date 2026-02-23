using Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;

namespace Hartsy.Extensions.VoiceAssistant.AudioProviders;

/// <summary>Chatterbox TTS provider — high-quality voice synthesis with expressive controls.</summary>
public sealed class ChatterboxProvider : IAudioProviderSource
{
    public static ChatterboxProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("chatterbox_tts")
        .WithName("Chatterbox TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_chatterbox", "ChatterboxEngine")
        .WithModelPrefix("Chatterbox")
        .WithModelClass("chatterbox_tts", "Chatterbox TTS")
        .AddFeatureFlag("chatterbox_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "librosa==0.11.0", InstallName = "librosa==0.11.0", ImportName = "librosa", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchvision==0.21.0+cu126", InstallName = "torchvision==0.21.0+cu126", ImportName = "torchvision", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "chatterbox-tts", InstallName = "git+https://github.com/JarodMica/chatterbox.git", ImportName = "chatterbox", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 15, AlternativeNames = ["chatterbox_tts", "resemble", "resemblevoice", "chatterbox", "chatterbox.preprocessing", "chatterbox.models"] },
        new() { Name = "s3tokenizer", InstallName = "s3tokenizer", ImportName = "s3tokenizer", Category = "tts" },
        new() { Name = "transformers==4.46.3", InstallName = "transformers==4.46.3", ImportName = "transformers", Category = "tts" },
        new() { Name = "diffusers==0.29.0", InstallName = "diffusers==0.29.0", ImportName = "diffusers", Category = "tts" },
        new() { Name = "resemble-perth==1.0.1", InstallName = "resemble-perth==1.0.1", ImportName = "resemble_perth", Category = "tts" },
        new() { Name = "conformer==0.3.2", InstallName = "conformer==0.3.2", ImportName = "conformer", Category = "tts" },
        new() { Name = "safetensors==0.5.3", InstallName = "safetensors==0.5.3", ImportName = "safetensors", Category = "tts" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Chatterbox Default", Description = "Balanced voice with moderate expression", EngineConfig = new() { ["exaggeration"] = 0.5, ["cfg_weight"] = 0.5 } },
        new() { Id = "expressive", Name = "Chatterbox Expressive", Description = "High expressiveness, more animated speech", EngineConfig = new() { ["exaggeration"] = 0.7, ["cfg_weight"] = 0.3 } },
        new() { Id = "calm", Name = "Chatterbox Calm", Description = "Calm and measured delivery", EngineConfig = new() { ["exaggeration"] = 0.3, ["cfg_weight"] = 0.7 } },
        new() { Id = "dramatic", Name = "Chatterbox Dramatic", Description = "Highly expressive, dramatic delivery", EngineConfig = new() { ["exaggeration"] = 0.8, ["cfg_weight"] = 0.2 } }
    ];
}
