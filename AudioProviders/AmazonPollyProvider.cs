using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Amazon Polly provider -- Neural/Standard/Generative TTS with 60+ languages.</summary>
public sealed class AmazonPollyProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static AmazonPollyProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("amazon_polly")
        .WithName("Amazon Polly")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("AmazonPolly")
        .WithModelClass("amazon_polly", "Amazon Polly")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("polly_tts_params")
        .WithApiProvider("aws_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Amazon Polly", Description = "Neural, Standard, and Generative TTS engines with 60+ languages", SourceUrl = "https://aws.amazon.com/polly/", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
