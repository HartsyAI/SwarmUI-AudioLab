using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenVoice v2 provider — transfers the tone/style of a reference voice onto existing audio. Audio in, audio out (no text generation).</summary>
public sealed class OpenVoiceProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the OpenVoice provider.</summary>
    public static OpenVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the OpenVoice V2 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("openvoice_clone")
        .WithName("OpenVoice V2")
        .WithCategory(AudioCategory.VoiceConversion)
        .WithModelPrefix("OpenVoice")
        .WithModelClass("openvoice_clone", "OpenVoice V2")
        .AddFeatureFlag("audiolab_clone")
        .AddFeatureFlag("openvoice_clone_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "v2", Name = "OpenVoice V2", Description = "Voice tone transfer: takes existing audio + a reference voice clip, outputs the same speech with the reference voice's tone/style applied. Zero-shot, no model training needed.", SourceUrl = "https://github.com/myshell-ai/OpenVoice", License = "MIT", EstimatedSize = "~500MB", EstimatedVram = "~2GB", SelfManaged = true, EngineConfig = new() { ["model_version"] = "v2" } }
    ];

    #endregion
}
