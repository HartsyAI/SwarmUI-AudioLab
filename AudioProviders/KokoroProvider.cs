using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Kokoro TTS provider — ultra-fast lightweight TTS (82M params, CPU-capable).</summary>
public sealed class KokoroProvider : IAudioProviderSource
{
    public static KokoroProvider Instance { get; } = new();

    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("kokoro_tts")
        .WithName("Kokoro TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_kokoro", "KokoroEngine")
        .WithModelPrefix("Kokoro")
        .WithModelClass("kokoro_tts", "Kokoro TTS")
        .AddFeatureFlag("kokoro_tts_params")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torch>=2.0.0", InstallName = "torch>=2.0.0", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12 },
        // kokoro installed with --no-deps: its misaki[en] extra pulls spacy-curated-transformers → blis
        // which has no Python 3.13 wheels. The actual runtime deps are listed explicitly below.
        new() { Name = "kokoro", InstallName = "kokoro", ImportName = "kokoro", Category = "tts", CustomInstallArgs = "--no-deps" },
        // Explicit kokoro runtime dependencies
        new() { Name = "misaki", InstallName = "misaki", ImportName = "misaki", Category = "tts", CustomInstallArgs = "--no-deps" },
        new() { Name = "regex", InstallName = "regex", ImportName = "regex", Category = "tts" },
        new() { Name = "loguru", InstallName = "loguru", ImportName = "loguru", Category = "tts" },
        new() { Name = "scipy", InstallName = "scipy", ImportName = "scipy", Category = "tts" },
        new() { Name = "num2words", InstallName = "num2words", ImportName = "num2words", Category = "tts" },
        new() { Name = "phonemizer", InstallName = "phonemizer", ImportName = "phonemizer", Category = "tts" },
        new() { Name = "spacy", InstallName = "spacy", ImportName = "spacy", Category = "tts" },
        new() { Name = "transformers>=4.40.0", InstallName = "transformers>=4.40.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "tts" }
    ];

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "default", Name = "Kokoro Default", Description = "82M param model, 96x real-time on GPU, CPU-capable (~1GB VRAM or CPU)" }
    ];
}
