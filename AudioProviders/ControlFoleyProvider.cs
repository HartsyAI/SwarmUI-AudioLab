using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>ControlFoley provider -- sound effects at 44.1 kHz from a flow-matching DiT, from text alone, scored to a video, or following a reference clip.</summary>
public sealed class ControlFoleyProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the ControlFoley provider.</summary>
    public static ControlFoleyProvider Instance { get; } = new();

    /// <summary>Builds and returns the ControlFoley provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("controlfoley_sfx")
        .WithName("ControlFoley")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("ControlFoley")
        .WithModelClass("controlfoley_sfx", "ControlFoley")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("controlfoley_sfx_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "large-44k", Name = "ControlFoley", Description = "2.8B flow-matching DiT, DFN5B CLIP text conditioning, VAE + BigVGAN v2 decoder. Text-to-audio, video-to-audio and audio-conditioned generation up to ~8s at 44.1 kHz.", SourceUrl = "https://huggingface.co/YJX-Xiaomi/ControlFoley", License = "CC-BY-NC-4.0", EstimatedSize = "~17GB", EstimatedVram = "~12GB" },
    ];

    #endregion
}
