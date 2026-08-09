using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>MeloTTS provider — multilingual VITS TTS (MyShell), 44.1 kHz. Engine-hosted; synth gated on the
/// espeak + tone + BERT front-end (engine TTS catalog id "melotts").</summary>
public sealed class MeloTTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the MeloTTS provider.</summary>
    public static MeloTTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the MeloTTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("melotts_tts")
        .WithName("MeloTTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("MeloTTS")
        .WithModelClass("melotts_tts", "MeloTTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("melotts_tts_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "english-v3", Name = "MeloTTS English v3", Description = "VITS multilingual TTS, 44.1 kHz", SourceUrl = "https://huggingface.co/myshell-ai/MeloTTS-English-v3", License = "MIT", EstimatedSize = "~200MB", EstimatedVram = "~1GB (or CPU)" }
    ];
}
