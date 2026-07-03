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
| qwen3_tts | 1.7B-CustomVoice | ✅ **fixed** (was always-OOM) | 112 | **109** | **1.04** | 8389 | ~1.0* | FIVE stacked bugs fixed: Reshape GC-use-after-free; talker-loop OOM (pre-decode `FreeActivations`); vocoder dim 512-vs-256 OOB (AccessViolation); **EOS never fired** → babbled to cap+OOM (faithful stop-condition port); dim guards. Now **realtime, 8GB** — the hardest case in the catalog, from always-crashes to working. Plus synchronous GPU-cache eviction. *speaks input then minor rambling tail |
| qwen3_tts | 0.6B-CustomVoice | ✅ **fixed** (was instant-crash) | 23 | **20** | **1.06** | 5060 | ~1.0* | was instant `INVALID_VALUE`: talker (hidden 1024) + MTP assumed 1.7B's 2048 dims. Now **faster-than-realtime**, first sentence verbatim. *minor rambling tail after correct content (EOS calibration) |
| fishspeech_tts | fish-speech-1.5 | ✅ | 12.7 | **9.3** | **0.81** | 4027 | **0.042** | was instant-EOS blank: 4 port bugs (codebook embeds not zeroed for text rows per upstream `semantic_token_id` masking; prefill duplicated last token; cb0 sampled instead of derived; fast model double-normalized) + extension used `<|audio_start|>` which isn't in the 1.5 vocab. After faithful port: best WER of the sweep |
| vibevoice_tts | 1.5b | ⚠️ 6 fixes in; 1 localized bug left | — | — | — | 11293 | — | **Fixed:** mmap use-after-unmap segfault; BF16 scalar reads; DPM-solver NaN; byte-level BPE dropping leading spaces (was the fluent-but-wrong-content cause); streaming acoustic/semantic caches now threaded through the AR loop (bit-identical equivalence test); upstream colon-space prompt format. Weights → `microsoft/VibeVoice-1.5B` (original, un-gated). **Remaining (precisely localized):** first segment tracks correctly + says ~right words + hits `speech_end` at step 21 (≈ reference's 22), but at the segment→EOS boundary picks `speech_start`(22.8) over `eos`(20.5) — a ~2.3-logit gap → starts a new segment instead of terminating → over-generates. Not tokenizer/cache/prompt (all ruled out by experiment); it's **diffusion-feedback numerical fidelity** (DPM/diffusion-head/step-count). Needs a working Python reference for per-step A/B (box's community-vibevoice env is currently broken) |
| neutts_tts | air | ✅ fixed | 92 | 90 | 0.10 | 3566 | 0.54 | was "(laughing)" (WER 1.0): fed raw text (model trained on espeak IPA) + never mapped 445 IPA tokenizer tokens + wrong framing. Faithful port vs `neutts.py`; now intelligible ("quick...jumps...lazy dog...benchmark...engine running...text-to-speech"). Residual WER = espeak-punctuation gap + best-effort no-reference voice (upstream always clones; needs X-Codec2 encoder). **Side-win: espeak now works → unblocks Piper/Zonos/MeloTTS** |
| orpheus_tts | 3b | ✅ un-gated, reverify pending | — | — | — | — | — | was HF 401. Now `unsloth/orpheus-3b-0.1-ft` — non-gated mirror, verified standard Llama-3.2 layout, no token needed. May hit engine Llama-3-tokenizer gate at synth (verify next) |
| csm_tts | 1b | ✅ un-gated, reverify pending | — | — | — | — | — | was HF 401. Now `nielsr/csm-1b` — non-gated mirror, verified byte-identical original format (backbone/decoder/text_embeddings, 187 tensors), no token needed. May hit engine Llama-3-tokenizer gate at synth (verify next) |
| kyutaitts_tts | 1.6b-en-fr | ❌ loader path | — | — | — | — | — | requests `pytorch_model.bin`; repo ships `dsm_tts_*.safetensors` — filename fix queued (synth separately gated on per-frame text front-end) |
| cosyvoice_tts | 2-0.5b | ⛔ gated | — | — | — | — | — | engine front-end pending |
| pockettts_tts | default | ⛔ gated | — | — | — | — | — | placeholder dims + missing SentencePiece asset |
| piper_tts | default | ⛔ gated | — | — | — | — | — | needs espeak-ng phonemizer + phoneme_id_map + ONNX loader |
| melotts_tts | english-v3 | ⛔ gated | — | — | — | — | — | needs espeak + tone/lang streams + BERT features |
| sparktts_tts | 0.5B | ⛔ gated | — | — | — | — | — | token offsets + BiCodec keys reconciliation pending |
| styletts2_tts | libritts | ⛔ gated | — | — | — | — | — | no unified LoadWeights yet |
| zonos_tts | transformer | ⛔ gated | — | — | — | — | — | needs espeak conditioning prefix |

## STT

All on GPU (verified GPU util 30–90%), transcribing an 8.65s clip. RTF = audio-sec / warm-wall-sec (higher = faster). Transcriptions verified accurate (tiny rough as expected; base→large clean).

| Provider | Model | Status | Cold (s) | Warm (s) | RTF warm | Notes |
|---|---|---|---|---|---|---|
| whisper_stt | tiny | ✅ | 1.1 | 1.06 | 8.2 | rough ("lazy dog"→"Z Dog") — expected for tiny |
| whisper_stt | base | ✅ | 1.64 | 1.59 | 5.4 | clean |
| whisper_stt | small | ✅ | 4.07 | 3.52 | 2.5 | clean |
| whisper_stt | medium | ✅ | 10.5 | 8.64 | 1.0 | clean |
| whisper_stt | large-v3 | ✅ | 9.28 | 2.27 | **3.8** | clean; in faster-whisper's reference range |
| whisper_stt | turbo | ✅ | 5.34 | 1.70 | **5.1** | clean |
| distilwhisper_stt | large-v3 | ✅ | 6.79 | 4.19 | 2.1 | clean |
| moonshine_stt | base | ✅ | 1.29 | 1.24 | 7.0 | clean |
| moonshine_stt | tiny | ✅ | 8.59 | 0.68 | 12.7 | fastest |
| whisperstreaming_stt | base | ✅ | 1.68 | 1.63 | 5.3 | LocalAgreement-2 streaming over Whisper base |
| kyutaistt_stt | 1b-en-fr | ❌ loader | — | — | — | `-trfs` checkpoint key layout (`model.layers.0.self_attn.q_proj.weight` not found) ≠ engine's Kyutai loader expectation. Repo has the file; key-mapping fix needed (open-item) |

**STT verdict:** every Whisper/Distil/Moonshine/streaming variant works on GPU at speeds squarely in the
faster-whisper reference range (turbo ~5x RT, large-v3 ~3.8x RT) — no engine work was needed here beyond the
systemic residency/ProjectLinear fixes. Only Kyutai STT has a key-layout mismatch.

## Music / Audio generation

| Provider | Model | Status | Wall (s) | RTF | VRAM peak MB | Notes |
|---|---|---|---|---|---|---|
| acestep_music | turbo | ✅ | 22.6 | **1.33** | 5192 | was OOM-every-time at 11.9GB (stale weights left no room); synchronous GPU-cache eviction on provider switch → fits at 5.2GB, faster-than-realtime. 30s music (flow-matching DiT — few steps, fast) |
| heartlib_music | 3b-hny | ⚠️ correct but impractically slow | ~3600 | ~0.008 | — | generates the full 30s (frame 375/375, no crash) but ~1hr wall on a 3060. 3B autoregressive at F32 = per-op-launch-overhead bound (same class as F5, ×3B). Needs op-fusion/CUDA-graphs/BF16 |
| musicgen_music | small | ✅ | 78 (8s audio) | 0.10 | 3606 | works; ~0.3x RT in Python on T4-class, ours 0.10 (F32, per-op overhead). Functional audio |
| audiogen_sfx | medium | ❌ loader bug | — | — | — | `PytorchPickleLoader` can't find EOCD in audiogen's 3.68GB zip (file IS valid + complete). MusicGen uses the same loader and works → narrow robustness bug (likely false-positive `PK\x05\x06` in uncompressed weight data fooling .NET's backward EOCD scan). Engine open-item |
| yue_music | en-cot | ⛔ user-placed | — | — | — | by design no auto-download: user places `m-a-p/YuE-s1-7B-anneal-*` folder + `xcodec.safetensors`. Clear error message. (7B AR — would also be HeartMuLa-class slow) |

## Voice conversion

| Provider | Model | Status | Wall (s) | RTF | VRAM peak MB | Notes |
|---|---|---|---|---|---|---|
| openvoice_clone | v2 | ✅ | 3.3 | **2.6–3.0** | 2023 | tone-color transfer works, faster-than-realtime, 2GB |
| rvc_clone | v2 | ⛔ engine gap | — | — | — | "not yet supported by the in-process C# engine" — handler pending (auto-lights-up when it lands) |

## Enhancement / processing

| Provider | Model | Status | Notes |
|---|---|---|---|
| demucs_fx | htdemucs | ⛔ user-placed | needs `Models/audio/fx/Demucs/htdemucs.th` placed manually (no auto-download); clear error |
| resemble_enhance_fx | denoise | ⛔ engine gap | engine's DeepSpeed `.pt` loader not implemented |

## Voice conversion

| Provider | Model | Status | Notes |
|---|---|---|---|
| _...pending..._ | | | |

## Enhancement / processing

| Provider | Model | Status | Notes |
|---|---|---|---|
| _...pending..._ | | | |

## Comparison vs ComfyUI/Python (RTX 3060-class)

Reference numbers gathered from published sources (faster-whisper README/issues, audiocraft docs, ACE-Step/HeartMuLa
repos, community 3060 reports). "≈" = extrapolated where no direct 3060 number exists.

| Model | HartsyInference (this engine, 3060) | Python/Comfy reference | Read |
|---|---|---|---|
| Whisper large-v3 | 3.8x RT | faster-whisper ~15x RT (fp16, 3070Ti); openai-ref ~5.5x RT | competitive with the reference *implementation*; faster-whisper's CTranslate2 int8 is faster |
| Whisper turbo | 5.1x RT | faster-whisper ~41x RT; community 3060 ~6.7x RT | in the community-3060 range |
| Kokoro-82M | RTF 4.3 | within Kokoro's typical fast range | on par |
| Chatterbox | RTF 0.50 | Python-class on this GPU | on par after residency fix (was 0.05 before) |
| F5-TTS | RTF 0.016 | ~near-realtime in Python (fp16) | **~30-60x slower** — F32 + per-op launch overhead (fusion/graphs needed) |
| ACE-Step v1.5 turbo | RTF 1.33 | <10s/song on 3090 (~24x RT); ≈4-5x RT on 3060 | slower but same order; flow-matching = few steps, fits 12GB |
| MusicGen small | RTF 0.10 | ~0.3x RT on T4-class | ~3x slower (F32 vs fp16) |
| HeartMuLa-3B | RTF 0.008 | RTF ~1.0 on A100/4090-class | **impractically slow on 3060** (F32 AR, per-op overhead) |
| OpenVoice v2 | RTF 2.6–3.0 | ~12x RT on A10G (paper) | reasonable for 3060 |
| Demucs htdemucs | (needs user checkpoint) | ~25x RT on 3060Ti | n/a until weights placed |

### The headline finding

**Where the engine matches Python it's because the weights are small or the model is few-step
(Whisper, Kokoro, Chatterbox, ACE-Step, OpenVoice — all on par).** Where it's far behind (F5, MusicGen, HeartMuLa)
the cause is uniform and now well-understood: **the engine runs F32 where PyTorch runs fp16/bf16 on tensor cores,
and long autoregressive/many-step loops pay per-op CUDA launch overhead across thousands of small kernels.**

Two engine-wide levers already landed this pass — **TF32 GEMMs** (Ampere tensor cores for F32 operands, PyTorch's
default) and **weight GPU-residency** (was re-uploading weights per op). The remaining lever for the slow models is
**kernel fusion / CUDA graphs** for the AR/DiT step loops, plus optional **BF16-native weights** (halves VRAM +
uses tensor cores natively). These are the highest-value follow-ups and would close most of the remaining gap.

VRAM, by contrast, is already excellent: the residency + activation-hygiene + synchronous-eviction work means
every model that fits Python's fp16 footprint now fits here too (F5 2.8GB, ACE-Step 5.2GB, Qwen3-1.7B 8GB), with
clean release between generations and no leaks observed.

## Known remaining issues / open engine work

**Performance — ROOT CAUSE IDENTIFIED (per-op GPU synchronization):**
Profiling HeartMuLa (instrumented repro, `GpuTransferHelper.GetSyncCount`/`GetStats`) showed the dominant cost is
NOT compute and NOT the individual ops we first suspected. Per frame: **~116 D2H syncs + ~722 cache-miss H2D
uploads, ~950ms**. `GpuTransferHelper.CopyToDevice` does a full `cuStreamSynchronize` on **every cache-miss H2D
copy** (a blocking stream by design — see `CudaBackend` ctor comment). For the many-small-op models this stalls
the pipeline thousands of times: F5's DiT is ~14,000 ops (32 steps × 22 layers × 2 CFG) at ~32ms/op = pure sync
overhead (TF32 GEMMs are sub-ms). This is why F5 (451s) and HeartMuLa (~1hr) are slow despite high GPU util.

Two targeted fixes landed this pass and are CORRECT but individually insufficient (each removed a real
inefficiency without moving the dominant sync cost): **F5 RoPE** routed off the CPU-fallback `ApplyRopeInterleaved`
onto the GPU `WanRopeInterleaved` kernel; **CSM/HeartMuLa depth decoder** made incremental (was O(codebooks²) —
fresh KV cache + host-rebuilt sequence per codebook; now one persistent cache + one token per codebook).

The real levers, in priority:
1. **Async H2D transfers** — switch `CopyToDevice`'s synchronous `cuMemcpyHtoD` + per-miss `SyncStream()` to
   `cuMemcpyHtoDAsync` on the compute stream (the engine's own ctor comment names this fix). Removes the per-op
   stall for ALL small-op models (audio DiT/AR + image). Deep core change — needs careful cross-engine validation.
2. **100% activation residency** in the hot loops — audit that every op keeps its output GPU-resident
   (`CacheActivation`) so downstream inputs are cache HITS (no miss → no sync). Per-model, lower-risk.
3. **BF16-native weights** — the engine promotes weights to F32, doubling VRAM and forgoing native tensor-core
   BF16 GEMMs.

**Correctness / coverage:**
- **Qwen3-TTS rambling tail** — both variants speak the input correctly then over-run with a hallucinated tail
  (EOS/conditioning calibration; the crash/OOM bugs are fixed, this is quality refinement).
- **VibeVoice text-conditioning** — runs cleanly but hallucinates content (WER 2.3); needs the same faithful-port
  audit the other models got.
- **AudioGen loader** — `PytorchPickleLoader` EOCD detection fails on its specific 3.68GB zip (MusicGen, same
  loader, works); needs a more robust EOCD scan.
- **Kyutai STT** — `-trfs` checkpoint key layout doesn't match the engine's Kyutai loader.
- **NeuTTS** — residual WER from espeak-punctuation gap + best-effort no-reference voice; full cloning needs the
  X-Codec2 encoder port. **RVC** and **Resemble-Enhance** handlers are engine-pending.

**Deferred (already improved this pass):** autoregressive TTS host-overhead — mitigated by weight residency + the
ProjectLinear fix; the remaining gap is the fusion/graphs work above.

**User-placed by design (not bugs):** YuE (`m-a-p/YuE-s1-7B` folder + xcodec), Demucs (`htdemucs.th`).
