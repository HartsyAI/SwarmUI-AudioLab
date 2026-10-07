using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Breeze TTS 2 provider -- T5Gemma2 text encoder over a Qwen3 backbone with a depth decoder, voice cloning from a reference clip and voice design from a written instruction.</summary>
public sealed class BreezeProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Breeze provider.</summary>
    public static BreezeProvider Instance { get; } = new();

    /// <summary>Builds and returns the Breeze TTS 2 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("breeze_tts")
        .WithName("Breeze TTS 2")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("Breeze")
        .WithModelClass("breeze_tts", "Breeze TTS 2")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("breeze_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "breeze-tts-2",
            Name = "Breeze TTS 2",
            Description = "Qwen3 backbone with a CSM-style depth decoder over a 12.5 Hz codec at 24 kHz. Voice design from an instruction, or cloning from a reference clip.",
            SourceUrl = "https://huggingface.co/BreezeBlue/Breeze-TTS-2",
            License = "Research / non-commercial",
            EstimatedSize = "~7GB",
            EstimatedVram = "~10GB",
            EngineConfig = new() { ["model_name"] = "BreezeBlue/Breeze-TTS-2" }
        }
    ];

    #endregion
}
