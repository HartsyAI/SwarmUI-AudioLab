using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>RVC provider — converts the voice in existing audio to a different voice using a trained model. Audio in, audio out (no text generation).</summary>
public sealed class RVCProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the RVC provider.</summary>
    public static RVCProvider Instance { get; } = new();

    /// <summary>Builds and returns the RVC voice conversion provider definition. Takes existing audio + a voice model, outputs the same speech in the target voice.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("rvc_clone")
        .WithName("RVC Voice Conversion")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithModelPrefix("RVC")
        .WithModelClass("rvc_clone", "RVC Voice Conversion")
        .AddFeatureFlag("audiolab_clone")
        .AddFeatureFlag("rvc_clone_params")
        .AddModels(Models)
        .WithEngineGroup("linux_docker")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "RVC V2", Description = "Voice conversion: re-voices existing audio using a trained .pth voice model. Does not generate new speech — takes audio in, outputs the same speech in a different voice.", SourceUrl = "https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~4GB", EngineConfig = new() { ["model_version"] = "v2" } }
    ];

    #endregion
}
