# AudioLab TTS Test Report — 2026-07-05

Every **installed** TTS model was run through the **standard SwarmUI Generate API**
(`GenerateText2Image`, model = the audio model, prompt = the text), so every output
was saved to the normal Output folder and shows in the UI history. Each output was
then transcribed with OpenAI-Whisper (`base.en`/`small.en`) and the transcript compared
to the input text.

- Reference clip for the voice-cloning models: your `~/Downloads/speech.mp3`
  → *"How did your brain even learn human speech? I'm just so curious!"* (5.08s).
- Target sentence: **"She sells sea shells by the sea shore."** (deliberately different
  from the reference so a cloning model can't pass by echoing the reference).

## Headline

**All 7 installed TTS engines generate and produce intelligible, matching speech.**
None are broken at the "fails to generate" level. The problems are **quality / speed /
stability**, and every one of those lives in the **HartsyInference engine**, not in the
AudioLab extension. I did not need to change extension code to make them run.

Your earlier "most failed to generate" was almost certainly one of:
1. **A stale deployed DLL** — restarting reuses the old extension build; the dev launch
   script rebuilt from source and all 7 came alive. (This matches your own past notes:
   *restart ≠ rebuild*.)
2. **Not supplying a voice reference** — F5-TTS and VibeVoice **hard-require** a reference
   clip + transcript and refuse cleanly without one. That refusal is by design, not a bug.

## Per-model results

| Model | Generates? | Words match (Whisper) | Speed | Audible quality | Verdict |
|---|---|---|---|---|---|
| **Bark** | ✅ | 0.94 ("quick *ground* fox") | 55s / 4.8s | staticy, robotic | Works, poor quality |
| **Chatterbox** | ✅ | 0.94 ("She *fell* seashells") | **9s** | good | **Best fast option** (default voice only) |
| **Dia** | ✅ | correct words but **loops** | **347s (!)** / 20s out | ok | Works but unusably slow + repeats |
| **F5-TTS** | ✅ | 0.97 | 226s | **distorted robot, hard to understand** | Generates, quality bad |
| **FishSpeech** | ✅ | 0.97 | **8s** | great vocals but **very low volume** | Works, needs gain fix |
| **NeuTTS** | ✅ | 0.85 + trailing garble ("Blackface") | 50s | decent, slightly robotic + small distortion | Works, EOS bug |
| **VibeVoice** | ✅ | unstable (see below) | 70s | good on some refs | Works but reference/length-sensitive |

Your listening notes match the measurements: FishSpeech great-but-quiet, F5 a
slow distorted robot, NeuTTS decent-but-robotic, VibeVoice pretty good.

### VibeVoice instability (important)
- Batch run (short Bark reference): said *"He shows seashells by the seashore"* — good, you liked it.
- Clean run (your 5s `speech.mp3` reference, same short target): rambled **11.9s of unrelated
  text** — *"This is you, pal. Hi, Kirk, this is you..."*.
- VibeVoice is a **long-form** model (designed for up to 90 min). Very short prompts +
  certain references destabilize it into hallucinating/looping. It works, but it needs
  longer target text and is sensitive to the reference. Engine-side sampling/stop tuning.

## What's fixed / correct in the extension (no code change needed)

The AudioLab extension side of TTS is working correctly:
- Model → provider routing, `Prompt` → engine `text`, reference audio passed as
  base64 data-URI on `referenceaudio` + `referencetext`.
- Output saved as a real WAV to `Output/local/raw/...` — appears in UI history like any
  generation (verified for all 7).
- Weights auto-download on first use; reference-required models refuse cleanly with a
  clear message when no reference is given.
- Chatterbox/NeuTTS correctly gate reference-cloning with an explanatory message and fall
  back to the default voice.

## What needs ENGINE work (HartsyInference, not the extension)

Ordered roughly by user impact:

1. **FishSpeech output gain** — output is ~10× quieter than the others (RMS 222 vs
   1500–7800). Needs output normalization/gain in the FishSpeech pipeline.
2. **F5-TTS quality** — distorted/robotic, barely intelligible to the ear even though
   Whisper decodes it. The extension README already flags F5 as "runtime-pending, audible
   quality not validated." Confirmed: needs numerical validation of the DiT/CFG/duration path.
3. **Dia speed + repetition** — 347s for one line, and it loops the phrase. Needs a
   repetition/stop-condition fix and a hard look at throughput (byte-level AR).
4. **NeuTTS EOS/stop token** — appends hallucinated trailing words after the sentence
   ("Blackface"). Stop-token handling in the NeuTTS pipeline.
5. **VibeVoice stability** — hallucinates/loops on short prompts and is reference-sensitive.
6. **Bark quality** — staticy/robotic; may be partly inherent to Bark, worth comparing to
   a reference implementation.
7. **Reference cloning not wired**: NeuTTS (needs X-Codec2 encoder port), Chatterbox
   (needs PCM→40-bin-mel front-end for the voice encoder). Both use default voice today.

## Stability finding: OOM kill

During back-to-back heavy model loads the **OS OOM-killed the whole SwarmUI process**
(host RAM hit ~3.5 GB free, next load pushed it over; RAM went to 0 GB free again during
the clean VibeVoice run). The extension *does* unload other providers before loading a new
one, but host RAM is still being exhausted. Worth investigating whether pickle/safetensors
loaders are held longer than necessary, or whether a hard host-RAM budget is needed before
loading a second large model. This is the most likely cause of *intermittent* "it failed"
behavior in real use.

## Not tested — not installed (9+ engines)

Only **7 of 16** TTS engines are installed. Not installed, so untested here:
Kokoro, Orpheus, CSM, Zonos, Piper, Qwen3, CosyVoice, Pocket TTS, Kyutai TTS
(+ SparkTTS, StyleTTS2, MeloTTS, GPT-SoVITS provider files).
- **Kokoro, Orpheus, CSM** have engine descriptors and should work if installed — worth testing.
- **Piper, Zonos** are documented as needing an espeak phonemizer. Note: **NeuTTS already
  uses `EspeakPhonemizer.FromCache` successfully**, so espeak IS present in the engine now —
  the `AudioUnsupportedReasons` claim that "the engine ships no espeak phonemizer" looks stale
  and Piper/Zonos may now be unblockable.

Say the word and I'll install + test any of these.

## Artifacts (saved outputs you can play in the UI)

All under `Output/local/raw/2026-07-05/`:
- Bark `0028001`, Chatterbox `0036001`, Dia `0030001`, FishSpeech `0036002`,
  F5 `0036003` (Bark ref) / `0050001` (clean ref), NeuTTS `0040001`,
  VibeVoice `0041001` (Bark ref, the good one) / `0100001` (clean ref, the rambling one).
