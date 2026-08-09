using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Moonshine STT provider — ultra-fast speech recognition, 5x faster than Whisper.</summary>
public sealed class MoonshineProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Moonshine provider.</summary>
    public static MoonshineProvider Instance { get; } = new();

    /// <summary>Builds and returns the Moonshine STT provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("moonshine_stt")
        .WithName("Moonshine STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("Moonshine")
        .WithModelClass("moonshine_stt", "Moonshine STT")
        .AddFeatureFlag("audiolab_stt")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "base", Name = "Moonshine Base", Description = "Fast transcription, good accuracy", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-base", License = "MIT", EstimatedSize = "~400MB", EstimatedVram = "~1GB (or CPU)", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshine/base" } },
        new() { Id = "tiny", Name = "Moonshine Tiny", Description = "Fastest transcription, lighter accuracy, CPU-capable", SourceUrl = "https://huggingface.co/UsefulSensors/moonshine-tiny", License = "MIT", EstimatedSize = "~200MB", EstimatedVram = "CPU only", SelfManaged = true, EngineConfig = new() { ["model_name"] = "moonshine/tiny" } }
    ];

    #endregion
}
