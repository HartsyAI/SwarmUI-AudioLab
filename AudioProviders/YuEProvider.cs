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
        .WithPythonEngine("music_yue", "YuEEngine")
        .WithModelPrefix("YuE")
        .WithModelClass("yue_music", "YuE Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("yue_music_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("yue")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.7.1+cu128", InstallName = "torch==2.7.1+cu128", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "torchaudio==2.7.1+cu128", InstallName = "torchaudio==2.7.1+cu128", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "transformers>=4.45.0", InstallName = "transformers>=4.45.0", ImportName = "transformers", Category = "music" },
        new() { Name = "accelerate>=0.26.0", InstallName = "accelerate>=0.26.0", ImportName = "accelerate", Category = "music" },
        new() { Name = "bitsandbytes>=0.45.0", InstallName = "bitsandbytes>=0.45.0", ImportName = "bitsandbytes", Category = "music", EstimatedInstallTimeMinutes = 5 },
        new() { Name = "sentencepiece", InstallName = "sentencepiece", ImportName = "sentencepiece", Category = "music" },
        new() { Name = "einops>=0.7.0", InstallName = "einops>=0.7.0", ImportName = "einops", Category = "music" },
        new() { Name = "omegaconf>=2.3.0", InstallName = "omegaconf>=2.3.0", ImportName = "omegaconf", Category = "music" },
        new() { Name = "scipy>=1.10.1", InstallName = "scipy>=1.10.1", ImportName = "scipy", Category = "music" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "music" },
        new() { Name = "descript-audio-codec", InstallName = "descript-audio-codec", ImportName = "dac", Category = "music", CustomInstallArgs = "--no-deps" },
        new() { Name = "descript-audiotools>=0.7.2", InstallName = "descript-audiotools>=0.7.2", ImportName = "audiotools", Category = "music", CustomInstallArgs = "--no-deps" },
    ];

    #endregion

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
