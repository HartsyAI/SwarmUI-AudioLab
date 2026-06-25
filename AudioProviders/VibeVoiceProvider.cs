using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>VibeVoice TTS provider — long-form multi-speaker synthesis (up to 90 min, 4 speakers).
/// Community-maintained fork after Microsoft removed the original repo.</summary>
public sealed class VibeVoiceProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the VibeVoice TTS provider.</summary>
    public static VibeVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the VibeVoice TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("vibevoice_tts")
        .WithName("VibeVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("VibeVoice")
        .WithModelClass("vibevoice_tts", "VibeVoice TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("vibevoice_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    // The in-process C# engine loads VibeVoice-1.5B (VibeVoicePipeline.LoadAsync is fixed to that repo). The
    // 0.5B-realtime and 7B variants are not loadable yet — advertise only 1.5B to avoid silently loading 1.5B.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.5b", Name = "VibeVoice 1.5B", Description = "Long-form multi-speaker TTS, up to 90 min, 4 speakers", SourceUrl = "https://huggingface.co/vibevoice/VibeVoice-1.5B", License = "MIT", EstimatedSize = "~5GB", EstimatedVram = "~7GB", EngineConfig = new() { ["model_name"] = "vibevoice/VibeVoice-1.5B" } }
    ];

    #endregion
}
