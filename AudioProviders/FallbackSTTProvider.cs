using Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;

namespace Hartsy.Extensions.VoiceAssistant.AudioProviders;

/// <summary>Zero-dependency fallback STT provider — returns placeholder transcription.</summary>
public sealed class FallbackSTTProvider : IAudioProviderSource
{
    public static FallbackSTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fallback_stt")
        .WithName("Fallback STT")
        .WithCategory(AudioCategory.STT)
        .WithPythonEngine("stt_fallback", "FallbackSTTEngine")
        .WithModelPrefix("FallbackSTT")
        .WithModelClass("fallback_stt", "Fallback STT")
        .AddFeatureFlag("fallback_stt_params")
        .AddModels(Models)
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Fallback Placeholder", Description = "Returns placeholder text — install Whisper or RealtimeSTT for real transcription" }
    ];
}
