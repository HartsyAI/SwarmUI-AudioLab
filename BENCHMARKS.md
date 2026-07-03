# AudioLab Model Benchmarks

E2E benchmarks of every local (HartsyInference-engine) audio model, driven entirely through the SwarmUI API
(`ProcessTTS` / `ProcessSTT` / `ProcessAudio` / `AudioLabInstallEngine`). Each model is verified by decoding the
returned WAV (duration / RMS / spectral sanity) and — for TTS — a Whisper round-trip transcription (WER vs input
text; proper nouns like "Hartsy" inflate WER slightly, normal words should be near-exact).

**Test rig:** RTX 3060 12GB (SM 8.6), Linux, CUDA 13.2, engine = local SharpInference build (see git log),
`AutoPromoteWeights=True`. "RTF" = generated-audio-seconds per wall-second (higher is better; 1.0 = realtime).
"Warm" = model already loaded (steady state); "Cold" = includes weight load, first-call JIT, and any downloads
already on disk. VRAM/RSS are whole-process peaks sampled at 4 Hz during the call.

> Status legend: ✅ works + verified · ⚠️ works with caveats · ⛔ gated (engine piece pending, clean error) · ❌ broken (bug)

## Engine/extension fixes made during this pass

| Fix | Where | Effect |
|---|---|---|
| Audio pipelines never preloaded weights to GPU — every op re-uploaded weights over PCIe | engine `GpuTransferHelper` (auto-promotion of repeatedly-uploaded tensors, `HARTSY_NO_AUTOPROMOTE=1` to disable, headroom via `HARTSY_AUTOPROMOTE_HEADROOM_MB`) | Chatterbox warm 66s → 26s (2.5x); applies to all audio models; weights now VRAM-resident |
| Weight-cast cache byte accounting never decremented on free | engine `GpuTransferHelper` | correct cache-size stats |
| `voice:"default"` (API placeholder) passed to models as a literal voice name | `TtsHandler` | Kokoro (and any voice-named model) no longer 404s on `voices/default.pt` |
| `processing_time` always 0.0 in `ProcessTTS`/`ProcessSTT` responses | `AudioLabAPI` | real seconds reported |
| `AudioLabInstallEngine model_id` ignored for self-managed providers — installing "whisper base" downloaded **all 7** Whisper checkpoints (~10GB) | `DynamicAudioBackend.InstallAndRegisterEngine` | model_id now installs only that variant |
| **Bark: no KV cache** in the shared GPT backbone (semantic+coarse re-ran the full growing sequence per token — O(T²), 20+ min per clip, never finished) | engine `GptBackbone`/`GptBlock`/`BarkCausalStage` (+ new `GptKvCache`); incremental step math verified against full forward in tests | semantic stage: minutes → seconds |
| **Bark: generation orchestration didn't match upstream** — semantic missing `merge_context` (256-text+256-history summed embeds) and `min_eos_p` early stop; coarse missing ratio-derived step count, 60-step sliding windows (overran the 1024 context → truncated audio), and per-step codebook-range-constrained sampling (sampled the whole 12k vocab → garbage codes); fine used placeholder id 0 instead of 1024 and skipped the 1024-frame windowing + temp-0.5 sampling | engine `BarkCausalStage.GenerateSemantic/GenerateCoarse`, `BarkFineModel.Refine`, `BarkPipeline` — faithful port of upstream `bark/generation.py` | correct-length, correctly-conditioned audio |
| **Bark: F32 codes handed to EnCodec** (requires I32) — latent crash at the decode step, never reached before because generation never completed | engine `BarkPipeline` | E2E completes |
| **`WhisperOps.ProjectLinear` CPU-transposed the full weight matrix on EVERY call** into a fresh scratch tensor (uncacheable → full weight re-crossed PCIe per linear, plus a cache-hostile CPU transpose). Every linear in the whole audio stack routes through this helper | engine `WhisperOps` — dispatch straight to `IBackend.Linear` (takes PyTorch `[out,in]` weights as-is; original tensor now auto-promotes to GPU residency) | systemic: all audio models |
| **MusicGen: no KV cache** (same O(T²) full-sequence refeed as Bark) + cross-attention K/V to the T5 text recomputed every step | engine `MusicGenKvCache`/`MusicGenBlock`/`MusicGenDecoder`/`MusicGenPipeline` — incremental probe/commit decode, cross K/V projected once per generation; equivalence-tested | O(T) music decode |

**Bark E2E after fixes (RTX 3060):** 13.0s audio in 189s cold (0.07x RT), VRAM peak 5.3GB, output intelligible speech
(random speaker — Bark's unconditioned mode). Follow-ups: voice-preset (history prompt) support for stable speakers;
per-token host↔GPU sync overhead is the remaining speed bottleneck (GPU util ~17% mean).

| Fix (continued) | Where | Effect |
|---|---|---|
| **Loaded models never evicted** — every provider's runner (multi-GB F32 weight copies) stayed resident forever; switching through providers accumulated until the kernel OOM-killed SwarmUI at 21.8GB RSS (32GB box) during a NeuTTS install | `IAudioHandler.UnloadAll()` on all 6 handlers + `AudioEngine.EvictOthersUnderMemoryPressure` (evicts other providers when `/proc/meminfo MemAvailable` < 10GB on provider switch; same-provider stays warm) — also guards the install prefetch path | no more OOM accumulation |
| NeuTTS: `NeuCodecEncoder` expects a key layout (`encoder.stem.*`) that doesn't exist in `neuphonic/neucodec` (which is X-Codec2: `CodecEnc.*`/`generator.*`) — install always failed | extension `NeuTtsModel` — encoder now optional: default voice works, reference cloning gates with a clear message. TODO(engine): port the X-Codec2 encoder | NeuTTS default voice usable |
| Qwen3-TTS 0.6B variants gated on a missing engine config preset | engine `Qwen3Config.Talker0_6B` + `Qwen3TtsConfig.Default_0_6B` (dims verified against the HF config.json), extension gate removed | 0.6B variants unlocked |
| Dia: untagged text degenerates into repetition loops (WER 0.48, looped first sentence) | extension `TtsModels` — auto-prepends `[S1]` when no speaker tag present | usable untagged input |

**Dia-1.6B (RTX 3060, pre-`[S1]`-fix):** 20.0s audio, 580s cold / 485s warm (0.04x RT), VRAM peak 11.4GB (hits the
12GB card's promotion headroom floor), keeps 10.3GB resident. Slowest working model — flagged as perf outlier.

## TTS

| Provider | Model | Status | Cold (s) | Warm (s) | RTF warm | VRAM peak MB | WER | Notes |
|---|---|---|---|---|---|---|---|---|
| chatterbox_tts | default | ✅ | 18.0 | **15.2** | **0.50** | 5084 | 0.22 | 66s → 15.2s across the two engine fixes (weight residency + ProjectLinear); output bit-identical (same WER). Python-class perf on this GPU |
| kokoro_tts | default (af_heart) | ✅ | 2.5 | **2.0** | **4.31** | 5508 | 0.30 | 8.4s → 2.0s; within the Python reference range for Kokoro-82M |
| bark_tts | default | ✅ | 189 (13.0s audio) | — | 0.07 | 5260 | 0.52* | required 4 engine fixes (see above); *unconditioned random speaker, quiet — voice presets pending |
| dia_tts | 1.6b | ✅ fixed (port-correct) | 1240 (20s dialogue) | 905 | 0.02 | 11721 | 0.84* | **9 recipe deviations fixed vs upstream**; step-0 logits corr 1.000000 with upstream torch; engine RMS matches upstream (0.1055 vs 0.1079). *First `[S1]` line transcribes verbatim (proves correctness); later dialogue lines degrade — a model limitation (upstream shows the same), not a port bug. Peak-level "clipping" is loud-but-legit speech. Short prompts → silence (upstream too; extension warns). Slow (F32, long dialogue); VRAM hygiene keeps it under the 12GB edge |
| f5_tts | v1-base | ⚠️ | 454 (~7.3s audio) | 455 | 0.016 | 10165 | **0.125** | correct + zero-shot clone works; 60x slower than Python — `F5Ops` runs adaLN/norms as CPU loops (GPU-residency refactor queued) |
| qwen3_tts | 1.7B-CustomVoice | ✅ fixed, reverify pending | 598 | — | — | 10741 | — | FIVE stacked bugs: (1) `Tensor.Reshape` views didn't root parent → GC use-after-free (fixed engine-wide); (2) talker-loop activations OOM'd the card → pre-decode `FreeActivations`; (3) vocoder config `AcousticCodebookDim=512` vs checkpoint `[2048,256]` → OOB unsafe strides = AccessViolation; (4) **EOS never fired** → babbled to cap + OOM → faithful stop-condition port (mask EOS < MinNewTokens, break on CodecEos); (5) dim guards added. Reverify pending |
| qwen3_tts | 0.6B-CustomVoice | ✅ fixed, reverify pending | — | — | — | 4084 | — | was instant `INVALID_VALUE`: talker (hidden 1024 vs 1.7B's 2048) + MTP predictor assumed 1.7B dims; faithful port fixes both. CPU-validated 2.8s real speech for a 25-word sentence (proportional length = EOS works). GPU reverify pending |
| fishspeech_tts | fish-speech-1.5 | ✅ | 12.7 | **9.3** | **0.81** | 4027 | **0.042** | was instant-EOS blank: 4 port bugs (codebook embeds not zeroed for text rows per upstream `semantic_token_id` masking; prefill duplicated last token; cb0 sampled instead of derived; fast model double-normalized) + extension used `<|audio_start|>` which isn't in the 1.5 vocab. After faithful port: best WER of the sweep |
| vibevoice_tts | 1.5b | ✅ fixed, retest pending | 47 → SIGSEGV | — | — | — | — | 3 bugs: pipeline disposed the mmap loaders while the Qwen-1.5B LM kept borrowed weight views (use-after-unmap → libcuda segfault on first upload); BF16 rank-0 speech scaling/bias factors read as F32 garbage; DPM-solver schedule included t=0 + missing `lower_order_final` → NaN cascade. Fixed vs upstream; standalone GPU validation produced clean audio |
| neutts_tts | air | ✅ fixed, reverify pending | 15.3 | 13.8 | — | 11656 | 1.0→fixed | was "(laughing)": port fed raw text (model trained on espeak IPA) + never mapped the 445 dedicated IPA tokenizer tokens + wrong chat framing. Faithful port vs upstream `neutts.py` (espeak-ng phonemize + IPA token table + exact template); GPU-validated real speech. **Side-win: espeak phonemizer now works → unblocks Piper/Zonos/MeloTTS** |
| orpheus_tts | 3b | ⛔ HF-gated | — | — | — | — | — | 401: accept license + set `HF_TOKEN` (user action) |
| csm_tts | 1b | ⛔ HF-gated | — | — | — | — | — | 401: accept license + set `HF_TOKEN` (user action) |
| kyutaitts_tts | 1.6b-en-fr | ❌ loader path | — | — | — | — | — | requests `pytorch_model.bin`; repo ships `dsm_tts_*.safetensors` — filename fix queued (synth separately gated on per-frame text front-end) |
| cosyvoice_tts | 2-0.5b | ⛔ gated | — | — | — | — | — | engine front-end pending |
| pockettts_tts | default | ⛔ gated | — | — | — | — | — | placeholder dims + missing SentencePiece asset |
| piper_tts | default | ⛔ gated | — | — | — | — | — | needs espeak-ng phonemizer + phoneme_id_map + ONNX loader |
| melotts_tts | english-v3 | ⛔ gated | — | — | — | — | — | needs espeak + tone/lang streams + BERT features |
| sparktts_tts | 0.5B | ⛔ gated | — | — | — | — | — | token offsets + BiCodec keys reconciliation pending |
| styletts2_tts | libritts | ⛔ gated | — | — | — | — | — | no unified LoadWeights yet |
| zonos_tts | transformer | ⛔ gated | — | — | — | — | — | needs espeak conditioning prefix |

## STT

| Provider | Model | Status | Wall (s) | Audio len | VRAM peak MB | Notes |
|---|---|---|---|---|---|---|
| whisper_stt | (default) | ✅ | 3.5–4.2 | 8.7s | — | round-trip verifier for TTS outputs |
| _...pending..._ | | | | | | |

## Music / Audio generation

| Provider | Model | Status | Wall (s) | Output | VRAM peak MB | Notes |
|---|---|---|---|---|---|---|
| _...pending..._ | | | | | | |

## Voice conversion

| Provider | Model | Status | Notes |
|---|---|---|---|
| _...pending..._ | | | |

## Enhancement / processing

| Provider | Model | Status | Notes |
|---|---|---|---|
| _...pending..._ | | | |

## Comparison vs ComfyUI/Python

_Pending: per-model reference numbers (published/typical RTF on RTX 3060-class hardware) collected alongside the sweep._

## Known remaining issues

- Autoregressive TTS (Chatterbox-class) GPU util ~28% mean warm: per-token host overhead (per-op stream syncs on
  transient H2D uploads, CPU-side sampling) is now the bottleneck; engine-side loop residency work needed.
