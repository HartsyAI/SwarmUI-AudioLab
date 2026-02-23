using Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;

namespace Hartsy.Extensions.VoiceAssistant.AudioProviders;

/// <summary>Bark TTS provider — text-to-audio model with speech, music, and sound effects.</summary>
public sealed class BarkProvider : IAudioProviderSource
{
    public static BarkProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("bark_tts")
        .WithName("Bark TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_bark", "BarkEngine")
        .WithModelPrefix("Bark")
        .WithModelClass("bark_tts", "Bark TTS")
        .AddFeatureFlag("bark_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "bark", InstallName = "bark", ImportName = "bark", Category = "tts" },
        new() { Name = "transformers>=4.31.0", InstallName = "transformers>=4.31.0", ImportName = "transformers", Category = "tts" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Bark Default", Description = "General-purpose text-to-audio generation" },
        new() { Id = "multilingual", Name = "Bark Multilingual", Description = "Multilingual speech synthesis with diverse voices" }
    ];
}
