using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>HeartLib provider — full-song music generation with vocals from style tags and lyrics (Apache 2.0).</summary>
public sealed class HeartLibProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the HeartLib provider.</summary>
    public static HeartLibProvider Instance { get; } = new();

    /// <summary>Builds and returns the HeartLib music generation provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("heartlib_music")
        .WithName("HeartLib Music")
        .WithCategory(AudioCategory.AudioGeneration)
        .WithPythonEngine("music_heartlib", "HeartLibEngine")
        .WithModelPrefix("HeartLib")
        .WithModelClass("heartlib_music", "HeartLib Music")
        .AddFeatureFlag("audiolab_audiogen")
        .AddFeatureFlag("heartlib_music_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("heartlib")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=2.0.0", InstallName = "numpy>=2.0.0", ImportName = "numpy", Category = "core" },
        new() { Name = "torch==2.7.1+cu128", InstallName = "torch==2.7.1+cu128", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "torchaudio==2.7.1+cu128", InstallName = "torchaudio==2.7.1+cu128", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "torchvision==0.22.1+cu128", InstallName = "torchvision==0.22.1+cu128", ImportName = "torchvision", Category = "pytorch", EstimatedInstallTimeMinutes = 8, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu128" },
        new() { Name = "transformers>=4.57.0", InstallName = "transformers>=4.57.0", ImportName = "transformers", Category = "ml" },
        new() { Name = "accelerate", InstallName = "accelerate", ImportName = "accelerate", Category = "ml" },
        new() { Name = "einops", InstallName = "einops", ImportName = "einops", Category = "ml" },
        new() { Name = "soundfile", InstallName = "soundfile", ImportName = "soundfile", Category = "core" },
        new() { Name = "tokenizers>=0.22.0", InstallName = "tokenizers>=0.22.0", ImportName = "tokenizers", Category = "ml" },
        new() { Name = "torchtune==0.4.0", InstallName = "torchtune==0.4.0", ImportName = "torchtune", Category = "music" },
        new() { Name = "torchao==0.9.0", InstallName = "torchao==0.9.0", ImportName = "torchao", Category = "music" },
        new() { Name = "vector-quantize-pytorch", InstallName = "vector-quantize-pytorch", ImportName = "vector_quantize_pytorch", Category = "music" },
        new() { Name = "tqdm", InstallName = "tqdm", ImportName = "tqdm", Category = "core" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "core" },
        new() { Name = "heartlib", InstallName = "git+https://github.com/HeartMuLa/heartlib.git", ImportName = "heartlib", Category = "music", EstimatedInstallTimeMinutes = 5, CustomInstallArgs = "--no-deps" },
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "3b-hny",
            Name = "HeartMuLa 3B (Happy New Year)",
            Description = "4B params, latest and best HeartMuLa model. Generates full songs with vocals from lyrics and style tags. Best lyrics controllability and music quality. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-oss-3B-happy-new-year",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-oss-3B-happy-new-year" }
        },
        new()
        {
            Id = "3b-base",
            Name = "HeartMuLa 3B (Base)",
            Description = "4B params, original HeartMuLa release. Solid music generation quality. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-oss-3B",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-oss-3B" }
        },
        new()
        {
            Id = "3b-rl",
            Name = "HeartMuLa 3B (RL-Tuned)",
            Description = "4B params, reinforcement learning optimized variant. Improved output quality via DPO training. Requires ~12GB VRAM (lazy load) or ~16GB VRAM (full load).",
            SourceUrl = "https://huggingface.co/HeartMuLa/HeartMuLa-RL-oss-3B-20260123",
            License = "Apache-2.0",
            EstimatedSize = "~12GB",
            EstimatedVram = "~12GB (lazy load)",
            EngineConfig = new() { ["model_name"] = "HeartMuLa/HeartMuLa-RL-oss-3B-20260123" }
        },
    ];

    #endregion
}
