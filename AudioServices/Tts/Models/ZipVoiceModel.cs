using System;
using SwarmUI.Utils;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>ZipVoice (k2-fsa/ZipVoice) — zero-shot voice-clone TTS: a flow-matching Zipformer (fm_decoder +
/// text_encoder) denoises a target mel in the voice of a reference clip, vocoded by the shared Vocos port
/// (same <c>lucasnewman/vocos-mel-24khz</c> family F5-TTS already uses) to 24 kHz. Provider id
/// <c>zipvoice_tts</c>. The Zipformer weights, tokens vocab, and Vocos vocoder all auto-download on first use
/// (see <see cref="ZipVoicePipeline.LoadAsync"/>).
///
/// <para><b>Zero-shot:</b> like F5-TTS, the user must supply a voice reference clip <i>and</i> the transcript
/// of that clip (the reference text) — ZipVoice conditions on the reference mel + reference text to clone the
/// voice, then generates the target text in that voice. English only (the tokenizer is espeak-ng IPA
/// phonemization; no Chinese support despite upstream being bilingual — a known, accepted scope limit).</para>
///
/// <para><b>Verified 2026-07-19:</b> full pipeline (mel extraction, joint-text tokenization, duration
/// prediction, Euler flow-matching sampling with CFG, Vocos decode) exercised end to end on real weights for
/// the first time — a 5s JFK reference clone generating a 13-word target sentence produced non-silent,
/// finite, non-clipping audio (RMS 0.095, peak 0.61) whose Whisper transcript recovered the target sentence's
/// content words. The backbone forward passes were separately parity-verified (cosine 1.000000 vs the real
/// Python reference) — see PARITY_VERIFICATION. Not yet perf-passed (base checkpoint, 16 steps + CFG measured
/// at RTF ~87 on an RTX 3060 — usable for correctness, slow for production; a perf pass is a follow-up).</para></summary>
public static class ZipVoiceModel
{
    private const string Repo = "k2-fsa/ZipVoice";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (_, ct) =>
        {
            ZipVoicePipeline pipeline = await ZipVoicePipeline.LoadAsync(ct: ct).ConfigureAwait(false);
            Logs.Info("[AudioLab][ZipVoice] Loaded k2-fsa/ZipVoice (Zipformer fm_decoder + text_encoder + Vocos 24 kHz). "
                + "Zero-shot: needs a voice reference + its transcript. English only.");

            return new TtsRunner(24_000, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new InvalidOperationException(
                        "ZipVoice is zero-shot — it needs a voice reference. Upload a short WAV clip in the voice reference field, "
                        + "and put the words spoken in that clip in the reference-text field.");
                }
                if (string.IsNullOrWhiteSpace(req.RefText))
                {
                    throw new InvalidOperationException(
                        "ZipVoice needs the transcript of the reference clip (exactly what is said in it) in the reference-text "
                        + "field — it aligns the cloned voice against that text.");
                }

                // GenerateFromAudio owns the mel front-end, feat_scale (0.1) normalization, and target_rms
                // matching — see ZipVoicePipeline's doc comment for the full pipeline shape.
                // Defaults (16 steps, CFG 1.0) mirror ZipVoiceOptions' own record defaults, which mirror the
                // base (non-distilled) zipvoice checkpoint's CLI defaults — spelled out here only so a UI
                // override of one knob doesn't silently reset the others to record defaults via the `with`
                // pattern; ZipVoiceOptions() with no args would give the same values.
                return pipeline.GenerateFromAudio(backend, req.ReferenceMono24k, 24_000, req.RefText, req.Text,
                    new ZipVoiceOptions
                    {
                        Seed = unchecked((ulong)req.Seed),
                        Steps = req.NfeStep ?? 16,
                        GuidanceScale = req.CfgScale.HasValue ? (float)req.CfgScale.Value : 1.0f,
                        Speed = req.Speed.HasValue ? (float)req.Speed.Value : 1.0f,
                    });
            }, pipeline);
        },
    };
}
