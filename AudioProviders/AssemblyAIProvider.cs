using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>AssemblyAI provider -- best-in-class accuracy STT with diarization and sentiment.</summary>
public sealed class AssemblyAIProvider : IAudioProviderSource
{
    /// <summary>Singleton instance.</summary>
    public static AssemblyAIProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("assemblyai_stt")
        .WithName("AssemblyAI")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("AssemblyAI")
        .WithModelClass("assemblyai_stt", "AssemblyAI STT")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("assemblyai_stt_params")
        .WithApiProvider("assemblyai_api")
        .AddModels(Models)
        .WithEngineGroup("api")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "AssemblyAI Universal-2", Description = "Best-in-class accuracy with speaker diarization and sentiment analysis", SourceUrl = "https://www.assemblyai.com", License = "Commercial API", EstimatedSize = "API", EstimatedVram = "None (API)" }
    ];
}
