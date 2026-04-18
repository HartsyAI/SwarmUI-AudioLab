using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Azure Speech STT provider -- real-time and batch speech recognition.</summary>
public sealed class AzureSTTProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static AzureSTTProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("azure_stt")
        .WithName("Azure Speech STT")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("AzureSTT")
        .WithModelClass("azure_stt", "Azure Speech STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("azure_stt_params")
        .WithApiProvider("azure_speech_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Azure Speech STT", Description = "Real-time speech recognition with custom models", SourceUrl = "https://azure.microsoft.com/en-us/products/ai-services/speech-to-text", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
