using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>GPT-SoVITS provider — generates new speech from text in a cloned voice (TTS with voice cloning). Requires ~1 min reference audio. Strong multilingual/CJK support.</summary>
public sealed class GPTSoVITSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the GPT-SoVITS provider.</summary>
    public static GPTSoVITSProvider Instance { get; } = new();

    /// <summary>Builds and returns the GPT-SoVITS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("gptsovits_clone")
        .WithName("GPT-SoVITS")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithModelPrefix("GPTSoVITS")
        .WithModelClass("gptsovits_clone", "GPT-SoVITS")
        .AddFeatureFlag("audiolab_clone")
        .AddFeatureFlag("gptsovits_clone_params")
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "GPT-SoVITS Default", Description = "Text-to-speech with voice cloning: generates new speech from text using a ~1 min reference audio clip. Unlike RVC/OpenVoice, this creates speech from text rather than converting existing audio. CJK + English.", SourceUrl = "https://github.com/RVC-Boss/GPT-SoVITS", License = "MIT", EstimatedSize = "~2GB", EstimatedVram = "~4GB" }
    ];

    #endregion
}
