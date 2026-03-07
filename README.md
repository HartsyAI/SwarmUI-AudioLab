# SwarmUI AudioLab Extension

A modular audio processing extension for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) that adds text-to-speech, speech-to-text, music generation, voice cloning, audio effects, and sound effects — all through a provider-based architecture integrated directly into the Generate tab.

## Features

- **Text-to-Speech (TTS)** — 12 providers including Chatterbox, Kokoro, Bark, Orpheus, Piper, Dia, F5-TTS, Zonos, CSM, VibeVoice, CosyVoice, and NeuTTS
- **Speech-to-Text (STT)** — 4 providers: Whisper, Distil-Whisper, Moonshine, and RealtimeSTT
- **Music Generation** — ACE-Step 1.5 (6 DiT models, 6 task types, lyrics alignment, 50 languages) and MusicGen (text-to-music with melody conditioning)
- **Voice Cloning** — OpenVoice, RVC, and GPT-SoVITS
- **Audio Effects** — Demucs (stem separation) and Resemble Enhance (audio enhancement/denoising)
- **Sound Effects** — AudioGen (text-to-sound-effect generation)
- **Video + Audio** — Combine audio with video or extract audio from video via ffmpeg
- **Streaming TTS** — Chunked text-to-speech with auto-play for immediate playback while generating
- **On-Demand Engine Installation** — Install only the engines you need, each in its own Python virtual environment
- **Docker Support** — Linux-only engines (RVC, GPT-SoVITS, Resemble-Enhance, CosyVoice, RealtimeSTT) can run via Docker on Windows

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

### Text-to-Speech

| Engine | Voice Cloning | Streaming | Notes |
| --- | --- | --- | --- |
| Chatterbox | Reference audio | Yes | Expressive with exaggeration/CFG controls |
| Kokoro | No | Yes | Fast, multiple built-in voices, speed control |
| Piper | No | Yes | CPU-only ONNX, lightweight, auto-downloads voices |
| Bark | No | Yes | Multi-language, emotion support |
| Orpheus | No | Yes | Emotion tags (`<laugh>`, `<sigh>`, etc.) |
| Dia | No | Yes | CFG-filtered generation |
| F5-TTS | Reference audio | Yes | Flow-matching, speed control |
| CSM | No | Yes | Multi-speaker conversations |
| VibeVoice | Reference audio | Yes | Diffusion-based, high quality |
| Zonos | Reference audio | Yes | Emotion control, multi-language |
| CosyVoice | Reference audio | Yes | Zero-shot cloning, multi-language (requires Docker on Windows) |
| NeuTTS | Reference audio (required) | Yes | Instant voice cloning |

### Speech-to-Text

| Engine | Notes |
| --- | --- |
| Whisper | OpenAI Whisper, 7 model sizes (tiny through turbo), transcribe + translate tasks, multi-language |
| Distil-Whisper | Faster distilled variant |
| Moonshine | Lightweight alternative |
| RealtimeSTT | Real-time transcription (requires Docker on Windows) |

### Music Generation

| Engine | Notes |
| --- | --- |
| ACE-Step 1.5 | 6 DiT model variants (turbo/sft/base), 6 task types (text2music, cover, repaint, extract, lego, complete), lyrics alignment, 50 languages, optional LM planner, native Windows + Docker |
| MusicGen | 10 model variants (mono/stereo/melody), text-to-music with optional melody conditioning, sampling controls |

### Voice Cloning

| Engine | Notes |
| --- | --- |
| OpenVoice | Voice style transfer |
| RVC | Pitch shift, F0 extraction (RMVPE/PM/Harvest/CREPE) (requires Docker on Windows) |
| GPT-SoVITS | Multi-language cloning (en/zh/ja/ko) (requires Docker on Windows) |

### Audio Effects

| Engine | Notes |
| --- | --- |
| Demucs | Source separation (vocals, drums, bass, other) |
| Resemble Enhance | Audio enhancement and super-resolution (requires Docker on Windows) |

### Sound Effects

| Engine | Notes |
| --- | --- |
| AudioGen | Generate sound effects from text descriptions |

## Usage

1. **Add the Audio Backend** — Go to Server > Backends and add "Audio Backend". Configure Docker if needed.
2. **Install an Engine** — In the Generate tab, browse the audio models and click Install for the engine you want. The extension creates a virtual environment and installs all dependencies automatically.
3. **Select a Model** — Choose an installed audio model from the model selector.
4. **Set Parameters** — The sidebar will show relevant parameter groups (TTS, STT, Music Generation, Voice Clone, Audio FX, Sound FX) based on the selected model.
5. **Generate** — Enter your prompt and click Generate. Audio output appears in the output area with a waveform player.

### Streaming TTS

Set the **Stream Chunk Size** parameter to a value greater than 0 to enable streaming. Text is split into chunks that generate and play back immediately while the next chunk processes. The final output is a concatenated WAV file.

### Video + Audio

Use the API endpoints to combine generated audio with video files or extract audio tracks from video. Requires ffmpeg.

## Architecture

```
SwarmUI-AudioLab/
├── AudioLab.cs                          # Extension entry point
├── AudioLabParams.cs                    # T2I parameter registration
├── AudioAPI/
│   ├── AudioLabAPI.cs                   # API endpoints (process, install, status)
│   └── VideoAudioEndpoints.cs           # Video+audio combining via ffmpeg
├── AudioBackends/
│   └── DynamicAudioBackend.cs           # Unified backend, model routing, streaming
├── AudioProviders/
│   ├── AudioProviderDefinitions.cs      # Provider registry
│   ├── ChatterboxProvider.cs            # One file per engine
│   ├── KokoroProvider.cs
│   └── ...
├── AudioProviderTypes/
│   ├── AudioCategory.cs                 # TTS, STT, MusicGen, VoiceClone, AudioFX, SoundFX
│   ├── AudioProviderDefinition.cs       # Provider definition schema
│   └── IAudioProviderSource.cs          # Provider interface
├── AudioServices/
│   ├── AudioServerManager.cs            # Python server lifecycle
│   ├── AudioDependencyInstaller.cs      # pip dependency management
│   └── VenvManager.cs                   # Per-engine-group virtual environments
├── Assets/
│   ├── audio-core.js                    # Frontend UI
│   ├── audio-api.js                     # API client
│   ├── audio-player.js                  # Waveform player (WaveSurfer)
│   ├── audio-integration.js             # SwarmUI integration
│   ├── audio-lab.css                    # Styling
│   └── lib/                             # WaveSurfer, Crunker
├── python_backend/
│   ├── audio_server.py                  # HTTP server (stdlib http.server)
│   ├── engine_registry.py               # Engine discovery
│   ├── engines/
│   │   ├── base_engine.py               # Base engine class
│   │   ├── tts_chatterbox.py            # One file per engine
│   │   ├── tts_kokoro.py
│   │   ├── stt_whisper.py
│   │   ├── music_acestep.py
│   │   ├── clone_rvc.py
│   │   ├── fx_demucs.py
│   │   ├── sfx_audiogen.py
│   │   └── ...
│   └── docker/
│       ├── Dockerfile
│       └── docker-compose.yml
└── README.md
```

The extension follows a provider-based architecture:

- **C# layer** registers providers, manages the backend lifecycle, routes generation requests by model prefix, and handles parameter mapping.
- **Python layer** runs a lightweight HTTP server (Python's built-in `http.server`) per engine group, keeping models loaded in GPU memory between requests. Each engine implements a common base class and is loaded on-demand.
- **Frontend** adds audio parameter groups to the Generate tab sidebar and provides a waveform-based audio player via WaveSurfer.js.

## Backend Settings

| Setting | Default | Description |
| --- | --- | --- |
| Use Docker | `false` | Enable Docker for Linux-only engines on Windows |
| Audio Model Root | `Models/audio` | Storage path for downloaded audio models |
| Timeout Seconds | `300` | Max wait time for audio generation |
| Debug Mode | `false` | Enable verbose logging |

## Troubleshooting

**Engine install fails:**
- Ensure Python 3.10+ is on your system PATH (`python --version` or `python3 --version`)
- Check that you have a stable internet connection for downloading model weights
- Check the SwarmUI server logs for detailed error output

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

## License

MIT License - see [LICENSE](LICENSE) for details.

## Acknowledgments

- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) — Base platform
- [WaveSurfer.js](https://wavesurfer.xyz/) — Audio waveform visualization
- [Crunker](https://github.com/jaggad/crunker) — Audio concatenation
