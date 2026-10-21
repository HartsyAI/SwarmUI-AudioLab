# AudioLab models vs. Hartsy repacks

Snapshot 2026-10-20. Hartsy read from the public API (all 118 families, every version) and from each audio card's download manifest (`/download/{id}/manifest`) and file headers. What the engine reads comes from the HartsyInference source. AudioLab rows come from `AudioProviders/*.cs`.

## How AudioLab uses Hartsy (PR `hartsy/repack-downloads`)

- `AudioServices/HartsyRepacks.cs` maps every local model to its Hartsy card. A unit test fails if a local model has neither a card nor a `PendingUpload` entry.
- Install, and the first generation of a model that is not on disk yet, download the card: weights into `{Models}/audio/{category}/{Prefix}/hartsy/`, configs and tokenizers beside the upstream files the weights stand in for. The engine then hard-links each repack into the upstream path it names in `hartsy.stands_in_for`.
- Whatever a card lacks is still fetched upstream by the engine, so a card with gaps installs and runs; the gaps below are what keeps it from being Hartsy-only.
- The model's link in the install dialog points at its Hartsy card.

## Summary

- 116 rows: 92 local, 24 API-only.
- Complete on Hartsy: 59. On Hartsy with gaps: 26. Broken sidecars: 2. Unusable card: 1. No card: 3. Not implemented: 1.
- New since 2026-10-16: the AuK base and flash cards, ControlFoley, IndexTTS 2.0 and 2.5, Fish Audio S2 Pro and NeuTTS Air. Still missing: Fish Audio Drama 3 (native, int4) and RVC, which are not on the public API yet.

## 1. Fix before merging

| Model | Card | Problem |
| --- | --- | --- |
| Whisper Large V3 (`whisper_stt/large-v3`) | [Whisper Large v3](https://hartsy.ai/Home?type=models&id=e9e78570-f4d9-4c66-a32b-46767e1cab86) | Links Whisper Base's config.json, vocab.json, added_tokens.json and generation_config.json. Large v3 has vocab_size 51866 (Base 51865) and 1,510 added-token ids shifted by one, so it would load with the wrong tokenizer. Upload openai/whisper-large-v3's sidecars and relink. |
| Whisper Turbo (`whisper_stt/turbo`) | [Whisper Turbo](https://hartsy.ai/Home?type=models&id=40cc4db8-2cc0-4e47-a162-8388e51ac554) | Same as Large v3: links Whisper Base's sidecars, but Turbo uses the v3 tokenizer (vocab_size 51866). Upload openai/whisper-large-v3-turbo's sidecars and relink. |

## 2. No card yet (for the upload agent)

| Provider | Model | License | Size | What the engine needs |
| --- | --- | --- | --- | --- |
| Fish Audio Drama 3 TTS | Fish Audio Drama 3 (`native`) | Fish Audio Community License v1.0 (commercial use below USD 500k annual revenue) | ~38GB | fishaudio/drama-3 (native) or fishaudio/drama-3-int4: config.json (read), tokenizer.json, the weight_map shards; codec.pth (falls back to S2 Pro's). |
| Fish Audio Drama 3 TTS | Fish Audio Drama 3 (int4) (`int4`) | Fish Audio Community License v1.0 (commercial use below USD 500k annual revenue) | ~21GB | fishaudio/drama-3 (native) or fishaudio/drama-3-int4: config.json (read), tokenizer.json, the weight_map shards; codec.pth (falls back to S2 Pro's). |
| RVC Voice Conversion | RVC V2 (`v2`) | MIT | ~500MB | Voice models are the user's own. Shared assets: contentvec (lengyue233/content-vec-best) and rmvpe (lj1995/VoiceConversionWebUI rmvpe.pt). |

## 3. On Hartsy, with gaps

The card installs and the model runs, but the engine still fetches these files elsewhere.

| Model | Card | Still fetched elsewhere | Action |
| --- | --- | --- | --- |
| ACE-Step 1.5 SFT (`acestep_music/sft`) | [ACE-Step 1.5 SFT](https://hartsy.ai/Home?type=models&id=d2c6986f-682b-47f4-b551-0079decf39b9) | acestep-v15-sft.config.json; silence latent, VAE and text encoder (linked to the other 8 ACE-Step cards, not this one) | Link the shared ACE-Step latent, VAE and text encoder; add the SFT config. |
| AuK-Flash (`auk_tts/flash`) | [AuK (Flash)](https://hartsy.ai/Home?type=models&id=0a0b69f6-faa6-494b-96ec-868167605ece) | tts/tencent--AuK/vae.safetensors (on the base card); Qwen/Qwen2.5-Omni-3B text encoder (HF); its tokenizer.json (HF) | Link the base card's VAE; upload the encoder and tokenizer. |
| AuK (`auk_tts/base`) | [AuK](https://hartsy.ai/Home?type=models&id=cc7d5cc0-66a2-46e3-b651-ede3f74a0b95) | Qwen/Qwen2.5-Omni-3B text encoder (HF); its tokenizer.json (HF) | Upload the encoder and its tokenizer as companions. |
| Bark TTS (`bark_tts/default`) | [Bark](https://hartsy.ai/Home?type=models&id=2654d7dd-c285-4229-9b44-045f66693728) | tts/google-bert--bert-base-multilingual-cased/vocab.txt | Add the multilingual BERT vocab.txt as a companion. |
| Breeze TTS 2 (`breeze_tts/breeze-tts-2`) | [Breeze TTS 2](https://hartsy.ai/Home?type=models&id=9ada5af5-85b0-4654-b5b6-02f94050697b) | tts/BreezeBlue--Breeze-TTS-2/tokenizer.json | Add tokenizer.json as a companion. |
| ControlFoley (`controlfoley_sfx/large-44k`) | [ControlFoley](https://hartsy.ai/Home?type=models&id=f80d2ca1-0217-4ec8-89ec-d5cface9732a) | ext_weights/v1-44.pth (not on the card); nvidia/bigvgan_v2_44khz_128band_512x (HF); apple/DFN5B-CLIP-ViT-H-14-384 (HF) | Upload the three companions. |
| CosyVoice2 0.5B (`cosyvoice_tts/2-0.5b`) | [CosyVoice 2](https://hartsy.ai/Home?type=models&id=59756559-fe4c-40b1-87ce-43f1554a42dc) | tts/ResembleAI--chatterbox/s3gen.safetensors (the Chatterbox card's codec); tts/FunAudioLLM--CosyVoice2-0.5B/CosyVoice-BlankEN/vocab.json and merges.txt; WeText grammars under the audio root (shared asset) | Link the Chatterbox s3gen companion; add CosyVoice-BlankEN/vocab.json and merges.txt. |
| Fish Audio S2 Pro (`fishaudio_tts/s2-pro`) | [Fish Audio S2 Pro](https://hartsy.ai/Home?type=models&id=753dfce5-3a61-467f-945b-60adb1607d82) | tts/fishaudio--s2-pro/tokenizer.json (HF) | Upload tokenizer.json as a companion. |
| Fish Speech 1.4 (`fishspeech_tts/fish-speech-1.4`) | [Fish Speech 1.5 (1.4)](https://hartsy.ai/Home?type=models&id=b5539bc0-672c-478e-84c3-3b0a939a4d9a) | tts/fishaudio--fish-speech-1.4/tokenizer.json | Add tokenizer.json as a companion. |
| IndexTTS-2.0 (`indextts2_tts/v2_0`) | [IndexTTS 2](https://hartsy.ai/Home?type=models&id=06498fe8-0dd3-4d21-83a5-9d41fdb951d8) | w2v-bert-2.0, CAM++, BigVGAN 22 kHz and the MaskGCT codec (HF); bpe.model, wav2vec2bert_stats.pt, feat1.pt, feat2.pt, s2mel.pth (HF) | Upload the companions. |
| IndexTTS-2.5 (`indextts2_tts/v2_5`) | [IndexTTS 2 (2.5)](https://hartsy.ai/Home?type=models&id=4f66d8cf-8493-48c6-b86d-5d735bd35be7) | w2v-bert-2.0, CAM++ and BigVGAN 22 kHz (HF); tiktoken tokenizer, wav2vec2bert_stats.pt, feat1.pt, feat2.pt, s2mel.pth, codec.pth (HF) | Upload the companions. |
| IndexTTS-1.5 (`indextts_tts/v1_5`) | [IndexTTS 1.5](https://hartsy.ai/Home?type=models&id=56ad3c7b-6ae1-4881-afe6-2934c2522cdc) | tts/IndexTeam--IndexTTS-1.5/bpe.model; bigvgan_generator.pth; config.yaml (fetched, not parsed); WeText grammars under the audio root (shared asset) | Add bigvgan_generator (as safetensors), bpe.model and config.yaml. The card has gpt.pth only. |
| Kyutai STT 1B (English + French) (`kyutaistt_stt/1b-en-fr`) | [Kyutai STT 1B English/French](https://hartsy.ai/Home?type=models&id=3809b06f-980f-4b99-a717-372c52192946) | stt/kyutai--stt-1b-en_fr/tokenizer_en_fr_audio_8000.model | Add the SentencePiece tokenizer as a companion. |
| Kyutai STT 2.6B (English) (`kyutaistt_stt/2.6b-en`) | [Kyutai STT 2.6B English](https://hartsy.ai/Home?type=models&id=0d6d85d0-82c9-4f02-9adc-d463a735479a) | stt/kyutai--stt-2.6b-en/tokenizer_en_audio_4000.model | Add the SentencePiece tokenizer as a companion. |
| Kyutai TTS 1.6B (`kyutaitts_tts/1.6b-en-fr`) | [Kyutai TTS 1.6B](https://hartsy.ai/Home?type=models&id=e81cc622-e0f9-4ee9-8cb9-daa3465de29f) | tts/kyutai--tts-voices/expresso/ex03-ex01_happy_001_channel1_334s.wav.1e68beda@240.safetensors (default voice, loaded at startup); other voices on demand | Add the default voice as a companion. |
| Pocket TTS (`pockettts_tts/default`) | [PocketTTS](https://hartsy.ai/Home?type=models&id=dd7cfcb2-4d63-4d9f-b07c-62562dbbc285) | tts/kyutai--pocket-tts-without-voice-cloning/languages/english/tokenizer.model | Add tokenizer.model as a companion. |
| Stable Audio Open Small (`stableaudio_music/open-small`) | [Stable Audio Open Small](https://hartsy.ai/Home?type=models&id=ee956408-1719-4251-bad9-6fde866ae4b1) | music/google-t5--t5-base/model.safetensors (the MusicGen cards' text encoder) | Link the MusicGen text-encoder companion. |
| YuE English (Chain-of-Thought) (`yue_music/en-cot`) | [YuE 7B English CoT](https://hartsy.ai/Home?type=models&id=3b0241fa-65fb-4d03-945a-3a67fa4004c5) | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) | Upload the X-Codec once and link it to all four YuE cards. |
| YuE English (In-Context Learning) (`yue_music/en-icl`) | [YuE 7B English ICL](https://hartsy.ai/Home?type=models&id=d35a6e57-e680-4e2e-9090-f32102d206d8) | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) | Upload the X-Codec once and link it to all four YuE cards. |
| YuE Chinese (Chain-of-Thought) (`yue_music/zh-cot`) | [YuE 7B Chinese CoT](https://hartsy.ai/Home?type=models&id=b939b731-6294-42b7-bf11-d8035bea2ffe) | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) | Upload the X-Codec once and link it to all four YuE cards. |
| YuE Chinese (In-Context Learning) (`yue_music/zh-icl`) | [YuE 7B Chinese ICL](https://hartsy.ai/Home?type=models&id=c1936296-e2dc-4a6c-ac76-0a4965875adb) | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) | Upload the X-Codec once and link it to all four YuE cards. |
| ZipVoice (`zipvoice_tts/base`) | [ZipVoice](https://hartsy.ai/Home?type=models&id=9533c7bf-774c-48e6-9387-b0308fc6b606) | tts/lucasnewman--vocos-mel-24khz/model.safetensors (the F5-TTS card's vocoder); espeak-ng data (shared asset) | Link the F5-TTS vocoder companion. |
| Zonos Transformer (`zonos_tts/transformer`) | [Zonos v0.1](https://hartsy.ai/Home?type=models&id=d0e9a4b9-aff4-42a6-9af3-5fe2a383ab0a) | tts/descript--descript-audio-codec/weights.pth (the Dia card's codec); espeak-ng data (shared asset) | Link the Dia codec companion. |
| MiniMax Music 3 (`minimax_music3/base`) | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | music/MiniMaxAI--MiniMax-Music3/tokenizer/tokenizer.json | Add tokenizer/tokenizer.json as a companion. |
| MiniMax Music 3 Q8 (`minimax_music3/q8`) | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | music/MiniMaxAI--MiniMax-Music3/tokenizer/tokenizer.json | Add tokenizer/tokenizer.json as a companion. |
| MiniMax Music 3 Q4 (`minimax_music3/q4`) | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | music/MiniMaxAI--MiniMax-Music3/tokenizer/tokenizer.json | Add tokenizer/tokenizer.json as a companion. |

## 4. Unusable card

- **Piper TTS** ([Piper TTS (en_US lessac medium)](https://hartsy.ai/Home?type=models&id=da49c57a-7d01-4f5a-9a11-ec60e2b3ba5d)): The card is one voice (en_US lessac medium) as a bare .onnx: no stand-in path, no .onnx.json, and it is gated to the starter tier. The engine needs {voice}.onnx and {voice}.onnx.json per voice under tts/rhasspy--piper-voices/{lang}/{locale}/{name}/{quality}/. Make it open and ship each voice's two files, or keep Piper on upstream.

## 5. Link-only fixes (no upload)

- ACE-Step 1.5 SFT: link the shared latent, VAE and text encoder (it has no companions); its config still needs uploading.
- CosyVoice 2: link the Chatterbox card's `chatterbox-codec-s3gen_fp32.safetensors`.
- ZipVoice: link the F5-TTS card's `f5-vocoder_fp32.safetensors`.
- Zonos: link the Dia card's `dia-1_6b-codec_fp32.safetensors`.
- Stable Audio Open Small: link the MusicGen cards' `musicgen-text-encoder_fp32.safetensors`.
- These cross families, so they need the admin `allFamilies` linkable list.

## 6. Shared assets not on any card

- espeak-ng data (Kokoro installs it; Piper, ZipVoice, StyleTTS2, Zonos and NeuTTS read it).
- Kokoro front-end dictionaries: misaki json files, cmudict, unidic-3.1.0, kokoro_zh.
- WeText grammars (IndexTTS, CosyVoice).
- `openai/whisper-small.en`, which voice sessions load (not a catalog row).
- Wake word: silero_vad.onnx, melspectrogram.onnx, embedding_model.onnx, hey_jarvis, RNNoise.
- RVC: contentvec and rmvpe.

## 7. Full inventory

| Provider | Model | Status | Hartsy card | Main file | Companions | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Whisper STT | Whisper Large V3 (`large-v3`) | Broken | [Whisper Large v3](https://hartsy.ai/Home?type=models&id=e9e78570-f4d9-4c66-a32b-46767e1cab86) | `whisper-large-v3_fp16.safetensors` 3.09 GB | 6 | Links Whisper Base's config.json, vocab.json, added_tokens.json and generation_config.json. Large v3 has vocab_size 51866 (Base 51865) and 1,510 added-token ids shifted by one, so it would load with the wrong tokenizer. Upload openai/whisper-large-v3's sidecars and relink. |
| Whisper STT | Whisper Turbo (`turbo`) | Broken | [Whisper Turbo](https://hartsy.ai/Home?type=models&id=40cc4db8-2cc0-4e47-a162-8388e51ac554) | `whisper-turbo_fp16.safetensors` 1.62 GB | 6 | Same as Large v3: links Whisper Base's sidecars, but Turbo uses the v3 tokenizer (vocab_size 51866). Upload openai/whisper-large-v3-turbo's sidecars and relink. |
| Piper TTS | Piper TTS (`default`) | Unusable | [Piper TTS (en_US lessac medium)](https://hartsy.ai/Home?type=models&id=da49c57a-7d01-4f5a-9a11-ec60e2b3ba5d) | `en_US-lessac-medium.onnx` 63 MB |  | The card is one voice (en_US lessac medium) as a bare .onnx: no stand-in path, no .onnx.json, and it is gated to the starter tier. The engine needs {voice}.onnx and {voice}.onnx.json per voice under tts/rhasspy--piper-voices/{lang}/{locale}/{name}/{quality}/. Make it open and ship each voice's two files, or keep Piper on upstream. |
| Fish Audio Drama 3 TTS | Fish Audio Drama 3 (int4) (`int4`) | Missing |  |  |  | fishaudio/drama-3 (native) or fishaudio/drama-3-int4: config.json (read), tokenizer.json, the weight_map shards; codec.pth (falls back to S2 Pro's). |
| Fish Audio Drama 3 TTS | Fish Audio Drama 3 (`native`) | Missing |  |  |  | fishaudio/drama-3 (native) or fishaudio/drama-3-int4: config.json (read), tokenizer.json, the weight_map shards; codec.pth (falls back to S2 Pro's). |
| RVC Voice Conversion | RVC V2 (`v2`) | Missing |  |  |  | Voice models are the user's own. Shared assets: contentvec (lengyue233/content-vec-best) and rmvpe (lj1995/VoiceConversionWebUI rmvpe.pt). |
| ACE-Step Music | ACE-Step 1.5 SFT (`sft`) | Gaps | [ACE-Step 1.5 SFT](https://hartsy.ai/Home?type=models&id=d2c6986f-682b-47f4-b551-0079decf39b9) | `acestep-sft_bf16.safetensors` 4.79 GB |  | acestep-v15-sft.config.json; silence latent, VAE and text encoder (linked to the other 8 ACE-Step cards, not this one) |
| AuK | AuK (`base`) | Gaps | [AuK](https://hartsy.ai/Home?type=models&id=cc7d5cc0-66a2-46e3-b651-ede3f74a0b95) | `auk_base.safetensors` 4.20 GB | 1 | Qwen/Qwen2.5-Omni-3B text encoder (HF); its tokenizer.json (HF) |
| AuK | AuK-Flash (`flash`) | Gaps | [AuK (Flash)](https://hartsy.ai/Home?type=models&id=0a0b69f6-faa6-494b-96ec-868167605ece) | `auk_flash.safetensors` 4.20 GB |  | tts/tencent--AuK/vae.safetensors (on the base card); Qwen/Qwen2.5-Omni-3B text encoder (HF); its tokenizer.json (HF) |
| Bark TTS | Bark TTS (`default`) | Gaps | [Bark](https://hartsy.ai/Home?type=models&id=2654d7dd-c285-4229-9b44-045f66693728) | `bark_fp32.safetensors` 4.52 GB |  | tts/google-bert--bert-base-multilingual-cased/vocab.txt |
| Breeze TTS 2 | Breeze TTS 2 (`breeze-tts-2`) | Gaps | [Breeze TTS 2](https://hartsy.ai/Home?type=models&id=9ada5af5-85b0-4654-b5b6-02f94050697b) | `breeze-breeze-tts-2_bf16.safetensors` 6.97 GB | 1 | tts/BreezeBlue--Breeze-TTS-2/tokenizer.json |
| ControlFoley | ControlFoley (`large-44k`) | Gaps | [ControlFoley](https://hartsy.ai/Home?type=models&id=f80d2ca1-0217-4ec8-89ec-d5cface9732a) | `controlfoley-large-44k_bf16.safetensors` 5.65 GB |  | ext_weights/v1-44.pth (not on the card); nvidia/bigvgan_v2_44khz_128band_512x (HF); apple/DFN5B-CLIP-ViT-H-14-384 (HF) |
| CosyVoice TTS | CosyVoice2 0.5B (`2-0.5b`) | Gaps | [CosyVoice 2](https://hartsy.ai/Home?type=models&id=59756559-fe4c-40b1-87ce-43f1554a42dc) | `cosyvoice-2-0_5b_fp32.safetensors` 2.57 GB | 2 | tts/ResembleAI--chatterbox/s3gen.safetensors (the Chatterbox card's codec); tts/FunAudioLLM--CosyVoice2-0.5B/CosyVoice-BlankEN/vocab.json and merges.txt; WeText grammars under the audio root (shared asset) |
| Fish Audio S2 TTS | Fish Audio S2 Pro (`s2-pro`) | Gaps | [Fish Audio S2 Pro](https://hartsy.ai/Home?type=models&id=753dfce5-3a61-467f-945b-60adb1607d82) | `fishaudio-s2-pro_bf16.safetensors` 9.12 GB | 1 | tts/fishaudio--s2-pro/tokenizer.json (HF) |
| Fish Speech TTS | Fish Speech 1.4 (`fish-speech-1.4`) | Gaps | [Fish Speech 1.5 (1.4)](https://hartsy.ai/Home?type=models&id=b5539bc0-672c-478e-84c3-3b0a939a4d9a) | `fishspeech-fish-speech-1_4_bf16.safetensors` 989 MB | 1 | tts/fishaudio--fish-speech-1.4/tokenizer.json |
| IndexTTS | IndexTTS-1.5 (`v1_5`) | Gaps | [IndexTTS 1.5](https://hartsy.ai/Home?type=models&id=56ad3c7b-6ae1-4881-afe6-2934c2522cdc) | `indextts-v1-5_bf16.safetensors` 1.17 GB |  | tts/IndexTeam--IndexTTS-1.5/bpe.model; bigvgan_generator.pth; config.yaml (fetched, not parsed); WeText grammars under the audio root (shared asset) |
| IndexTTS-2 | IndexTTS-2.0 (`v2_0`) | Gaps | [IndexTTS 2](https://hartsy.ai/Home?type=models&id=06498fe8-0dd3-4d21-83a5-9d41fdb951d8) | `indextts2-v2-0_fp32.safetensors` 3.48 GB |  | w2v-bert-2.0, CAM++, BigVGAN 22 kHz and the MaskGCT codec (HF); bpe.model, wav2vec2bert_stats.pt, feat1.pt, feat2.pt, s2mel.pth (HF) |
| IndexTTS-2 | IndexTTS-2.5 (`v2_5`) | Gaps | [IndexTTS 2 (2.5)](https://hartsy.ai/Home?type=models&id=4f66d8cf-8493-48c6-b86d-5d735bd35be7) | `indextts2-v2-5_fp32.safetensors` 3.26 GB |  | w2v-bert-2.0, CAM++ and BigVGAN 22 kHz (HF); tiktoken tokenizer, wav2vec2bert_stats.pt, feat1.pt, feat2.pt, s2mel.pth, codec.pth (HF) |
| Kyutai STT | Kyutai STT 1B (English + French) (`1b-en-fr`) | Gaps | [Kyutai STT 1B English/French](https://hartsy.ai/Home?type=models&id=3809b06f-980f-4b99-a717-372c52192946) | `kyutaistt-1b-en-fr_bf16.safetensors` 2.70 GB |  | stt/kyutai--stt-1b-en_fr/tokenizer_en_fr_audio_8000.model |
| Kyutai STT | Kyutai STT 2.6B (English) (`2.6b-en`) | Gaps | [Kyutai STT 2.6B English](https://hartsy.ai/Home?type=models&id=0d6d85d0-82c9-4f02-9adc-d463a735479a) | `kyutaistt-2_6b-en_bf16-00001-of-00002.safetensors` 4.37 GB | 2 | stt/kyutai--stt-2.6b-en/tokenizer_en_audio_4000.model |
| Kyutai TTS | Kyutai TTS 1.6B (`1.6b-en-fr`) | Gaps | [Kyutai TTS 1.6B](https://hartsy.ai/Home?type=models&id=e81cc622-e0f9-4ee9-8cb9-daa3465de29f) | `kyutaitts-1_6b-en-fr_bf16.safetensors` 3.68 GB | 2 | tts/kyutai--tts-voices/expresso/ex03-ex01_happy_001_channel1_334s.wav.1e68beda@240.safetensors (default voice, loaded at startup); other voices on demand |
| MiniMax Music 3 | MiniMax Music 3 (`base`) | Gaps | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | `minimaxmusic3-base_fp32-00001-of-00002.safetensors` 4.96 GB | 16 | music/MiniMaxAI--MiniMax-Music3/tokenizer/tokenizer.json |
| MiniMax Music 3 | MiniMax Music 3 Q4 (`q4`) | Gaps | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | `minimaxmusic3-base_fp32-00001-of-00002.safetensors` 4.96 GB | 16 | Converted on-device from the base card; installs the base card. |
| MiniMax Music 3 | MiniMax Music 3 Q8 (`q8`) | Gaps | [MiniMax Music 3](https://hartsy.ai/Home?type=models&id=c97879c7-648d-4b1c-8ab0-1e1ca159ded1) | `minimaxmusic3-base_fp32-00001-of-00002.safetensors` 4.96 GB | 16 | Converted on-device from the base card; installs the base card. |
| Pocket TTS | Pocket TTS (`default`) | Gaps | [PocketTTS](https://hartsy.ai/Home?type=models&id=dd7cfcb2-4d63-4d9f-b07c-62562dbbc285) | `pockettts_bf16.safetensors` 219 MB | 27 | tts/kyutai--pocket-tts-without-voice-cloning/languages/english/tokenizer.model |
| Stable Audio Open Small | Stable Audio Open Small (`open-small`) | Gaps | [Stable Audio Open Small](https://hartsy.ai/Home?type=models&id=ee956408-1719-4251-bad9-6fde866ae4b1) | `stableaudio-open-small_fp32.safetensors` 1.36 GB | 2 | music/google-t5--t5-base/model.safetensors (the MusicGen cards' text encoder) |
| YuE Music | YuE English (Chain-of-Thought) (`en-cot`) | Gaps | [YuE 7B English CoT](https://hartsy.ai/Home?type=models&id=3b0241fa-65fb-4d03-945a-3a67fa4004c5) | `yue-en-cot_bf16-00001-of-00003.safetensors` 4.92 GB | 10 | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) |
| YuE Music | YuE English (In-Context Learning) (`en-icl`) | Gaps | [YuE 7B English ICL](https://hartsy.ai/Home?type=models&id=d35a6e57-e680-4e2e-9090-f32102d206d8) | `yue-en-icl_bf16-00001-of-00003.safetensors` 4.92 GB | 10 | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) |
| YuE Music | YuE Chinese (Chain-of-Thought) (`zh-cot`) | Gaps | [YuE 7B Chinese CoT](https://hartsy.ai/Home?type=models&id=b939b731-6294-42b7-bf11-d8035bea2ffe) | `yue-zh-cot_bf16-00001-of-00003.safetensors` 4.92 GB | 10 | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) |
| YuE Music | YuE Chinese (In-Context Learning) (`zh-icl`) | Gaps | [YuE 7B Chinese ICL](https://hartsy.ai/Home?type=models&id=c1936296-e2dc-4a6c-ac76-0a4965875adb) | `yue-zh-icl_bf16-00001-of-00003.safetensors` 4.92 GB | 10 | xcodec.safetensors (AudioLab fetches it from HF HartsyAI/YuE-xcodec-mini-safetensors) |
| ZipVoice | ZipVoice (`base`) | Gaps | [ZipVoice](https://hartsy.ai/Home?type=models&id=9533c7bf-774c-48e6-9387-b0308fc6b606) | `zipvoice-base_fp32.safetensors` 491 MB | 1 | tts/lucasnewman--vocos-mel-24khz/model.safetensors (the F5-TTS card's vocoder); espeak-ng data (shared asset) |
| Zonos TTS | Zonos Transformer (`transformer`) | Gaps | [Zonos v0.1](https://hartsy.ai/Home?type=models&id=d0e9a4b9-aff4-42a6-9af3-5fe2a383ab0a) | `zonos-transformer_bf16.safetensors` 3.25 GB | 2 | tts/descript--descript-audio-codec/weights.pth (the Dia card's codec); espeak-ng data (shared asset) |
| ACE-Step Music | ACE-Step 1.5 Base (`base`) | Complete | [ACE-Step 1.5 Base](https://hartsy.ai/Home?type=models&id=96c439b9-f47d-46f6-81a8-495937953dba) | `acestep-base_bf16.safetensors` 4.79 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 Turbo (`turbo`) | Complete | [ACE-Step 1.5 Turbo](https://hartsy.ai/Home?type=models&id=9e18f584-5100-4fbf-9bfd-404e33c623ab) | `acestep-turbo_bf16.safetensors` 4.79 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 Turbo Continuous (`turbo-continuous`) | Complete | [ACE-Step 1.5 Turbo Continuous](https://hartsy.ai/Home?type=models&id=8bcbe703-af3b-4196-ab36-d2a94561fe5d) | `acestep-turbo-continuous_bf16.safetensors` 4.79 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 Turbo Shift1 (`turbo-shift1`) | Complete | [ACE-Step 1.5 Turbo Shift 1](https://hartsy.ai/Home?type=models&id=a5f4bcbd-92b8-4101-9f3a-b013fd636c8c) | `acestep-turbo-shift1_bf16.safetensors` 4.79 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 Turbo Shift3 (`turbo-shift3`) | Complete | [ACE-Step 1.5 Turbo Shift 3](https://hartsy.ai/Home?type=models&id=98190d1e-f65e-44d0-a2db-e54ea12ab3d6) | `acestep-turbo-shift3_bf16.safetensors` 4.79 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 XL Base (`xl-base`) | Complete | [ACE-Step 1.5 XL Base](https://hartsy.ai/Home?type=models&id=b40fa58b-a2f1-42eb-94d3-69670de1aa95) | `acestep-xl-base_bf16.safetensors` 9.97 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 XL SFT (`xl-sft`) | Complete | [ACE-Step 1.5 XL SFT](https://hartsy.ai/Home?type=models&id=dc1e074b-53f3-4dc0-ae4a-75510f19da9d) | `acestep-xl-sft_bf16.safetensors` 9.97 GB | 4 |  |
| ACE-Step Music | ACE-Step 1.5 XL Turbo (`xl-turbo`) | Complete | [ACE-Step 1.5 XL Turbo](https://hartsy.ai/Home?type=models&id=5d1bf2c5-ea32-4578-b660-e6bce081cd1f) | `acestep-xl-turbo_bf16.safetensors` 9.97 GB | 4 |  |
| AudioGen SFX | AudioGen Medium (`medium`) | Complete | [AudioGen Medium](https://hartsy.ai/Home?type=models&id=015876a5-3218-41e1-9b54-26a9077f1870) | `audiogen-medium_fp16.safetensors` 3.68 GB | 2 |  |
| Chatterbox TTS | Chatterbox TTS (`default`) | Complete | [Chatterbox TTS](https://hartsy.ai/Home?type=models&id=09cbaa61-47a9-45ef-b760-eb087b891459) | `chatterbox_fp32.safetensors` 2.13 GB | 4 |  |
| CSM Conversational | CSM 1B (`1b`) | Complete | [CSM 1B](https://hartsy.ai/Home?type=models&id=3a20447a-358e-4c7c-a5b7-a1c96966d5fd) | `csm-1b_fp16.safetensors` 4.15 GB |  |  |
| Demucs Separation | HTDemucs (`htdemucs`) | Complete | [Demucs HTDemucs](https://hartsy.ai/Home?type=models&id=3f8afef7-3cb8-44ae-9525-b64dcc8640c0) | `demucs-htdemucs_fp16.safetensors` 84 MB |  |  |
| Demucs Separation | HTDemucs 6-Stem (`htdemucs_6s`) | Complete | [Demucs HTDemucs 6-Stem](https://hartsy.ai/Home?type=models&id=9e52e33b-2b81-4969-b20d-6adef56766d3) | `demucs-htdemucs-6s_fp16.safetensors` 55 MB |  |  |
| Dia TTS | Dia 1.6B (`1.6b`) | Complete | [Dia 1.6B](https://hartsy.ai/Home?type=models&id=1d0a4876-387b-4a8f-a2ca-33db0f9a40a4) | `dia-1_6b_fp32.safetensors` 6.44 GB | 1 |  |
| Distil-Whisper STT | Distil-Whisper Large V3 (`large-v3`) | Complete | [Distil-Whisper Large v3](https://hartsy.ai/Home?type=models&id=ab0eced5-4105-462e-b290-72856deae9df) | `distilwhisper-large-v3_fp16.safetensors` 1.51 GB | 6 |  |
| Distil-Whisper STT | Distil-Whisper Large V3.5 (`large-v3.5`) | Complete | [Distil-Whisper Large v3.5](https://hartsy.ai/Home?type=models&id=12de1d30-d060-4711-838e-1bba2bcf39b1) | `distilwhisper-large-v3_5_fp32.safetensors` 3.03 GB | 6 | Links Distil-Whisper v3's sidecars. Works: same vocab and vocab_size; generation_config differs only in forced_decoder_ids and language. Relink for accuracy. |
| F5-TTS | F5-TTS v1 Base (`v1-base`) | Complete | [F5-TTS](https://hartsy.ai/Home?type=models&id=304888f4-94f2-4826-b16a-870dc5a6f7ef) | `f5-v1-base_fp32.safetensors` 1.35 GB | 2 |  |
| Fish Speech TTS | Fish Speech 1.5 (`fish-speech-1.5`) | Complete | [Fish Speech 1.5](https://hartsy.ai/Home?type=models&id=d35bf127-febd-44a7-863d-548c64f1484d) | `fishspeech-fish-speech-1_5_bf16.safetensors` 1.28 GB | 3 |  |
| GPT-SoVITS | GPT-SoVITS Default (`default`) | Complete | [GPT-SoVITS](https://hartsy.ai/Home?type=models&id=9a5d0d0e-4afb-4fe1-9976-9a5ad6a8ac85) | `gptsovits_fp16.safetensors` 106 MB | 2 |  |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Base) (`3b-base`) | Complete | [HeartMuLa 3B](https://hartsy.ai/Home?type=models&id=275e03bb-1e73-425e-8481-c53c82ca5384) | `heartmula-3b-base_fp32-00001-of-00004.safetensors` 4.93 GB | 7 |  |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Base) Q4 (`3b-base-q4`) | Complete | [HeartMuLa 3B](https://hartsy.ai/Home?type=models&id=275e03bb-1e73-425e-8481-c53c82ca5384) | `heartmula-3b-base_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Base) Q8 (`3b-base-q8`) | Complete | [HeartMuLa 3B](https://hartsy.ai/Home?type=models&id=275e03bb-1e73-425e-8481-c53c82ca5384) | `heartmula-3b-base_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Happy New Year) (`3b-hny`) | Complete | [HeartMuLa 3B Happy New Year](https://hartsy.ai/Home?type=models&id=cc752ffd-7d73-4611-b047-cac510f4e1b7) | `heartmula-3b-hny_fp32-00001-of-00004.safetensors` 4.93 GB | 7 |  |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Happy New Year) Q4 (`3b-hny-q4`) | Complete | [HeartMuLa 3B Happy New Year](https://hartsy.ai/Home?type=models&id=cc752ffd-7d73-4611-b047-cac510f4e1b7) | `heartmula-3b-hny_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| HeartLib (HeartMuLa) | HeartMuLa 3B (Happy New Year) Q8 (`3b-hny-q8`) | Complete | [HeartMuLa 3B Happy New Year](https://hartsy.ai/Home?type=models&id=cc752ffd-7d73-4611-b047-cac510f4e1b7) | `heartmula-3b-hny_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| HeartLib (HeartMuLa) | HeartMuLa 3B (RL-Tuned) (`3b-rl`) | Complete | [HeartMuLa 3B RL](https://hartsy.ai/Home?type=models&id=3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00) | `heartmula-3b-rl_fp32-00001-of-00004.safetensors` 4.93 GB | 7 |  |
| HeartLib (HeartMuLa) | HeartMuLa 3B (RL-Tuned) Q4 (`3b-rl-q4`) | Complete | [HeartMuLa 3B RL](https://hartsy.ai/Home?type=models&id=3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00) | `heartmula-3b-rl_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| HeartLib (HeartMuLa) | HeartMuLa 3B (RL-Tuned) Q8 (`3b-rl-q8`) | Complete | [HeartMuLa 3B RL](https://hartsy.ai/Home?type=models&id=3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00) | `heartmula-3b-rl_fp32-00001-of-00004.safetensors` 4.93 GB | 7 | Converted on-device from the base card; installs the base card. |
| Kokoro TTS | Kokoro Default (`default`) | Complete | [Kokoro TTS](https://hartsy.ai/Home?type=models&id=886ff167-e93d-44fb-9bf4-0f4ec4393891) | `kokoro_fp32.safetensors` 327 MB | 55 | Only shared assets come from elsewhere. |
| MeloTTS | MeloTTS English v3 (`english-v3`) | Complete | [MeloTTS English](https://hartsy.ai/Home?type=models&id=6d5891e7-92f5-49e1-b1e2-3eb4c7b2b6f6) | `melotts-english-v3_fp32.safetensors` 207 MB | 2 | vocab.txt is BERT's; the installer puts it beside the BERT encoder, where the engine reads it. |
| Moonshine Streaming STT | Moonshine Streaming Medium (`medium`) | Complete | [Moonshine Streaming Medium](https://hartsy.ai/Home?type=models&id=1c2a2195-9c0a-42d3-8c6a-1956baf6df20) | `moonshinestreaming-medium_fp32.safetensors` 1.06 GB | 1 |  |
| Moonshine Streaming STT | Moonshine Streaming Small (`small`) | Complete | [Moonshine Streaming Small](https://hartsy.ai/Home?type=models&id=eab61907-9561-4279-9fbd-ab9691c8d953) | `moonshinestreaming-small_fp32.safetensors` 561 MB | 1 |  |
| Moonshine Streaming STT | Moonshine Streaming Tiny (`tiny`) | Complete | [Moonshine Streaming Tiny](https://hartsy.ai/Home?type=models&id=560df088-9a4f-4b3c-90d2-43a3b57c7100) | `moonshinestreaming-tiny_fp32.safetensors` 176 MB | 1 |  |
| Moonshine STT | Moonshine Base (`base`) | Complete | [Moonshine Base](https://hartsy.ai/Home?type=models&id=215fe70b-8e1b-4be3-804a-25d46ef85a6f) | `moonshine-base_fp32.safetensors` 246 MB | 2 |  |
| Moonshine STT | Moonshine Tiny (`tiny`) | Complete | [Moonshine Tiny](https://hartsy.ai/Home?type=models&id=732e2a60-ad35-4aee-9639-e81196face4d) | `moonshine-tiny_fp32.safetensors` 108 MB | 2 | Links Moonshine Base's config.json. Works: the engine never reads Moonshine's config.json. |
| MusicGen | MusicGen Large (`large`) | Complete | [MusicGen Large](https://hartsy.ai/Home?type=models&id=57e56727-ab46-49ef-aeec-7dec2c484070) | `musicgen-large_fp16.safetensors` 6.51 GB | 2 |  |
| MusicGen | MusicGen Medium (`medium`) | Complete | [MusicGen Medium](https://hartsy.ai/Home?type=models&id=6e26c1da-1098-406d-aa5e-9e131b217723) | `musicgen-medium_fp32.safetensors` 8.14 GB | 2 |  |
| MusicGen | MusicGen Small (`small`) | Complete | [MusicGen Small](https://hartsy.ai/Home?type=models&id=87c05a55-058a-4d54-bbfc-faf2e18ff41f) | `musicgen-small_fp32.safetensors` 2.36 GB | 2 |  |
| NeuTTS Air | NeuTTS Air (`air`) | Complete | [NeuTTS Air](https://hartsy.ai/Home?type=models&id=b308a4ff-9e9a-4b27-a32a-0ea220c6b0e3) | `neutts-air_bf16.safetensors` 1.50 GB | 1 | Only shared assets come from elsewhere. |
| OpenVoice V2 | OpenVoice V2 (`v2`) | Complete | [OpenVoice V2](https://hartsy.ai/Home?type=models&id=4208508c-bd10-4119-afbd-368afa546921) | `openvoice-v2_fp32.safetensors` 131 MB |  |  |
| Orpheus TTS | Orpheus 3B (`3b`) | Complete | [Orpheus 3B](https://hartsy.ai/Home?type=models&id=7ebbef68-6a4e-4b62-a49a-d92210d69016) | `orpheus-3b_bf16.safetensors` 6.60 GB | 1 |  |
| Qwen3 TTS | Qwen3-TTS 0.6B Base (`0.6B-Base`) | Complete | [Qwen3-TTS 0.6B Base](https://hartsy.ai/Home?type=models&id=a22bc965-45e2-489f-8713-6956f7af0ae9) | `qwen3tts-0_6b-base_bf16.safetensors` 1.83 GB | 1 |  |
| Qwen3 TTS | Qwen3-TTS 0.6B CustomVoice (`0.6B-CustomVoice`) | Complete | [Qwen3-TTS 0.6B CustomVoice](https://hartsy.ai/Home?type=models&id=1373e35a-cf15-471d-a704-a28be15f96de) | `qwen3tts-0_6b-customvoice_bf16.safetensors` 1.81 GB | 1 |  |
| Qwen3 TTS | Qwen3-TTS 1.7B Base (`1.7B-Base`) | Complete | [Qwen3-TTS 1.7B Base](https://hartsy.ai/Home?type=models&id=5266aa28-2c49-4be0-8212-35c0296370da) | `qwen3tts-1_7b-base_bf16.safetensors` 3.86 GB | 1 |  |
| Qwen3 TTS | Qwen3-TTS 1.7B CustomVoice (`1.7B-CustomVoice`) | Complete | [Qwen3-TTS 1.7B CustomVoice](https://hartsy.ai/Home?type=models&id=50f7bb96-ce77-4c8c-af93-3ed352baab4d) | `qwen3tts-1_7b-customvoice_bf16.safetensors` 3.83 GB | 1 |  |
| Qwen3 TTS | Qwen3-TTS 1.7B VoiceDesign (`1.7B-VoiceDesign`) | Complete | [Qwen3-TTS 1.7B VoiceDesign](https://hartsy.ai/Home?type=models&id=661d0c97-86b7-44e1-8f6f-5972bab87f99) | `qwen3tts-1_7b-voicedesign_bf16.safetensors` 3.83 GB | 1 |  |
| Resemble Enhance | Resemble Denoise (`denoise`) | Complete | [Resemble Enhance](https://hartsy.ai/Home?type=models&id=63475ba9-c3cf-4e32-9501-3fa7e4221390) | `resemble-enhance-enhance_fp16.safetensors` 713 MB |  | Same card as enhance: the checkpoint carries the denoiser (189 denoiser.* tensors). |
| Resemble Enhance | Resemble Enhance (`enhance`) | Complete | [Resemble Enhance](https://hartsy.ai/Home?type=models&id=63475ba9-c3cf-4e32-9501-3fa7e4221390) | `resemble-enhance-enhance_fp16.safetensors` 713 MB |  | Same checkpoint serves denoise. |
| SheetSage2 Transcription | SheetSage2 (BF16) (`bf16`) | Complete | [SheetSage2](https://hartsy.ai/Home?type=models&id=ead42a7b-c5b5-43b9-91e7-77a50d3edb3e) | `sheetsage2_bf16.safetensors` 1.39 GB |  |  |
| Spark-TTS | Spark-TTS 0.5B (`0.5B`) | Complete | [Spark-TTS 0.5B](https://hartsy.ai/Home?type=models&id=4c302041-730f-4285-8462-d28b58bffb52) | `sparktts-0_5b_fp32.safetensors` 2.03 GB | 4 |  |
| StyleTTS 2 | StyleTTS 2 LibriTTS (`libritts`) | Complete | [StyleTTS 2](https://hartsy.ai/Home?type=models&id=3c32cabe-1815-4a67-8d5b-2adb1b7df82c) | `styletts2-libritts_fp32.safetensors` 872 MB |  | Only shared assets come from elsewhere. |
| VibeVoice TTS | VibeVoice 1.5B (`1.5b`) | Complete | [VibeVoice 1.5B](https://hartsy.ai/Home?type=models&id=c65f409f-a1a8-4ced-9a13-43f093e40e0e) | `vibevoice-1_5b_bf16-00001-of-00003.safetensors` 1.98 GB | 4 |  |
| Whisper Streaming | Whisper Streaming (base) (`base`) | Complete | [Whisper Base](https://hartsy.ai/Home?type=models&id=c9618d12-d411-4fb0-acce-9d475b02a8e3) | `whisper-base_fp32.safetensors` 290 MB | 6 | Same card as Whisper Base; installs into Whisper's folder, so the file is stored once. |
| Whisper STT | Whisper Base (`base`) | Complete | [Whisper Base](https://hartsy.ai/Home?type=models&id=c9618d12-d411-4fb0-acce-9d475b02a8e3) | `whisper-base_fp32.safetensors` 290 MB | 6 |  |
| Whisper STT | Whisper Large V2 (`large-v2`) | Complete | [Whisper Large v2](https://hartsy.ai/Home?type=models&id=163b99cd-b7fa-4b57-8382-3fe4cbe39abc) | `whisper-large-v2_fp32.safetensors` 6.17 GB | 6 | Links Whisper Base's sidecars. Works (only alignment_heads differs, unread). Relink for accuracy. |
| Whisper STT | Whisper Medium (`medium`) | Complete | [Whisper Medium](https://hartsy.ai/Home?type=models&id=bc615007-6b9b-42bf-8c41-f158c664a0de) | `whisper-medium_fp32.safetensors` 3.06 GB | 6 | Links Whisper Base's sidecars. Works (only alignment_heads differs, unread). Relink for accuracy. |
| Whisper STT | Whisper Small (`small`) | Complete | [Whisper Small](https://hartsy.ai/Home?type=models&id=7323b95b-dd12-4b2e-9525-33eb26015be5) | `whisper-small_fp32.safetensors` 967 MB | 6 | Links Whisper Base's sidecars. Works (only alignment_heads differs, unread). Relink for accuracy. |
| Whisper STT | Whisper Tiny (`tiny`) | Complete | [Whisper Tiny](https://hartsy.ai/Home?type=models&id=949944e6-4477-4e32-85b4-985ee5271b47) | `whisper-tiny_fp32.safetensors` 151 MB | 6 | Links Whisper Base's sidecars. Works: they differ only in generation_config alignment_heads, which the engine does not read. Relink for accuracy. |
| YuE2 Music | YuE2 3B (BF16) (`bf16`) | Complete | [YuE2](https://hartsy.ai/Home?type=models&id=e3ccd2ac-577e-4693-acd2-11ef4ead4ce9) | `yue2_bf16.safetensors` 7.80 GB |  |  |
| RealtimeSTT | RealtimeSTT Default (`default`) | Not implemented |  |  |  |  |
| Amazon Polly | Amazon Polly (`default`) | API |  |  |  |  |
| AssemblyAI | AssemblyAI Universal-2 (`default`) | API |  |  |  |  |
| AWS Transcribe | AWS Transcribe (`default`) | API |  |  |  |  |
| Azure Neural TTS | Azure Neural TTS (`default`) | API |  |  |  |  |
| Azure Speech STT | Azure Speech STT (`default`) | API |  |  |  |  |
| Cartesia Sonic TTS | Cartesia Sonic 2 (`default`) | API |  |  |  |  |
| Deepgram Aura TTS | Deepgram Aura (`default`) | API |  |  |  |  |
| Deepgram Nova-3 STT | Deepgram Nova-3 (`default`) | API |  |  |  |  |
| Dolby.io Audio Processing | Dolby.io Enhance (`default`) | API |  |  |  |  |
| ElevenLabs Sound Effects | ElevenLabs Sound Effects (`default`) | API |  |  |  |  |
| ElevenLabs TTS | ElevenLabs Multilingual v2 (`default`) | API |  |  |  |  |
| ElevenLabs Voice Changer | ElevenLabs Voice Changer (`default`) | API |  |  |  |  |
| ElevenLabs Voice Isolator | ElevenLabs Voice Isolator (`default`) | API |  |  |  |  |
| Google Cloud STT | Google Cloud STT (`default`) | API |  |  |  |  |
| Google Cloud TTS | Google Cloud TTS (`default`) | API |  |  |  |  |
| OpenAI Transcription | GPT-4o Mini Transcribe (`gpt-4o-mini-transcribe`) | API |  |  |  |  |
| OpenAI Transcription | GPT-4o Transcribe (`gpt-4o-transcribe`) | API |  |  |  |  |
| OpenAI Transcription | Whisper-1 (`whisper-1`) | API |  |  |  |  |
| OpenAI TTS | OpenAI GPT-4o Mini TTS (`gpt-4o-mini-tts`) | API |  |  |  |  |
| OpenAI TTS | OpenAI TTS-1 (`tts-1`) | API |  |  |  |  |
| OpenAI TTS | OpenAI TTS-1 HD (`tts-1-hd`) | API |  |  |  |  |
| Play.ht TTS | Play.ht (`default`) | API |  |  |  |  |
| Suno Music | Suno v4 (`default`) | API |  |  |  |  |
| Udio Music | Udio v1.5 (`default`) | API |  |  |  |  |

## 8. Re-check

```bash
curl -s https://hartsy.ai/download/<card-id>/manifest | python3 -m json.tool
```

