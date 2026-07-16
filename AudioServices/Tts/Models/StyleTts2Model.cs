using System;
using System.Linq;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Phonemizer;
using HartsyInference.Phonemizer.Espeak;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>StyleTTS 2 (yl4579/StyleTTS2-LibriTTS) — diffusion-style TTS, 24 kHz. Provider id <c>styletts2_tts</c>.
/// Voice-clone: the caller supplies a reference clip; the engine extracts its 256-d style via the StarGAN-v2
/// <c>StyleEncoder</c>s (verified corr 1.0 vs upstream) and synthesizes the target text in that voice through the
/// Kokoro-shared PLBERT/text-encoder/prosody backbone + the LibriTTS <c>type: hifigan</c> generator
/// (<c>StyleHifiGanGenerator</c>, corr 0.999999). Text→IPA via the engine's espeak phonemizer; the 178-symbol
/// StyleTTS2 tokenizer is embedded in-engine. Random/unconditional (no-reference) synthesis needs the diffusion
/// style sampler, which is not yet reconciled to the real checkpoint — a reference clip is required for now.</summary>
public static class StyleTts2Model
{
    private const string Repo = "yl4579/StyleTTS2-LibriTTS";
    private const string CheckpointFile = "Models/LibriTTS/epochs_2nd_00020.pth";
    // StyleTTS2-LibriTTS was trained on American-English (espeak en-us) phonemes WITH punctuation preserved
    // (preserve_punctuation=True). Using British "en" gives wrong vowels and stripping punctuation makes the
    // prosody predictor slur across phrase boundaries, so match the training front-end exactly.
    private const string EspeakLanguage = "en-us";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string pth = await AudioModelCache.GetAsync(Repo, CheckpointFile, ct: ct).ConfigureAwait(false);
            StyleTts2Pipeline pipeline = StyleTts2Pipeline.LoadFromCheckpoint(pth);
            EspeakPhonemizer phonemizer = EspeakPhonemizer.FromCache(EspeakLanguage);
            Logs.Info("[AudioLab][StyleTTS2] Loaded yl4579/StyleTTS2-LibriTTS (StarGAN-v2 clone + HiFiGAN, 24 kHz).");

            // Preserve punctuation (prosodic phrasing) and normalise whitespace, matching the training front-end.
            string Ipa(string s) =>
                string.Join(' ', phonemizer.PhonemizeToIpa(s, EspeakLanguage, preservePunctuation: true)
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

            return new TtsRunner(24_000, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new NotSupportedException(
                        "[AudioLab][StyleTTS2] Supply a voice-reference clip — StyleTTS2 clones its speaker. "
                        + "Random/unconditional synthesis (no reference) needs the diffusion style sampler, which "
                        + "is not yet reconciled to the real checkpoint.");
                }
                float speed = (float)(req.Speed ?? 1.0);
                return pipeline.SynthesizeCloneFromAudio(backend, Ipa(req.Text), req.ReferenceMono24k, 24_000, speed);
            }, pipeline);
        },
    };
}
