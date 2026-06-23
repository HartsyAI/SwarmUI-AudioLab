using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>YuE provider — full-song music generation with vocals from genre tags and lyrics (Apache 2.0).</summary>
public sealed class YuEProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the YuE provider.</summary>
    public static YuEProvider Instance { get; } = new();

    /// <summary>Builds and returns the YuE music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("yue_music")
        .WithName("YuE Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithModelPrefix("YuE")
        .WithModelClass("yue_music", "YuE Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("yue_music_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "en-cot",
            Name = "YuE English (Chain-of-Thought)",
            Description = "7B params, best quality English song generation. Uses chain-of-thought reasoning for better lyric alignment. Slower but higher quality. Requires ~16GB VRAM (fp16), ~10GB (8-bit), or ~8GB (4-bit).",
            SourceUrl = "https://huggingface.co/m-a-p/YuE-s1-7B-anneal-en-cot",
            License = "Apache-2.0",
            EstimatedSize = "~14GB",
            EstimatedVram = "~16GB (fp16)",
            EngineConfig = new() { ["model_name"] = "m-a-p/YuE-s1-7B-anneal-en-cot" }
        },
        new()
        {
            Id = "en-icl",
            Name = "YuE English (In-Context Learning)",
            Description = "7B params, faster English song generation via in-context pattern matching. Good for style transfer with reference audio. Requires ~16GB VRAM (fp16), ~10GB (8-bit), or ~8GB (4-bit).",
            SourceUrl = "https://huggingface.co/m-a-p/YuE-s1-7B-anneal-en-icl",
            License = "Apache-2.0",
            EstimatedSize = "~14GB",
            EstimatedVram = "~16GB (fp16)",
            EngineConfig = new() { ["model_name"] = "m-a-p/YuE-s1-7B-anneal-en-icl" }
        },
        new()
        {
            Id = "zh-cot",
            Name = "YuE Chinese (Chain-of-Thought)",
            Description = "7B params, Chinese/Cantonese song generation with chain-of-thought reasoning. Best for Mandarin and Cantonese lyrics. Requires ~16GB VRAM (fp16), ~10GB (8-bit), or ~8GB (4-bit).",
            SourceUrl = "https://huggingface.co/m-a-p/YuE-s1-7B-anneal-zh-cot",
            License = "Apache-2.0",
            EstimatedSize = "~14GB",
            EstimatedVram = "~16GB (fp16)",
            EngineConfig = new() { ["model_name"] = "m-a-p/YuE-s1-7B-anneal-zh-cot" }
        },
        new()
        {
            Id = "zh-icl",
            Name = "YuE Chinese (In-Context Learning)",
            Description = "7B params, faster Chinese/Cantonese song generation. In-context learning mode for pattern matching and style transfer. Requires ~16GB VRAM (fp16), ~10GB (8-bit), or ~8GB (4-bit).",
            SourceUrl = "https://huggingface.co/m-a-p/YuE-s1-7B-anneal-zh-icl",
            License = "Apache-2.0",
            EstimatedSize = "~14GB",
            EstimatedVram = "~16GB (fp16)",
            EngineConfig = new() { ["model_name"] = "m-a-p/YuE-s1-7B-anneal-zh-icl" }
        },
    ];

    #endregion
}
