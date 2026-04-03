using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>AWS Transcribe provider -- speech-to-text with custom vocabulary support.</summary>
public sealed class AWSTranscribeProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static AWSTranscribeProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("aws_transcribe")
        .WithName("AWS Transcribe")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("AWSTranscribe")
        .WithModelClass("aws_transcribe", "AWS Transcribe")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("aws_stt_params")
        .WithApiProvider("aws_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "AWS Transcribe", Description = "Speech-to-text with custom vocabulary and content redaction", SourceUrl = "https://aws.amazon.com/transcribe/", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
