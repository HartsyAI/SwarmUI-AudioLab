using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Bark TTS provider — text-to-audio model with speech, music, and sound effects.</summary>
public sealed class BarkProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the Bark TTS provider.</summary>
    public static BarkProvider Instance { get; } = new();

    /// <summary>Builds and returns the Bark TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("bark_tts")
        .WithName("Bark TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_bark", "BarkEngine")
        .WithModelPrefix("Bark")
        .WithModelClass("bark_tts", "Bark TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("bark_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "bark", InstallName = "bark", ImportName = "bark", Category = "tts" },
        new() { Name = "transformers>=4.31.0", InstallName = "transformers>=4.31.0", ImportName = "transformers", Category = "tts" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Bark TTS", Description = "Text-to-audio generation with speech, music, and sound effects", SourceUrl = "https://huggingface.co/suno/bark", License = "MIT", EstimatedSize = "~5GB", EstimatedVram = "~5GB" }
    ];

    #endregion
}
