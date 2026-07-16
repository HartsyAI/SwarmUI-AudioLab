using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>StyleTTS 2 provider — diffusion-style TTS reusing the Kokoro modules, 24 kHz. Engine-hosted; gated on
/// a StyleTts2 submodule load helper (see <see cref="AudioServices.Tts.StyleTts2Model"/>).</summary>
public sealed class StyleTTS2Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the StyleTTS 2 provider.</summary>
    public static StyleTTS2Provider Instance { get; } = new();

    /// <summary>Builds and returns the StyleTTS 2 provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("styletts2_tts")
        .WithName("StyleTTS 2")
        .WithCategory(AudioCategory.TTS)
        .WithModelPrefix("StyleTTS2")
        .WithModelClass("styletts2_tts", "StyleTTS 2")
        .AddFeatureFlag("audiolab_tts")
        .AddModels(Models)
        .WithEngineGroup("main")
        .Build();

    private static AudioModelDefinition[] Models =>
    [
        new() { Id = "libritts", Name = "StyleTTS 2 LibriTTS", Description = "Multi-speaker TTS, 24 kHz, zero-shot voice cloning — supply a Reference Audio clip and it speaks your text in that voice. For best quality use a CLEAN, full-bandwidth 24 kHz reference (studio/podcast); it faithfully clones the reference's recording character, so a phone-quality or old/band-limited clip yields tinny output. MIT via the yl4579/StyleTTS2 GitHub repo (the HF weights repo declares no license).", SourceUrl = "https://huggingface.co/yl4579/StyleTTS2-LibriTTS", License = "MIT", EstimatedSize = "~750MB", EstimatedVram = "~2GB" }
    ];
}
