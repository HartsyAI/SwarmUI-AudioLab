# SwarmUI AudioLab Extension

A modular audio processing extension for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) that adds text-to-speech, speech-to-text, audio generation, voice conversion, and audio processing — all through a provider-based architecture integrated directly into the Generate tab.

## Features

- **Text-to-Speech (TTS)** — 17 providers: Chatterbox, Kokoro, Bark, Orpheus, Piper, Dia, F5-TTS, StyleTTS2, Fish Speech, Pocket TTS, Kyutai TTS, Qwen3, Zonos, CSM, VibeVoice, CosyVoice, and NeuTTS
- **Speech-to-Text (STT)** — 5 providers: Whisper, Kyutai STT, Distil-Whisper, Moonshine, and RealtimeSTT
- **Audio Generation** — ACE-Step 1.5 (6 DiT models, 6 task types, lyrics alignment, 50 languages), MusicGen (text-to-music with melody conditioning), and AudioGen (text-to-sound-effects)
- **Voice Conversion** — RVC (re-voice existing audio), OpenVoice (tone/style transfer), GPT-SoVITS (TTS with cloned voice)
- **Audio Processing** — Demucs (stem separation) and Resemble Enhance (audio enhancement/denoising)
- **Multi-Track DAW Editor** — Full digital audio workstation with timeline, transport controls, per-track mute/solo/volume, clip arrangement via drag-and-drop, mixer panel, loop regions, undo/redo, and multi-track mixdown export (WAV/MP3/OGG/FLAC/AAC)
- **Video + Audio** — Combine audio with video or extract audio from video via ffmpeg
- **Streaming TTS** — Chunked text-to-speech with auto-play for immediate playback while generating
- **Generation Cancellation** — Stop Generation and Stop All Generations buttons work for all audio providers
- **Pure C# Inference (no Python)** — Audio models run in-process via the [HartsyInference](https://www.nuget.org/packages/HartsyInference) engine, hosted by the companion **HartsyInference (Pure C# Inference)** backend extension. No Python, no venvs, no Docker.
- **On-Demand Weights** — Install only the models you need. Multi-variant engines (e.g. ACE-Step's 9 checkpoints) expose a per-model Install/Remove button plus a **Download All** option, so you can grab just `turbo` instead of every variant. STT and other self-managed models download themselves on first use.

## Requirements

- SwarmUI installed and working
- The **HartsyInference (Pure C# Inference)** backend extension installed and added in Server > Backends (this is the in-process engine AudioLab runs models on)
- ffmpeg on PATH (for audio decode/encode and video+audio features)
- A CUDA or Vulkan GPU is recommended for the larger models; several (e.g. Kokoro, Moonshine) are CPU-capable

> **No Python or Docker required.** Earlier versions ran each engine in a Python virtual environment; the audio engines now run as pure C# in-process. The per-engine capability tables below are the coverage roadmap — models light up as the C# engine adds support.

## Installation

1. Clone into the SwarmUI extensions directory:
   ```bash
   cd /path/to/SwarmUI/src/Extensions/
   git clone https://github.com/Hartsy/SwarmUI-AudioLab.git
   ```

2. Restart SwarmUI. The extension loads automatically.

3. In SwarmUI, go to **Server** > **Backends** and add the **Audio Backend**.

4. In **Server** > **Backends**, expand the Audio Backend card and click an engine to open its install panel. Install individual models with their per-model **Install** button (or **Download All** for the whole set); multi-variant engines let you install/remove one checkpoint at a time. Weights download automatically; self-managed models (e.g. Whisper) fetch themselves on first use. Generation runs in-process on the C# engine.

## Measured Performance (in-engine, this extension)

Real-time factor (RTF = generated-audio-seconds ÷ warm generation time; higher = faster than real time), measured
end-to-end through this extension on the pure-C# engine. These are **our measured numbers** — distinct from the
per-engine "Notes" column further down, which quotes each upstream project's own claims.

Measured through the canonical **`GenerateText2Image`** path (select the model → text in the prompt → WAV in `/Output`, like any gen); STT via `ProcessSTT`.

| Model | Type | RTX 3060 | RTX 4090 | Status |
| --- | --- | --- | --- | --- |
| Piper | TTS | 8.6× | 8.3× | ✅ runnable |
| Moonshine | STT | 6.5× | 6.5× | ✅ runnable |
| Whisper (base) | STT | 5.1× | 5.4× | ✅ runnable |
| Kokoro | TTS | 4.5× | 5.2× | ✅ runnable + word-correct (misaki g2p + canonical-fallback fixes) |
| MeloTTS | TTS | 1.4× | 1.4× | ✅ runnable + word-correct (stride + number-norm fixes); slow — optimization target |
| F5-TTS (voice clone) | TTS | ~0.4× | — | ✅ runnable + word-correct; **174.6 s → 6.4 s (34×)** host-conv→GPU, bit-parity |

The small TTS/STT models are **host/launch-bound, not compute-bound** — a faster GPU (4090) barely helps; the real
lever is CUDA-graph capture to remove kernel-launch overhead. Both STT engines (Moonshine, Whisper) transcribe
real human speech (JFK clip) + synthetic clips **word-perfect** (verified 2026-07-13); the Whisper `en-US`
default-language crash is **fixed** (locale-code normalization). Full table, method, and the remaining outlier list
(e.g. Spark-TTS not-yet-runtime-wired) are in the engine repo at `benchmarks/results/audio_tts_stt_2026-07-12.md`. Note: several models marked "verified" upstream
are numerical-parity-verified in the test harness but **not yet runnable through the install/generate path** — the
install button surfaces a clear message when a model isn't wired yet.

## Supported Engines

### Text-to-Speech (16 Providers, 30+ Models)

| Engine | Voice Reference | Streaming | VRAM | Notes |
| --- | --- | --- | --- | --- |
| Chatterbox | Optional | Yes | ~4 GB | Expressive with exaggeration/CFG controls |
| Kokoro | No | Yes | ~1 GB | 96x real-time on GPU, CPU-capable, multiple built-in voices |
| Pocket TTS | Optional | No | CPU (~200MB) | 100M params, 8 built-in voices, voice cloning, MIT license, ~6x real-time on CPU |
| Kyutai TTS | Optional | No | ~8 GB | 1.8B params, English+French, voice conditioning, 75x real-time, ~200ms latency |
| Piper | No | Yes | CPU only | CPU-only ONNX runtime, lightweight, auto-downloads voices |
| Bark | No | Yes | ~5 GB | Multi-language, emotion/music/SFX support |
| Orpheus | No | Yes | ~16 GB | 3B params, emotion tags (`<laugh>`, `<sigh>`, etc.) |
| Dia | No | Yes | ~10 GB | 1.6B params, 2-speaker dialogue with nonverbal sounds |
| F5-TTS | Required | Yes | ~4 GB | Flow-matching, zero-shot cloning from ~10s reference |
| Fish Speech | Optional | Yes | 4–24 GB | 80+ languages, inline prosody tags (`[whisper]`, `[emphasis]`, etc.) |
| Qwen3 TTS | Optional | Yes | 4–8 GB | 5 model variants: cloning, custom voices, voice design from descriptions |
| CSM | No | Yes | ~4.5 GB | 1B params, multi-turn conversational speech |
| VibeVoice | Optional | Yes | 3–16 GB | 3 sizes (0.5B–7B), multi-speaker, up to 90 min long-form |
| Zonos | Optional | Yes | ~4 GB | Emotion control, transformer and hybrid variants (EN/JP/CN/FR/DE) |
| CosyVoice | Optional | Yes | ~8 GB | Ultra-low latency streaming, multilingual |
| NeuTTS | Required | Yes | ~2 GB | 0.5B params, instant voice cloning, CPU-capable |

### Speech-to-Text (5 Providers, 14 Models)

| Engine | Models | VRAM | Notes |
| --- | --- | --- | --- |
| Whisper | 7 sizes (tiny–turbo) | 1–10 GB | OpenAI Whisper, transcribe + translate, multi-language |
| Kyutai STT | 1B (en+fr), 2.6B (en) | 3–6 GB | Auto capitalization/punctuation, 1B has voice activity detection |
| Distil-Whisper | large-v3, large-v3.5 | ~2 GB | 6x faster than Whisper large-v3 |
| Moonshine | base, tiny | ~1 GB / CPU | Lightweight, CPU-capable |
| RealtimeSTT | default | ~2 GB | Real-time streaming with wake word detection (not yet runnable on the C# engine — use Whisper) |

### Audio Generation (3 Providers, 17 Models)

| Engine | Models | VRAM | Notes |
| --- | --- | --- | --- |
| ACE-Step 1.5 | 6 DiT variants (turbo/sft/base) | 8–10 GB | 6 task types (text2music, cover, repaint, extract, lego, complete), lyrics alignment, 50 languages, optional LM planner |
| MusicGen | 10 variants (mono/stereo/melody) | 4–10 GB | Text-to-music with optional melody conditioning, sampling controls |
| AudioGen | medium (1.5B) | ~4 GB | Text-to-sound-effect generation |

### Voice Conversion (3 Providers)

These engines transform voice characteristics. **RVC and OpenVoice** are post-processing tools that take existing audio and change the voice (audio in → audio out). **GPT-SoVITS** is different — it generates new speech from text in a cloned voice (text in → audio out).

> **Voice Conversion vs. TTS Voice Reference:** Many TTS engines above (F5, Fish Speech, Zonos, etc.) also support voice cloning via a reference audio clip, but they are TTS engines that generate speech from text. The engines below are specifically designed for voice transformation or voice-cloned speech synthesis.

| Engine | Type | VRAM | Notes |
| --- | --- | --- | --- |
| RVC V2 | Audio → Audio | ~4 GB | Re-voices existing audio using a trained voice model (.safetensors). Pitch shift; F0 via YIN today (RMVPE/Harvest/PM coming). ContentVec encoder auto-downloads. |
| OpenVoice V2 | Audio → Audio | ~2 GB | Transfers the tone/style of a reference voice onto existing audio. Zero-shot (no model training, just a wav clip). |
| GPT-SoVITS | Text → Audio | ~4 GB | Generates new speech from text in a cloned voice using a reference clip + its transcript. English today (CJK pending). |

### Audio Processing (2 Providers, 5 Models)

| Engine | Models | VRAM | Notes |
| --- | --- | --- | --- |
| Demucs | htdemucs, htdemucs_ft, htdemucs_6s | ~2 GB | Source separation (vocals, drums, bass, other; 6-stem variant adds guitar + piano) |
| Resemble Enhance | denoise, enhance | ~2 GB | Speech denoising and super-resolution to 44.1 kHz (engine support pending — DeepSpeed checkpoint loader) |

## Performance (Pure-C# engine vs. Python reference)

Measured on an **NVIDIA RTX 3060 (12 GB)**, 2026-07-09. **Ours** = warm SwarmUI `ProcessTTS`/`ProcessSTT` engine time (2nd call, model resident — excludes load). **Python** = the official library on the same GPU, warm. **RTF** = gen-time ÷ audio-duration (lower is faster; < 1 = faster than real-time). **Diff** = ours ÷ Python. TTS prompt: *"The speech synthesizer is now working correctly."* STT clip: the 11 s JFK sample.

All tested TTS engines and both STT engines produce correct, intelligible output — re-verified 2026-07-13 through the canonical `GenerateText2Image` path with **whisper `medium.en`** as a proven oracle (not by ear alone), across short/long/numbers/punctuation prompts. The perf gap on the heavy diffusion/DiT/AR models was an engine-wide bottleneck: several conv layers (depthwise / grouped `Conv1d`, positional-embed) ran as host-side loops that force a GPU→host sync per call. **2026-07-13: fixed for F5-TTS** — its per-forward `F5ConvPosEmbed` grouped Conv1D (the ~99% wall-time cost) now routes to the GPU `backend.Conv1d`, cutting F5 from 142.7 s to ~6.4 s (**~22–34×**) at bit-parity. The same host→GPU-conv treatment is the remaining lever for the other Vocos/VITS-class models.

### Text-to-Speech

| Model | Ours (GPU) | Ours RTF | Python (GPU) | Python RTF | Diff | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| **Kokoro-82M** | 0.83 s | 0.26 | 0.10 s | 0.03 | ~8× | fast; small VITS-class |
| **FishSpeech 1.5** | 3.50 s | 1.06 | — | — | — | no pip lib to compare |
| **Chatterbox** | 5.59 s | 2.12 | — | — | — | no pip lib to compare |
| **NeuTTS-air** | 46 s | ~6 | — | — | — | voice-clone (needs a reference) |
| **VibeVoice 1.5B** | ~64–113 s | — | — | — | — | ⚠ **re-measure pending** (OOM-prone under load) |
| **Bark** | 185.7 s | 14.2 | — | — | — | output length varies (stochastic); no safe pip compare |
| **Dia 1.6B** | ~320 s | ~0.036 | — | — | 10/10 | **fixed 2026-07-15** (word-correct via `Dia-1.6B-0626` checkpoint; EOS-stops at 11.4 s); slowest TTS (dual-CFG F32) |
| **F5-TTS v1** | **6.4 s** (was 142.7 s) | ~2.4 | 3.47 s | 0.73 | ~1.8× | **host-conv bottleneck FIXED 07-13** (grouped Conv1D → GPU `backend.Conv1d`, bit-parity) |

### Speech-to-Text

| Model | Ours (GPU) | Ours RTF | Python (GPU) | Python RTF | Diff | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| **Whisper-base** | 1.04 s | 0.095 | 0.25 s | 0.022 | ~4× | correct transcription |
| **Moonshine-base** | 0.64 s | 0.059 | — | — | — | fastest STT; correct |
| **Distil-Whisper** | — | — | — | — | — | provider repo-id config bug (fix pending) |

> Python baselines are shown only where the official library installs and runs in this environment without OOM risk. Kokoro/F5 Python numbers predate a `neucodec` install that upgraded `transformers` to 5.x (which broke `kokoro`/`torchvision` imports); the remaining models have no pip package (Chatterbox, VibeVoice, FishSpeech, NeuTTS) or are too large to run alongside the resident stack on a 12 GB-VRAM / 31 GB-RAM box.

## Usage

1. **Add the Audio Backend** — Go to Server > Backends and add "Audio Backend".
2. **Install Models** — In Server > Backends, open the Audio Backend's engine panel and click an engine. Install the specific models you want with their per-model **Install** button, or click **Download All** to fetch the whole set; installed models show a **Remove** button that deletes just that variant's weights. The extension downloads weights and runs in-process on the C# engine — no virtual environment, no pip, no Python. Progress streams in real time via WebSocket. (Self-managed models fetch their weights on first use.)
3. **Select a Model** — Choose an installed audio model from the model selector.
4. **Set Parameters** — The sidebar shows relevant parameter groups (TTS, STT, Audio Generation, Voice Conversion, Audio Processing) based on the selected model.
5. **Generate** — Enter your prompt and click Generate. Audio output appears in the output area with a waveform player.
6. **Cancel** — Click "Stop Generation" to cancel the current generation or "Stop All Generations" to cancel all active sessions. Works for all providers.

### Streaming TTS

Set the **Stream Chunk Size** parameter to control how text is split for streaming:

| Mode | Behavior |
| --- | --- |
| `word` | Each word generates separately |
| `phrase` | ~5 words per chunk, snaps to nearby punctuation |
| `sentence` | Splits on `.` `!` `?` boundaries (respects abbreviations) |
| `paragraph` | Splits on double newlines, falls back to sentences |

Each chunk generates and plays back immediately while the next chunk processes. The final output is a concatenated WAV file saved to the output directory.

### TTS Voice Reference (Voice Cloning in TTS)

Many TTS engines accept a **reference audio** file (WAV) and optional **reference text** (transcript of the reference audio). Upload a short clip (~5–15 seconds) of the target voice. The model generates new speech from your text prompt that sounds like the reference voice.

Supported by: F5-TTS, Fish Speech, Pocket TTS, Kyutai TTS, Qwen3, Zonos, VibeVoice, Chatterbox, CosyVoice, NeuTTS.

This is different from the **Voice Conversion** engines (RVC, OpenVoice) which take existing audio and change the voice without generating new speech.

### Video + Audio

Use the API endpoints or UI to combine generated audio with video files or extract audio tracks from video. Requires ffmpeg on PATH.

- **Replace** mode swaps the video's audio track with your generated audio.
- **Mix** mode blends the original and new audio tracks together.

### Multi-Track DAW Editor

Click **Audio Lab** on any audio output to open the DAW editor. The editor opens near-fullscreen with the audio loaded as the first clip on Track 1.

**Layout:**
- **Transport Bar** — Record, rewind, play/stop, forward, loop toggle, time display, BPM, and zoom slider
- **Timeline Ruler** — Canvas-rendered time ruler with beat grid, playhead indicator, and draggable loop region handles
- **Track Headers** — Per-track controls: editable name, mute (M), solo (S), volume slider, arm (R), and remove (X) button
- **Clip Lanes** — Drag clips horizontally to reposition, or drag across tracks to move between lanes. Right-click clips for context menu (split at playhead, delete, duplicate, rename, mute/unmute)
- **Bottom Panel** — Tabbed panel with Clip Editor (details + actions for selected clip), Mixer (vertical faders, pan, mute/solo per track + master), and Apply to Model (set clip as voice reference for TTS)
- **Footer** — Add Track, Import Audio, Export Mixdown (WAV/MP3/OGG/FLAC/AAC), and Close

**Playback:** Uses the Web Audio API (`AudioBufferSourceNode`) for sample-accurate multi-track synchronized playback. WaveSurfer.js provides visual-only waveform rendering per clip. Loop regions wrap playback between start and end markers.

**Export:** Multi-track mixdown renders via `OfflineAudioContext` with per-track gain and pan. WAV exports directly from the browser. MP3, OGG, FLAC, and AAC formats route through the backend ffmpeg conversion endpoint.

**Keyboard Shortcuts:**
| Key | Action |
| --- | --- |
| Space | Play / Stop |
| Ctrl+Z | Undo |
| Ctrl+Shift+Z | Redo |
| Delete | Delete selected clip |

### Future Features (Roadmap)

Planned DAW upgrades, prioritized from producer feedback research (r/musicproduction, r/ableton, r/Reaper, r/Bitwig, r/FL_Studio):

- **Piano roll + playable instruments** — Scale-aware MIDI note editing (FL Studio-style: lock-to-key highlighting, ghost notes, grid-snapped editing) backed by a built-in sampler/synth. The single most-requested DAW feature across every community surveyed; requires an instrument engine, so it lands after the sample-based workflow matures.
- **Session-view loop jamming** — A non-linear clip-launch grid (Ableton-style) for auditioning loop combinations before committing them to the arrangement timeline.
- **Retrospective record** — An always-listening capture buffer (FL "Dump Score Log" style) so an improvised take on the beat pads or mic can be recovered *after* the fact, without having armed record.
- **Comping & takes** — Loop-record multiple takes into stacked lanes and assemble the best parts; take lanes double as recoverable edit history (Pro Tools playlist-style).
- **True auto-warp** — Full transient-based warping of imported audio to the project grid (the current Conform-to-BPM auto-detects tempo and stretches uniformly; warping adds per-transient alignment and stretch-quality modes).
- **Parameter modulation** — Assignable LFO/envelope modulators on any FX knob (Bitwig-style "modulate anything"), building on the existing automation engine.
- **Sound Palette upgrades** — Starred favorites that persist across projects, audition-at-project-tempo, and prompt presets for common one-shots and loop styles.
- **Demucs weight auto-download** — One-click stems installs fetch the htdemucs checkpoint automatically instead of requiring manual placement in the model folder.
- **Full FX-parameter automation** — Extend the volume/pan envelope lanes to any effect parameter.

## API Endpoints

All endpoints require authentication and use SwarmUI's permission system.

### Audio Processing

| Endpoint | Method | Description |
| --- | --- | --- |
| `ProcessAudio` | POST | Generic entry point — routes to any provider by `provider_id` |
| `ProcessTTS` | POST | Text-to-speech with `text`, `voice`, `language`, `volume` params |
| `ProcessSTT` | POST | Speech-to-text with `audio_data` (base64), `language` params |
| `ProcessWorkflow` | POST | Chain multiple operations (e.g., STT then TTS) with ordered steps |

### Engine Management

| Endpoint | Method | Description |
| --- | --- | --- |
| `AudioLabListEngines` | GET | List all engines with install status, models, and metadata |
| `AudioLabInstallEngine` | POST (WS) | Install an engine (download weights) with real-time WebSocket progress streaming |
| `AudioLabUninstallEngine` | POST | Remove engine from registry (optionally delete its weights) |
| `GetAllProvidersStatus` | GET | List all registered providers with metadata |
| `GetInstallationStatus` | GET | Per-provider install status |
| `GetInstallationProgress` | GET | Poll real-time installation/download progress |

### Audio Format Conversion

| Endpoint | Method | Description |
| --- | --- | --- |
| `ConvertAudioFormat` | POST | Convert WAV audio to MP3, OGG, FLAC, AAC, or M4A via ffmpeg. Used by DAW export. |

### Video + Audio

| Endpoint | Method | Description |
| --- | --- | --- |
| `CombineVideoAudio` | POST | Merge audio track into video (replace or mix mode), 200 MB video / 50 MB audio limit |
| `ExtractAudioFromVideo` | POST | Extract audio track as 16-bit PCM WAV at 44.1 kHz, 200 MB video limit |

### Permissions

| Permission | Level | Covers |
| --- | --- | --- |
| `audio_process` | Power Users | ProcessAudio, ProcessTTS, ProcessSTT, ProcessWorkflow, CombineVideoAudio, ExtractAudioFromVideo, ConvertAudioFormat |
| `audio_manage_backends` | Power Users | AudioLabInstallEngine, AudioLabUninstallEngine |
| `audio_check_status` | Power Users | GetAllProvidersStatus, GetInstallationStatus, GetInstallationProgress, AudioLabListEngines |

## Architecture

```
SwarmUI-AudioLab/
├── AudioLab.cs                          # Extension entry point
├── AudioLabParams.cs                    # T2I parameter registration + BuildEngineArgs param→engine mapping
├── AudioAPI/
│   ├── AudioLabAPI.cs                   # API endpoints (process, install, status)
│   ├── AudioParameters.cs               # Shared parameter helpers
│   ├── AudioProgressTracking.cs         # Install/generation progress
│   └── VideoAudioEndpoints.cs           # Video+audio combining/extraction via ffmpeg
├── AudioBackends/
│   └── DynamicAudioBackend.cs           # Unified routing backend (model routing, streaming, cancellation, install)
├── AudioProviders/
│   ├── AudioProviderDefinitions.cs      # Provider registry (auto-discovers all IAudioProviderSource)
│   ├── KokoroProvider.cs                # One file per provider — engine-backed (Kokoro, Chatterbox, ACE-Step, …)
│   ├── ElevenLabsTTSProvider.cs         #   and cloud-API providers (ElevenLabs, Azure, OpenAI, …)
│   └── ...                              # ~56 provider files total
├── AudioProviderTypes/
│   ├── AudioCategory.cs                 # TTS, STT, AudioGeneration, VoiceConversion, AudioProcessing
│   ├── AudioProviderDefinition.cs       # Provider definition schema
│   ├── AudioModelDefinition.cs          # Per-model metadata (id, license, source URL, size, VRAM)
│   ├── AudioProviderDefinitionBuilder.cs # Fluent builder for provider definitions
│   └── IAudioProviderSource.cs          # Provider interface
├── AudioServices/                       # The in-process C# inference layer (HartsyInference engine)
│   ├── AudioEngine.cs                   # Dispatch table: provider id → handler; owns the compute device
│   ├── AudioWeights.cs / AudioWeightsRegistry.cs # Download URLs + on-disk weight resolution
│   ├── AudioServerManager.cs            # Routes a request to the engine (or a cloud API handler)
│   ├── AudioUnsupportedReasons.cs       # Precise "not runnable yet" messages
│   ├── Tts/  Stt/  Music/  Vc/  Fx/     # Per-category handlers + model descriptors
│   │   └── Models/                      #   one descriptor per model (repo + how to load + how to synth)
│   └── ApiHandlers/                     # Cloud-API providers (Azure, Google, Suno, Udio, …)
├── Assets/
│   ├── audio-core.js                    # Frontend UI (engine browser, param groups)
│   ├── audio-api.js                     # API client (backend communication)
│   ├── audio-player.js                  # Waveform player (WaveSurfer.js)
│   ├── audio-daw*.js                    # DAW editor (orchestrator, tracks, timeline, mixer)
│   ├── audio-integration.js             # SwarmUI integration hooks
│   ├── audio-lab.css                    # Styling (theme-aware)
│   └── lib/                             # WaveSurfer, Crunker, Timeline, Minimap plugins
└── README.md
```

The extension follows a two-layer architecture (no Python, no separate server process):

- **C# layer** registers providers with a fluent builder API, manages the routing backend lifecycle, routes generation requests by model prefix, maps UI parameters to engine arguments (`BuildEngineArgs`), and runs inference **in-process** via the [HartsyInference](https://www.nuget.org/packages/HartsyInference) engine. `AudioEngine` holds a dispatch table from provider id to a per-category handler (TTS/STT/Music/VC/FX); each handler drives a per-model descriptor that knows the model's HuggingFace repo and how to load + run it. Cloud-API providers route through `ApiHandlers/` instead. Weights download and cache to the Audio Model Root; pipelines stay resident in GPU/CPU memory between requests.
- **Frontend** adds audio parameter groups to the Generate tab sidebar, provides a waveform-based audio player via WaveSurfer.js, and integrates with SwarmUI's generation lifecycle (model selection, parameter visibility, streaming playback, cancellation).

### Cancellation

When the user clicks Stop Generation, SwarmUI fires the session's `InterruptToken`. The C# layer observes it and cancels in two ways:

1. **Infrastructure** — the in-process generation call is passed the cancellation token; the engine aborts at the next checkpoint and the result is discarded.
2. **Cooperative** — pipelines with iterative loops check the token periodically for fast mid-inference cancellation.

Both "Stop Generation" (current session) and "Stop All Generations" (all sessions) work through the same token mechanism. For single-shot calls (e.g. Bark), the computation may finish before the cancel arrives — the result is still discarded.

## Backend Settings

| Setting | Default | Description |
| --- | --- | --- |
| Audio Model Root | `Models/audio` | Storage path for downloaded audio model weights |
| Auto Redownload Missing Weights | `true` | If a model's weights are missing at generation time (e.g. deleted to free space), re-download them automatically; when off, generation refuses with a clear message |
| Debug Mode | `false` | Enable verbose audio engine logging |
| Use Docker | `false` | Legacy flag from the old Python backend; the C# engine runs in-process and does not use it |

## Troubleshooting

**Engine install / weight download fails:**
- Check that you have a stable internet connection for downloading model weights
- Check the SwarmUI server logs for detailed error output
- Some models aren't runnable on the C# engine yet — the install/generate error names the exact missing piece

**Gated model access denied:**
- Some models (e.g., certain Fish Speech or Qwen3 variants) require accepting a license agreement on HuggingFace
- Go to the model's HuggingFace page, accept the agreement, then set your HuggingFace token in SwarmUI: Server > User Settings > API Keys
- Get a token at https://huggingface.co/settings/tokens (needs "Read" permission)

**A model says "not runnable in the C# engine yet":**
- That model's pipeline exists but is missing a specific prerequisite (a tokenizer asset, phonemizer, or confirmed weight layout). The message names it. Pick another model in the same category in the meantime.

**No audio output:**
- Verify the engine is installed (check the model browser for audio models)
- Check that the Audio Backend is running (Server > Backends)
- Look at the SwarmUI server logs for `[AudioLab]` errors

**Video+audio features not working:**
- Install ffmpeg and ensure it is on your system PATH

**Stop Generation not working:**
- Ensure the Audio Backend is running and healthy (check Server > Backends)
- For single-call engines (e.g., Bark), the GPU computation may finish before the cancel signal arrives — the result is still discarded

## License

MIT License - see [LICENSE](LICENSE) for details.

## Acknowledgments

- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) — Base platform
- [WaveSurfer.js](https://wavesurfer.xyz/) — Audio waveform visualization (player + DAW clip rendering)
- [Crunker](https://github.com/jaggad/crunker) — Audio concatenation
- [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) — FFmpeg wrapper for audio format conversion
