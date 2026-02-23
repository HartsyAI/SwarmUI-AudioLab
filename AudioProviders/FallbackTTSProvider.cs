using Hartsy.Extensions.VoiceAssistant.AudioProviderTypes;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;

namespace Hartsy.Extensions.VoiceAssistant.AudioProviders;

/// <summary>Zero-dependency fallback TTS provider — generates silence as a placeholder.</summary>
public sealed class FallbackTTSProvider : IAudioProviderSource
{
    public static FallbackTTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fallback_tts")
        .WithName("Fallback TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_fallback", "FallbackTTSEngine")
        .WithModelPrefix("FallbackTTS")
        .WithModelClass("fallback_tts", "Fallback TTS")
        .AddFeatureFlag("fallback_tts_params")
        .AddModels(Models)
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Fallback Silence", Description = "Generates silence — install Chatterbox or Bark for real speech synthesis" }
    ];
}
