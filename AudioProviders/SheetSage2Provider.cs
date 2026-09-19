using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviders;

/// <summary>SheetSage2 — music transcription. It listens to a recording and writes the two-voice ABC lead sheet
/// YuE2 plans from, so an existing song can be loaded into the Score tab, edited, and re-rendered in a new
/// style.</summary>
/// <remarks>Filed under STT because it is audio→text and rides the transcription service, but it writes a score
/// rather than a sentence: no language, no translation, and a one-minute clip is a minute of GPU. Its weights
/// live inside the YuE2 repo and are cached beside it, so a machine that already generates with YuE2 fetches
/// only the 1.39 GB encoder file.</remarks>
public sealed class SheetSage2Provider : IAudioProviderSource
{
    /// <summary>Singleton instance of the SheetSage2 provider.</summary>
    public static SheetSage2Provider Instance { get; } = new();

    /// <summary>Builds and returns the SheetSage2 transcription provider definition.</summary>
    public AudioProviderDefinition GetProvider() => AudioProviderDefinitionBuilder.Create()
        .WithId("sheetsage2_transcribe")
        .WithName("SheetSage2 Transcription")
        .WithCategory(AudioCategory.STT)
        .WithModelPrefix("SheetSage2")
        .WithModelClass("sheetsage2", "SheetSage2")
        .AddFeatureFlag("audiolab_stt")
        .AddFeatureFlag("sheetsage2_params")
        .AddModels(Models)
        .WithEngineGroup("music")
        .Build();

    #region Models

    private static AudioModelDefinition[] Models =>
    [
        new()
        {
            Id = "bf16",
            Name = "SheetSage2 (BF16)",
            Description = "Transcribes music into an ABC lead sheet: melody, chord symbols, key, meter and tempo. "
                + "A MERT2 conformer encoder over 24 kHz audio feeding a score decoder, read in sliding 300-second "
                + "windows. Ships inside the YuE2 repo, so the two share one download directory.",
            SourceUrl = "https://huggingface.co/Comfy-Org/YuE2",
            License = "CC-BY-NC-4.0",
            EstimatedSize = "~1.4GB",
            EstimatedVram = "~4GB (bf16)",
            EngineConfig = new() { ["model_name"] = "Comfy-Org/YuE2" }
        },
    ];

    #endregion
}
