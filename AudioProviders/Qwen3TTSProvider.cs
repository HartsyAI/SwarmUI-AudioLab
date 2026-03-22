using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>Qwen3-TTS provider -- multilingual TTS with voice cloning, custom voices, and voice design by Alibaba.</summary>
public sealed class Qwen3TTSProvider : IAudioProviderSource
{
    /// <summary>Singleton instance of the Qwen3-TTS provider.</summary>
    public static Qwen3TTSProvider Instance { get; } = new();

    /// <summary>Builds and returns the Qwen3-TTS provider definition with dependencies and models.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("qwen3_tts")
        .WithName("Qwen3 TTS")
        .WithCategory(AudioCategory.TTS)
        .WithPythonEngine("tts_qwen3", "Qwen3TTSEngine")
        .WithModelPrefix("Qwen3TTS")
        .WithModelClass("qwen3_tts", "Qwen3 TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("qwen3tts_tts_params")
        .AddFeatureFlag("qwen3tts_speaker_params")
        .AddFeatureFlag("qwen3tts_instruct_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddDependencies(Dependencies)
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Dependencies

    private static PackageDefinition[] Dependencies =>
    [
        new() { Name = "numpy>=1.26.0", InstallName = "numpy>=1.26.0", ImportName = "numpy", Category = "core" },
        new() { Name = "soundfile>=0.12.0", InstallName = "soundfile>=0.12.0", ImportName = "soundfile", Category = "core" },
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // qwen-tts with --no-deps to avoid pinned transformers==4.57.3 and unnecessary gradio/sox
        new() { Name = "qwen-tts", InstallName = "qwen-tts", ImportName = "qwen_tts", Category = "tts", EstimatedInstallTimeMinutes = 3, CustomInstallArgs = "--no-deps" },
        // Explicit runtime dependencies needed by qwen-tts inference
        new() { Name = "transformers>=4.57.0", InstallName = "transformers>=4.57.0", ImportName = "transformers", Category = "tts" },
        new() { Name = "accelerate>=1.12.0", InstallName = "accelerate>=1.12.0", ImportName = "accelerate", Category = "tts" },
        new() { Name = "librosa", InstallName = "librosa", ImportName = "librosa", Category = "tts" },
        new() { Name = "einops", InstallName = "einops", ImportName = "einops", Category = "tts" },
        new() { Name = "onnxruntime", InstallName = "onnxruntime", ImportName = "onnxruntime", Category = "tts" },
        new() { Name = "safetensors", InstallName = "safetensors", ImportName = "safetensors", Category = "tts" },
        new() { Name = "huggingface_hub", InstallName = "huggingface_hub", ImportName = "huggingface_hub", Category = "tts" },
        // sox is a hard import in qwen_tts/core/tokenizer_25hz/vq/speech_vq.py; requires SoX system binary on PATH
        new() { Name = "sox", InstallName = "sox", ImportName = "sox", Category = "tts" }
    ];

    #endregion

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "1.7B-Base",
            Name = "Qwen3-TTS 1.7B Base",
            Description = "1.7B voice cloning model. Provide reference audio + transcript to clone any voice. 10 languages. Requires ~8GB VRAM.",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            License = "Apache 2.0",
            EstimatedSize = "~3.4GB",
            EstimatedVram = "~8GB",
            ModelClassId = "qwen3_tts_clone",
            ModelClassName = "Qwen3 TTS Clone",
            EngineConfig = new() { ["model_name"] = "Qwen/Qwen3-TTS-12Hz-1.7B-Base", ["mode"] = "voice_clone" }
        },
        new()
        {
            Id = "0.6B-Base",
            Name = "Qwen3-TTS 0.6B Base",
            Description = "Lightweight 0.6B voice cloning model. Provide reference audio + transcript. 10 languages. Requires ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base",
            License = "Apache 2.0",
            EstimatedSize = "~1.2GB",
            EstimatedVram = "~4GB",
            ModelClassId = "qwen3_tts_clone",
            ModelClassName = "Qwen3 TTS Clone",
            EngineConfig = new() { ["model_name"] = "Qwen/Qwen3-TTS-12Hz-0.6B-Base", ["mode"] = "voice_clone" }
        },
        new()
        {
            Id = "1.7B-CustomVoice",
            Name = "Qwen3-TTS 1.7B CustomVoice",
            Description = "1.7B model with 9 premium speakers and natural language instruction control for emotion/style. 10 languages. Requires ~8GB VRAM.",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
            License = "Apache 2.0",
            EstimatedSize = "~3.4GB",
            EstimatedVram = "~8GB",
            ModelClassId = "qwen3_tts_custom",
            ModelClassName = "Qwen3 TTS CustomVoice",
            EngineConfig = new() { ["model_name"] = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", ["mode"] = "custom_voice" }
        },
        new()
        {
            Id = "0.6B-CustomVoice",
            Name = "Qwen3-TTS 0.6B CustomVoice",
            Description = "Lightweight 0.6B custom voice model with 9 premium speakers. 10 languages. Requires ~4GB VRAM.",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice",
            License = "Apache 2.0",
            EstimatedSize = "~1.2GB",
            EstimatedVram = "~4GB",
            ModelClassId = "qwen3_tts_custom",
            ModelClassName = "Qwen3 TTS CustomVoice",
            EngineConfig = new() { ["model_name"] = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", ["mode"] = "custom_voice" }
        },
        new()
        {
            Id = "1.7B-VoiceDesign",
            Name = "Qwen3-TTS 1.7B VoiceDesign",
            Description = "1.7B model that generates voices from natural language descriptions (e.g. 'A warm deep male voice with a British accent'). 10 languages. Requires ~8GB VRAM.",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign",
            License = "Apache 2.0",
            EstimatedSize = "~3.4GB",
            EstimatedVram = "~8GB",
            ModelClassId = "qwen3_tts_design",
            ModelClassName = "Qwen3 TTS VoiceDesign",
            EngineConfig = new() { ["model_name"] = "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", ["mode"] = "voice_design" }
        }
    ];

    #endregion
}
