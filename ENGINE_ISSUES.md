# Engine issues: removing the non-Hartsy downloads

Snapshot 2026-10-20. Read from HartsyInference origin/main (e845dcd9) and the live Hartsy cards. Paths are relative to the audio models root (`{Models}/audio`) unless marked otherwise.

- **[checked]** = confirmed by a request or a hash against the live data in this session.
- **[source]** = read from the engine source (file:line given). Not run.

## A. Engine changes that remove external downloads

1. **Local files are probed after the network.** Every load first asks Hugging Face for `model.safetensors` and then for `model.safetensors.index.json`, and only then reads a local `pytorch_model.bin`. A 404 falls through; offline, a 5xx, a stall or a 401/403 stops the load. Affects Bark, MusicGen small/medium, Orpheus SNAC and Kyutai STT. [checked: `model.safetensors` returns 404 on facebook/musicgen-medium, suno/bark and hubertsiuzdak/snac_24khz.] [source: AudioCheckpoints.cs:19-24, 31, 48, 53; AudioModelCache.cs:287-305; BarkTtsModel.cs:38, 81-86; MusicCatalog.cs:66, 233-235; OrpheusTtsModel.cs:34; SttCatalog.cs:174.] **Fix:** check every local candidate before any request.

2. **SheetSage2 prefetch looks in the wrong folder.** `PrefetchSttAsync` writes to `stt/`, but the loader reads `music/`. An install therefore downloads the card's file again from Comfy-Org/YuE2 on Hugging Face. [source: ModelPrefetchService.cs:113 vs SheetSage2Pipeline.cs:63.] **Fix:** use the loader's category.

3. **espeak-ng data comes from a PyPI wheel.** Used by Kokoro, Piper, ZipVoice, StyleTTS2, Zonos and NeuTTS. [source: EspeakDataInstaller.cs:15, files.pythonhosted.org.] **Fix:** publish the data as a Hartsy card and point the installer at it.

4. **WeText text-normalisation grammars come from a PyPI wheel.** Used by IndexTTS 1.5, IndexTTS 2 and CosyVoice. The class comment also names ModelScope `pengzhendong/wetext`. [source: src/HartsyInference.Audio/Frontends/WeText/*.cs:12, 16.] **Fix:** publish as a Hartsy card.

5. **Kokoro front-end data comes from GitHub and S3.** CMUdict and the misaki data come from raw.githubusercontent.com; unidic-3.1.0 comes from an S3 bucket (cotonoha-dic). [source: KokoroFrontend.cs:18, 21; KokoroJapaneseAssets.cs:18, 21.] **Fix:** publish as one or more Hartsy cards.

6. **Piper reads one file pair per voice from rhasspy/piper-voices.** Layout `{lang}/{locale}/{name}/{quality}/{voice}.onnx` plus `.onnx.json`. [source: PiperPipeline.cs:87-95, 138-148.] **Fix:** one card per voice, or a loader that reads a single card file.

7. **Demucs falls back to Meta's CDN.** [source: FxCatalog.cs:60-66.] **Fix:** remove the fallback once the card is the only source.

8. **Voice sessions load `openai/whisper-small.en`.** It is not a catalog row, so it has no card, and it is a different checkpoint from `whisper-small`. [source: AudioLab VoiceEngineModels.cs:231.] **Fix:** publish it as a card, or change the voice path.

9. **RVC converts two HF checkpoints at runtime.** `contentvec` (lengyue233/content-vec-best) and `rmvpe` (lj1995/VoiceConversionWebUI) are converted to `.safetensors` on first use. [source: VcCatalog.cs:98-101, 127-153.] **Fix:** publish the converted files and stand them in at the names the loader reads.

10. **Integrity is not checked on present files.** A present file is never hashed against its pin, so a wrong stand-in loads silently. [source: ModelDownloader.cs:90-91; AudioWeightsCatalog.cs:20.] **Fix:** verify the pinned hash when a file is used or first linked.

## B. Hartsy uploads and links still needed (no engine change)

The engine already reads these paths. Each row needs the file on its card, declared with `hartsy.stands_in_for`, or linked from another card.

| Card | Missing from Hartsy | Notes |
|---|---|---|
| Whisper Large v3, Turbo | Their own `config.json`, `vocab.json`, `added_tokens.json`, `generation_config.json` | [checked] Both still link Whisper Base's sidecars. v3 has vocab_size 51866 against Base's 51865. **Broken until relinked.** |
| Kyutai STT 2.6B, 1B | SentencePiece tokenizers | Read from `stt/kyutai--stt-2.6b-en/` and `stt/kyutai--stt-1b-en_fr/`, not from the `-trfs` folder. |
| Bark | `google-bert/bert-base-multilingual-cased` `vocab.txt` | Read from `tts/google-bert--bert-base-multilingual-cased/`. |
| CosyVoice 2 | `CosyVoice-BlankEN/vocab.json`, `merges.txt` | Under `tts/FunAudioLLM--CosyVoice2-0.5B/`. |
| MiniMax Music 3 | `tokenizer/tokenizer.json` | Under `music/MiniMaxAI--MiniMax-Music3/tokenizer/`. |
| Pocket TTS | `languages/english/tokenizer.model` | |
| IndexTTS 1.5 | `bpe.model`, `bigvgan_generator.pth` | |
| IndexTTS 2.0 / 2.5 | `w2v-bert-2.0`, CAM++ (`campplus_cn_common.bin`), BigVGAN 22 kHz, MaskGCT semantic codec (2.0), `s2mel.pth`, `wav2vec2bert_stats.pt`, `feat1.pt`, `feat2.pt`, `codec.pth` (2.5), tokenizer | Nothing is on the cards yet. |
| Fish Speech 1.4 | `tokenizer.json` | |
| Fish Audio S2 Pro | `tokenizer.json` | Under `tts/fishaudio--s2-pro/`. |
| Breeze TTS 2 | `tokenizer.json` (LLM) | Under `tts/BreezeBlue--Breeze-TTS-2/`. |
| AuK base, flash | Qwen2.5-Omni-3B encoder and its `tokenizer.json` | |
| AuK flash | `tts/tencent--AuK/vae.safetensors` | Already on the base card. Link it to flash. |
| ControlFoley | `ext_weights/v1-44.pth` (see C), BigVGAN 44 kHz, DFN5B CLIP | |
| ACE-Step 1.5 SFT | Silence latent, VAE, text encoder (link from the other ACE cards), `config.json` | SFT has no companions. |
| YuE (4 cards) | X-Codec, declared at `music/YuE/xcodec.safetensors` | Now HF HartsyAI only. |
| Stable Audio Open Small | `music/google-t5--t5-base/model.safetensors` | Link from the MusicGen cards. |
| ZipVoice | `tts/lucasnewman--vocos-mel-24khz/model.safetensors` | Link from F5-TTS. |
| Zonos | Dia codec (`tts/descript--descript-audio-codec/weights.pth`) | Link from Dia. |
| CosyVoice 2 | `tts/ResembleAI--chatterbox/s3gen.safetensors` | Link from Chatterbox. |
| Kyutai TTS | Default voice `tts/kyutai--tts-voices/...expresso...` | |
| Piper | Open access (currently starter-gated), per-voice cards | See A6. |
| Fish Audio Drama 3 (native, int4), RVC | No card on the public API yet | Confirm they are published. |

## C. Converted files the engine cannot load as-is

1. **ControlFoley's `v1-44.pth` reader rejects safetensors.** [source: ControlFoleyAudioDecoder.cs:112-114.] **Fix:** accept safetensors there, or keep the upload as `.pth`.
2. **Dia codec.** It loads only if its keys and shapes match the canonical descript `.pth`. The engine says HF safetensors mirrors are reshaped and would fail. [source: DiaTtsModel.cs:43.] **Not verified** against our file. Check it, or redo the conversion from the canonical `.pth`.
3. **Piper card.** One bare `.onnx`, no `.onnx.json`, and gated to the starter tier. The installer cannot use it.

## D. Verified without an engine change

Safetensors at a `.pt`, `.pth`, `.bin` or `.ckpt` path loads when the reader sniffs content. [source: AnyFormatCheckpointLoader.cs:24, 49-59; PytorchPickleLoader.cs:42-44.] Confirmed for: the ACE-Step silence latent at the lowercase path (one `[1,64,N]` tensor), Kokoro voice packs (converted to `.bin` on load), Chatterbox conditioning [checked: flattened keys `t3.*` and `gen.*`], IndexTTS 1.5 `gpt.pth`, the Fish Speech 1.5 pair, Demucs `.th`, Orpheus SNAC, MusicGen and AudioGen, Bark, and Resemble Enhance (key prefixes). Key-level checks against each loader are still open.

## E. AudioLab-side links (not engine work)

- `AudioWeightsRegistry`: ACE-Step and YuE URLs on Hugging Face, the wake-word files on GitHub, and the score soundfont on GitHub. These stay as fallbacks when a card fails to install.
- The README table and the `source_url` values still link to Hugging Face. They are used for presence checks and author metadata, so they need a separate change.

## Scope

The engine is pinned to 2.0.0-alpha.382 in the extension. Source here is origin/main e845dcd9, which may be newer. The loader behaviour was read, not run. No model was generated.
