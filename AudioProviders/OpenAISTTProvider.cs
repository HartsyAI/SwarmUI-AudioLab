using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>OpenAI Transcription provider -- STT via OpenAI API (whisper-1, gpt-4o-transcribe).</summary>
public sealed class OpenAISTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static OpenAISTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("openai_stt")
        .WithName("OpenAI Transcription")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("OpenAISTT")
        .WithModelClass("openai_stt", "OpenAI Transcription")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("openai_stt_params")
        .WithApiProvider("openai_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "gpt-4o-transcribe", Name = "GPT-4o Transcribe", Description = "Best accuracy transcription model", SourceUrl = "https://platform.openai.com/docs/guides/speech-to-text", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" },
        new() { Id = "gpt-4o-mini-transcribe", Name = "GPT-4o Mini Transcribe", Description = "Fast, affordable transcription", SourceUrl = "https://platform.openai.com/docs/guides/speech-to-text", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" },
        new() { Id = "whisper-1", Name = "Whisper-1", Description = "OpenAI Whisper model via API", SourceUrl = "https://platform.openai.com/docs/guides/speech-to-text", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
