using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>VibeVoice TTS provider — long-form multi-speaker synthesis (up to 90 min, 4 speakers).
/// Community-maintained fork after Microsoft removed the original repo.</summary>
public sealed class VibeVoiceProvider : IAudioProviderSource
{
    /// <summary>Gets the singleton instance of the VibeVoice TTS provider.</summary>
    public static VibeVoiceProvider Instance { get; } = new();

    /// <summary>Builds and returns the VibeVoice TTS provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("vibevoice_tts")
        .WithName("VibeVoice TTS")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("VibeVoice")
        .WithModelClass("vibevoice_tts", "VibeVoice TTS")
        .AddFeatureFlag("audiolab_tts")
        .AddFeatureFlag("vibevoice_tts_params")
        .AddFeatureFlag("tts_voice_ref")
        // Real incremental generation (IStreamingTtsRunner — the acoustic VAE already decodes one real chunk
        // per AR step, this just emits it immediately instead of buffering to the end), not AudioLab's own
        // text-chunk-and-regenerate loop — see AudioEngineBridge.SupportsNativeStreaming, the single source of
        // truth this flag also drives.
        .AddFeatureFlag("tts_streaming")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    #region Models

    // The in-process engine also registers "vibevoice:realtime" (VibeVoice-Realtime-0.5B, split-LM
    // architecture, real weights load) — deliberately NOT advertised here yet: its checkpoint ships no
    // acoustic-VAE encoder, so zero-shot voice cloning is architecturally impossible against it, and
    // upstream's own precomputed per-speaker voice-cache files aren't wired in either (needs a dedicated
    // pickle deserializer). Calling it throws a clear NotSupportedException rather than producing
    // unconditioned audio — don't surface it in the UI until one of those two paths lands. 7B is not
    // loadable yet either — advertise only 1.5B.
    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "1.5b", Name = "VibeVoice 1.5B", Description = "Long-form multi-speaker TTS, up to 90 min, 4 speakers", SourceUrl = "https://huggingface.co/vibevoice/VibeVoice-1.5B", License = "MIT", EstimatedSize = "~5GB", EstimatedVram = "~7GB", EngineConfig = new() { ["model_name"] = "vibevoice/VibeVoice-1.5B" } }
    ];

    #endregion
}
