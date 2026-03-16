# SwarmUI AudioLab Extension

A modular audio processing extension for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) that adds text-to-speech, speech-to-text, audio generation, voice conversion, and audio processing — all through a provider-based architecture integrated directly into the Generate tab.

## Features

- **Text-to-Speech (TTS)** — 14 providers: Chatterbox, Kokoro, Bark, Orpheus, Piper, Dia, F5-TTS, Fish Speech, Qwen3, Zonos, CSM, VibeVoice, CosyVoice, and NeuTTS
- **Speech-to-Text (STT)** — 4 providers: Whisper, Distil-Whisper, Moonshine, and RealtimeSTT
- **Audio Generation** — ACE-Step 1.5 (6 DiT models, 6 task types, lyrics alignment, 50 languages), MusicGen (text-to-music with melody conditioning), and AudioGen (text-to-sound-effects)
- **Voice Conversion** — RVC (re-voice existing audio), OpenVoice (tone/style transfer), GPT-SoVITS (TTS with cloned voice)
- **Audio Processing** — Demucs (stem separation) and Resemble Enhance (audio enhancement/denoising)
- **Multi-Track DAW Editor** — Full digital audio workstation with timeline, transport controls, per-track mute/solo/volume, clip arrangement via drag-and-drop, mixer panel, loop regions, undo/redo, and multi-track mixdown export (WAV/MP3/OGG/FLAC/AAC)
- **Video + Audio** — Combine audio with video or extract audio from video via ffmpeg
- **Streaming TTS** — Chunked text-to-speech with auto-play for immediate playback while generating
- **Generation Cancellation** — Stop Generation and Stop All Generations buttons work for all audio providers
- **On-Demand Engine Installation** — Install only the engines you need, each in its own Python virtual environment
- **Docker Support** — Linux-only engines (RVC, GPT-SoVITS, Resemble Enhance, CosyVoice, RealtimeSTT) can run via Docker on Windows

## Requirements

- SwarmUI installed and working
- Python 3.10+ available on your system PATH (used to create per-engine virtual environments)
- ffmpeg on PATH (for video+audio features)
- Docker with NVIDIA Container Toolkit (optional, for Linux-only engines on Windows)

## Installation

1. Clone into the SwarmUI extensions directory:
   ```bash
   cd /path/to/SwarmUI/src/Extensions/
   git clone https://github.com/Hartsy/SwarmUI-AudioLab.git
   ```

2. Restart SwarmUI. The extension loads automatically.

3. In SwarmUI, go to **Server** > **Backends** and add the **Audio Backend**.

4. Open the Generate tab, select an audio model, and use the Install button to install the engine you want. Dependencies are installed automatically into an isolated virtual environment.

## Supported Engines

### Text-to-Speech (14 Providers, 30+ Models)

| Engine | Voice Reference | Streaming | VRAM | Notes |
| --- | --- | --- | --- | --- |
| Chatterbox | Optional | Yes | ~4 GB | Expressive with exaggeration/CFG controls |
| Kokoro | No | Yes | ~1 GB | 96x real-time on GPU, CPU-capable, multiple built-in voices |
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
| CosyVoice | Optional | Yes | ~8 GB | Ultra-low latency streaming, multilingual (Docker required on Windows) |
| NeuTTS | Required | Yes | ~2 GB | 0.5B params, instant voice cloning, CPU-capable |

### Speech-to-Text (4 Providers, 12 Models)

| Engine | Models | VRAM | Notes |
| --- | --- | --- | --- |
| Whisper | 7 sizes (tiny–turbo) | 1–10 GB | OpenAI Whisper, transcribe + translate, multi-language |
| Distil-Whisper | large-v3, large-v3.5 | ~2 GB | 6x faster than Whisper large-v3 |
| Moonshine | base, tiny | ~1 GB / CPU | Lightweight, CPU-capable |
| RealtimeSTT | default | ~2 GB | Real-time streaming with wake word detection (Docker required on Windows) |

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
| RVC V2 | Audio → Audio | ~4 GB | Re-voices existing audio using a trained .pth voice model. Pitch shift, F0 extraction (RMVPE/PM/Harvest/CREPE). Docker required on Windows. |
| OpenVoice V2 | Audio → Audio | ~2 GB | Transfers the tone/style of a reference voice onto existing audio. Zero-shot (no model training, just a wav clip). |
| GPT-SoVITS | Text → Audio | ~4 GB | Generates new speech from text in a cloned voice using ~1 min reference audio. CJK + English. Docker required on Windows. |

### Audio Processing (2 Providers, 5 Models)

| Engine | Models | VRAM | Notes |
| --- | --- | --- | --- |
| Demucs | htdemucs, htdemucs_ft, htdemucs_6s | ~2 GB | Source separation (vocals, drums, bass, other; 6-stem variant adds guitar + piano) |
| Resemble Enhance | denoise, enhance | ~2 GB | Speech denoising and super-resolution to 44.1 kHz, Docker required on Windows |

## Usage

1. **Add the Audio Backend** — Go to Server > Backends and add "Audio Backend". Configure Docker if needed.
2. **Install an Engine** — In the Generate tab, browse the audio models and click Install for the engine you want. The extension creates a virtual environment and installs all dependencies automatically. Progress streams in real time via WebSocket.
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

Supported by: F5-TTS, Fish Speech, Qwen3, Zonos, VibeVoice, Chatterbox, CosyVoice, NeuTTS.

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
| `AudioLabListEngines` | GET | List all engines with install status, models, dependencies, and Docker requirements |
| `AudioLabInstallEngine` | POST (WS) | Install engine with real-time WebSocket progress streaming |
| `AudioLabUninstallEngine` | POST | Remove engine from registry (does not delete venv) |
| `GetAllProvidersStatus` | GET | List all registered providers with metadata |
| `GetInstallationStatus` | GET | Check Python availability and per-provider install status |
| `GetInstallationProgress` | GET | Poll real-time installation progress (percentage, current package) |
| `InstallProviderDependencies` | POST | Install pip dependencies for a provider |

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
| `audio_manage_backends` | Power Users | InstallProviderDependencies, AudioLabInstallEngine, AudioLabUninstallEngine |
| `audio_check_status` | Power Users | GetAllProvidersStatus, GetInstallationStatus, GetInstallationProgress, AudioLabListEngines |

## Architecture

```
SwarmUI-AudioLab/
├── AudioLab.cs                          # Extension entry point
├── AudioLabParams.cs                    # T2I parameter registration
├── AudioAPI/
│   ├── AudioLabAPI.cs                   # API endpoints (process, install, status)
│   └── VideoAudioEndpoints.cs           # Video+audio combining/extraction via ffmpeg
├── AudioBackends/
│   └── DynamicAudioBackend.cs           # Unified backend, model routing, streaming, cancellation
├── AudioProviders/
│   ├── AudioProviderDefinitions.cs      # Provider registry (auto-discovers all IAudioProviderSource)
│   ├── ChatterboxProvider.cs            # One file per engine (14 TTS + 4 STT + 2 MusicGen + ...)
│   ├── KokoroProvider.cs
│   ├── FishSpeechProvider.cs
│   ├── Qwen3TTSProvider.cs
│   └── ...                              # 26 provider files total
├── AudioProviderTypes/
│   ├── AudioCategory.cs                 # TTS, STT, AudioGeneration, VoiceConversion, AudioProcessing
│   ├── AudioProviderDefinition.cs       # Provider definition schema
│   ├── AudioProviderDefinitionBuilder.cs # Fluent builder for provider definitions
│   └── IAudioProviderSource.cs          # Provider interface
├── AudioServices/
│   ├── AudioServerManager.cs            # Python server lifecycle, HTTP client, cancel support
│   ├── AudioDependencyInstaller.cs      # pip dependency management
│   └── VenvManager.cs                   # Per-engine-group virtual environments
├── Assets/
│   ├── audio-core.js                    # Frontend UI (engine browser, param groups)
│   ├── audio-api.js                     # API client (backend communication)
│   ├── audio-player.js                  # Waveform player (WaveSurfer.js)
│   ├── audio-editor.js                  # Audio editor modal (delegates to DAW)
│   ├── audio-daw.js                     # DAW orchestrator (modal, transport, state, playback, undo/redo, export)
│   ├── audio-daw-track.js              # Track class (header UI, clip lane, drag-drop, WaveSurfer rendering)
│   ├── audio-daw-timeline.js           # Timeline ruler (canvas, beat grid, zoom, loop markers, playhead)
│   ├── audio-daw-mixer.js              # Mixer panel (per-track faders, pan, mute/solo, master bus)
│   ├── audio-integration.js             # SwarmUI integration hooks
│   ├── audio-lab.css                    # Styling (theme-aware, uses CSS custom properties)
│   └── lib/                             # WaveSurfer, Crunker, Timeline, Minimap plugins
├── python_backend/
│   ├── audio_server.py                  # Threaded HTTP server with /process, /cancel, /download endpoints
│   ├── engine_registry.py               # Engine discovery and caching
│   ├── engines/
│   │   ├── base_engine.py               # Base class (model download, cancellation, audio encoding)
│   │   ├── tts_chatterbox.py            # One file per engine
│   │   ├── tts_kokoro.py
│   │   ├── tts_fishspeech.py
│   │   ├── tts_qwen3.py
│   │   ├── stt_whisper.py
│   │   ├── music_acestep.py
│   │   ├── clone_rvc.py
│   │   ├── fx_demucs.py
│   │   ├── sfx_audiogen.py
│   │   └── ...                          # 26 engine files total
│   └── docker/
│       ├── Dockerfile
│       └── docker-compose.yml
└── README.md
```

The extension follows a three-layer architecture:

- **C# layer** registers providers with a fluent builder API, manages the backend lifecycle, routes generation requests by model prefix, handles parameter mapping, and manages cancellation tokens.
- **Python layer** runs a threaded HTTP server (Python's built-in `http.server` with `ThreadingMixIn`) per engine group, keeping models loaded in GPU memory between requests. Each engine extends `BaseAudioEngine` and is loaded on-demand. The server supports concurrent request handling (allowing `/cancel` to arrive while `/process` is running).
- **Frontend** adds audio parameter groups to the Generate tab sidebar, provides a waveform-based audio player via WaveSurfer.js, and integrates with SwarmUI's generation lifecycle (model selection, parameter visibility, streaming playback, cancellation).

### Engine Groups

Engines are organized into groups that share a Python virtual environment:

| Group | Engines | Notes |
| --- | --- | --- |
| `main` | 17 providers (Kokoro, Piper, F5, Fish Speech, Qwen3, Bark, NeuTTS, Orpheus, Dia, CSM, VibeVoice, Zonos, Whisper, Distil-Whisper, Moonshine, Demucs, OpenVoice) | Shared venv, most engines |
| `chatterbox` | Chatterbox | Isolated due to dependency conflicts |
| `audiocraft` | MusicGen, AudioGen | Shared AudioCraft dependencies |
| `acestep` | ACE-Step | Isolated venv |
| `linux_docker` | CosyVoice, RealtimeSTT, RVC, GPT-SoVITS, Resemble Enhance | Docker containers on Windows, native venv on Linux |

### Cancellation

Cancellation is built into all 26 engines through a three-layer system:

1. **Infrastructure** — When the user clicks Stop Generation, SwarmUI fires the session's `InterruptToken`. The C# layer detects this, cancels the HTTP request, and sends a `/cancel/{id}` request to the Python server. The server marks the result as cancelled.
2. **Cooperative** — Engines with iterative processing loops (Fish Speech, Kokoro, Piper, CosyVoice, Zonos, Qwen3) call `self.is_cancelled()` periodically for fast mid-inference cancellation.
3. **Session** — Both "Stop Generation" (current session) and "Stop All Generations" (all sessions) work automatically through the same token mechanism.

## Backend Settings

| Setting | Default | Description |
| --- | --- | --- |
| Use Docker | `false` | Enable Docker for Linux-only engines on Windows |
| Audio Model Root | `Models/audio` | Storage path for downloaded audio models |
| Timeout Seconds | `300` | Max wait time per audio generation request |
| Debug Mode | `false` | Enable verbose Python server logging |

## Troubleshooting

**Engine install fails:**
- Ensure Python 3.10+ is on your system PATH (`python --version` or `python3 --version`)
- Check that you have a stable internet connection for downloading model weights
- Check the SwarmUI server logs for detailed error output

**Gated model access denied:**
- Some models (e.g., certain Fish Speech or Qwen3 variants) require accepting a license agreement on HuggingFace
- Go to the model's HuggingFace page, accept the agreement, then set your HuggingFace token in SwarmUI: Server > User Settings > API Keys
- Get a token at https://huggingface.co/settings/tokens (needs "Read" permission)

**Docker engines not available on Windows:**
- Enable "Use Docker" in the Audio Backend settings
- Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) with WSL2 backend
- Install [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) for GPU support

**No audio output:**
- Verify the engine is installed (check the model browser for audio models)
- Check that the Audio Backend is running (Server > Backends)
- Look at the SwarmUI server logs for Python errors

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
