using System;
using SwarmUI.Utils;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>MeloTTS English-v3 (VITS + multi-stream text encoder) — 44.1 kHz. Provider id <c>melotts_tts</c>.
/// Self-contained: the engine <see cref="MeloTts"/> facade bundles the CMUdict g2p (phoneme + tone + language
/// streams) and the bert-base-uncased prosody BERT front-end, so no external phonemizer/BERT wiring is needed.
/// Validated e2e in the engine (audio corr 0.9993). Not zero-shot — no voice reference required. Weights
/// (<c>myshell-ai/MeloTTS-English-v3</c> + <c>bert-base-uncased</c> + its <c>vocab.txt</c>) auto-download.</summary>
public static class MeloTtsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "myshell-ai/MeloTTS-English-v3",
        LoadAsync = async (_, ct) =>
        {
            MeloTts melo = await MeloTts.LoadAsync(ct: ct).ConfigureAwait(false);
            Logs.Info("[AudioLab][MeloTTS] Loaded myshell-ai/MeloTTS-English-v3 (VITS + CMUdict g2p + bert-base-uncased prosody BERT, 44.1 kHz).");
            return new TtsRunner(melo.SampleRate,
                (backend, req) => melo.SynthesizeText(backend, req.Text, seed: req.Seed),
                melo);
        },
    };
}
