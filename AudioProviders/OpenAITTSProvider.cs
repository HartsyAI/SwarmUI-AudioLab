using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenAI TTS provider -- text-to-speech via OpenAI API (tts-1, tts-1-hd, gpt-4o-mini-tts).</summary>
public sealed class OpenAITTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static OpenAITTSProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("openai_tts")
        .WithName("OpenAI TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("OpenAI")
        .WithModelClass("openai_tts", "OpenAI TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("openai_tts_params")
        .WithApiProvider("openai_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "tts-1", Name = "OpenAI TTS-1", Description = "Fast, affordable TTS with 6 voices", SourceUrl = "https://platform.openai.com/docs/guides/text-to-speech", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" },
        new() { Id = "tts-1-hd", Name = "OpenAI TTS-1 HD", Description = "Higher quality TTS", SourceUrl = "https://platform.openai.com/docs/guides/text-to-speech", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" },
        new() { Id = "gpt-4o-mini-tts", Name = "OpenAI GPT-4o Mini TTS", Description = "Instruction-following TTS with custom voice directions", SourceUrl = "https://platform.openai.com/docs/guides/text-to-speech", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
