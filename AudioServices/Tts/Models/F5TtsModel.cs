using System;
using SwarmUI.Utils;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>F5-TTS v1 Base (SWivid/F5-TTS) — zero-shot voice-clone TTS: a flow-matching DiT denoises a target
/// mel in the voice of a reference clip, vocoded by Vocos to 24 kHz. Provider id <c>f5_tts</c>. The DiT, the
/// char vocab, and the Vocos vocoder all auto-download on first use (see <see cref="F5TtsPipeline.LoadAsync"/>).
///
/// <para><b>Zero-shot:</b> the user must supply a voice reference clip <i>and</i> the transcript of that clip
/// (the reference text). F5 conditions on the reference mel + reference text to clone the voice, then generates
/// the target text in that voice. We compute the reference mel here with the exact Vocos mel-24k feature
/// extractor F5 expects (24 kHz, n_fft 1024, hop 256, 100 mels, magnitude, natural-log).</para>
///
/// <para><b>Runtime-pending:</b> the engine's F5 pipeline is wired structurally but is flagged in-source as not
/// yet validated against real reference audio (the duration heuristic, CFG mixing, and in-context infill masking
/// need numerical reference dumps). It runs end-to-end and produces audio; audible-quality parity is a
/// follow-up.</para></summary>
public static class F5TtsModel
{
    private const string Repo = "SWivid/F5-TTS";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (_, ct) =>
        {
            F5TtsPipeline pipeline = await F5TtsPipeline.LoadAsync(ct: ct).ConfigureAwait(false);
            Logs.Info("[AudioLab][F5-TTS] Loaded SWivid/F5-TTS v1 Base (DiT + Vocos 24 kHz). Zero-shot: needs a voice reference + its transcript.");

            return new TtsRunner(24_000, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new InvalidOperationException(
                        "F5-TTS is zero-shot — it needs a voice reference. Upload a short WAV clip in the voice reference field, "
                        + "and put the words spoken in that clip in the reference-text field.");
                }
                if (string.IsNullOrWhiteSpace(req.RefText))
                {
                    throw new InvalidOperationException(
                        "F5-TTS needs the transcript of the reference clip (exactly what is said in it) in the reference-text "
                        + "field — it aligns the cloned voice against that text.");
                }

                // GenerateFromAudio owns the correct F5/Vocos mel front-end (HTK scale, no area norm, center
                // reflect-pad, magnitude, natural-log clamp 1e-5) + target_rms 0.1 + ref-text normalization.
                // Building the mel here with a Slaney/no-center config was the "distorted robot" bug.
                return pipeline.GenerateFromAudio(backend, req.ReferenceMono24k, 24_000, req.RefText, req.Text,
                    new F5TtsOptions
                    {
                        Seed = (ulong)req.Seed,
                        Steps = req.NfeStep ?? 32,
                        CfgStrength = req.CfgScale.HasValue ? (float)req.CfgScale.Value : 2.0f,
                        Speed = req.Speed.HasValue ? (float)req.Speed.Value : 1.0f,
                    });
            }, pipeline);
        },
    };
}
