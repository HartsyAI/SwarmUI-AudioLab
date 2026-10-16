using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Fish Audio Drama 3 TTS provider -- a 35B-A3B mixture-of-experts model over S2's 10-codebook codec, directed with inline
/// <c>[bracket cues]</c>, multi-speaker through <c>&lt;|speaker:N|&gt;</c> tags, cloning from one or more references, and revision of a
/// take sent back as history. Each model id is an engine variant token (<c>native</c>, <c>int4</c>), so it reaches the engine as
/// <c>fishaudio-drama3:{id}</c>.</summary>
public sealed class FishAudioDrama3Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Fish Audio Drama 3 provider.</summary>
    public static FishAudioDrama3Provider Instance { get; } = new();

    /// <summary>Builds and returns the Fish Audio Drama 3 provider definition. No shared sampling flag: its UI defaults would replace
    /// the model's own (temperature 0.7, top-p 0.7, top-k 30) on every take.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("fishaudio_drama3_tts")
        .WithName("Fish Audio Drama 3 TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("FishAudioDrama3")
        .WithModelClass("fishaudio_drama3_tts", "Fish Audio Drama 3 TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("tts_voice_ref")
        .AddFeatureFlag("tts_streaming")
        .AddFeatureFlag("fishaudio_drama3_tts_params")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "native",
            Name = "Fish Audio Drama 3",
            Description = "The official release: FP8 experts, loaded as Q8_0. Directable with [bracket cues], multi-speaker, voice cloning from references with their transcripts, and revision of a take sent back as history. Larger than most cards: the routed experts are offloaded to the CPU when it does not fit.",
            SourceUrl = "https://huggingface.co/fishaudio/drama-3",
            License = "Fish Audio Research License (non-commercial)",
            EstimatedSize = "~38GB",
            EstimatedVram = "~40GB (experts offload on smaller GPUs)",
        },
        new()
        {
            Id = "int4",
            Name = "Fish Audio Drama 3 (int4)",
            Description = "The GPTQ 4-bit release, loaded losslessly as Q4_0: about half the memory of the default build.",
            SourceUrl = "https://huggingface.co/fishaudio/drama-3-int4",
            License = "Fish Audio Research License (non-commercial)",
            EstimatedSize = "~21GB",
            EstimatedVram = "~22GB (experts offload on smaller GPUs)",
        },
    ];

    #endregion
}
