using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>AuK provider — Tencent's zero-shot voice cloning and instruction (voice design) TTS, with a 4-step distilled Flash variant.</summary>
public sealed class AukProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the AuK provider.</summary>
    public static AukProvider Instance { get; } = new();

    /// <summary>Builds and returns the AuK provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("auk_tts")
        .WithName("AuK")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("AuK")
        .WithModelClass("auk_tts", "AuK")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("auk_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    private const string LicenseText = "MIT (AuK); text encoder Qwen2.5-Omni-3B is under the Qwen Research License";

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "flash", Name = "AuK-Flash", Description = "Distilled 4-step zero-shot voice cloning and instruction TTS (no CFG; steps and CFG are ignored). Needs the Qwen2.5-Omni-3B text encoder (~10GB, auto-downloaded, Qwen Research License). 24kHz.", SourceUrl = "https://huggingface.co/tencent/AuK-Flash", License = LicenseText, EstimatedSize = "~12GB (incl. ~10GB Qwen2.5-Omni-3B encoder)", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "tencent/AuK-Flash" } },
        new() { Id = "base", Name = "AuK", Description = "Full-quality zero-shot voice cloning and instruction TTS (default 32 steps, CFG 2.0). Needs the Qwen2.5-Omni-3B text encoder (~10GB, auto-downloaded, Qwen Research License). 24kHz.", SourceUrl = "https://huggingface.co/tencent/AuK", License = LicenseText, EstimatedSize = "~12GB (incl. ~10GB Qwen2.5-Omni-3B encoder)", EstimatedVram = "~10GB", EngineConfig = new() { ["model_name"] = "tencent/AuK" } }
    ];

    #endregion
}
