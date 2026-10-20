# Audio Lab DAW audit (2026-10-20)

Scope: the DAW (`Assets/audio-daw*.js`, `audio-editor.js`, `audio-api.js`, `Tabs/Text2Image/Audio Lab.html`) and the
server paths it calls. Every DAW module was read in full. The live site (swarm.hartsy.ai) serves exactly
origin/main's assets (byte-compared), so these findings apply to production.

## Confirmed bugs (broken flows)

| # | Flow | What happens | Cause | Fix |
|---|------|--------------|-------|-----|
| 1 | Every Generate flow with no Audio Backend | Generate tab says "No engine installed" even with weights on disk; a direct generation fails with `Invalid model value ... 'Audio Models/Piper/default'` | `installed` comes from the backend; the `Audio Models/...` names only exist once an Audio Backend registers them. `AudioLabListEngines` reports `backend_status: NOT_FOUND` but the DAW ignores it | Read `backend_status`; show a setup call to action instead of "no engine" |
| 2 | Music with "Match project tempo" (on by default), every Sound Palette loop | `Invalid value for param Text2Audio Time Signature - '4/4'` | DAW sends `text2audiotimesignature: "4/4"`; core only accepts `2`, `3`, `4`, `6` (beats per bar) | Send the numerator, only when it is in that set |
| 3 | MusicGen, Stable Audio (Music category) | Generates with an empty prompt | DAW puts the description in `text2audiostyle` and sends Prompt = lyrics (empty). `DynamicAudioBackend` reads Prompt for MusicGen, Stable Audio and AudioGen; only ACE-Step, YuE, YuE2, HeartLib and MiniMax read Style | Route prompt/style per engine |
| 4 | Project load / crash resume | Drum machine pads go silent | `restoreProject` puts lane blobs in `blobStore`, then calls `blobStore.clear()` | Clear first |
| 5 | Score metadata on generated clips | Style field always empty | `buildClipScoreMeta` reads `params.textaudiostyle` (missing the 2) | Fix the key |
| 6 | Stems | "HTDemucs Fine-tuned" is offered but the engine only ships `htdemucs` and `htdemucs_6s` | Hard-coded `STEM_MODELS` | Build the list from the engine list |
| 7 | Transport under ~1500 px wide | BPM, snap, Project, Import, Export and Close are clipped and unreachable | `.daw-transport` never wraps, container is `overflow: hidden` | Wrap or scroll |
| 8 | First use | No way to open the DAW | Tab stays hidden until an autosave, saved project or audio result exists | Keep the tab reachable |
| 9 | Sound Palette variation | Wrong duration | Estimated from blob size as if raw float audio | Use the decoded duration |
| 10 | Export vs playback | Reverb differs between them | Impulse response built with `Math.random()` per chain | Seeded generator |
| 11 | Undo | Time signature and drum pattern are not restored | Not in the snapshot | Add them |

Still to verify live: "Save WAV to Swarm Outputs" (`AddImageToHistory` with an audio data URL; core has a TODO for
audio there) and the Demucs install-status path with no backend.

## Mics and instruments (the input gap)

- Recording uses MediaRecorder through WaveSurfer.Record: lossy Opus, not PCM.
- No latency compensation. A take is placed at the button-press time, playback starts only after the mic opens,
  so overdubs land out of sync with existing tracks.
- Only the first armed track records. No multitrack recording.
- One global input device. No per-track input, no channel choice (L, R, stereo), no input monitoring, no input
  meter before recording.
- No metronome, count-in, pre-roll or punch in/out.
- A MIDI keyboard only writes notes into the Score tab. No instrument tracks, MIDI clips, piano roll or software
  instrument.

## Gaps against Pro Tools

| Area | Pro Tools | Audio Lab today |
|------|-----------|-----------------|
| Track header | Number, R / I / S / M, view selector, automation mode, inserts A-E, I/O, volume and pan readouts | Name, M, S, volume slider, R, pan knob, automation toggle, meter |
| Rulers | Bars and Beats, Min:Secs, Tempo, Chords, Markers stacked | One ruler, time or bars |
| Markers and sections | Yes | None |
| Counters | Main and Sub, selection Start / End / Length | Time plus bars.beats |
| Edit modes and tools | Shuffle, Spot, Slip, Grid; zoom, trim, select, grab, scrub, pencil, smart tool | Snap toggle; drag and trim |
| Grid and nudge | Selectable values | Grid follows zoom; no nudge |
| Selection | Time range across tracks | Playhead and loop only |
| Clips | Fade handles, crossfades, clip gain line and readout | Numeric fades in the Clip Editor |
| Automation | Read / write / touch / latch on many parameters | Volume and pan breakpoints, draw only |
| Mixer | Inserts, sends, buses, I/O, groups | Pan, fader, meter, M, S; FX in a separate tab |
| Signal flow | Inserts pre-fader | FX after the fader, so a compressor reacts to fader moves |
| Export | Bounce a range, stems, 24-bit and 32-bit float | Full mixdown, 16-bit WAV, plus conversions |
| Long sessions | Virtualized drawing | Ruler is one canvas of duration x zoom pixels (blank past the browser canvas limit); every clip decodes its own WaveSurfer copy |

## Priority

1. Bugs 1 to 7.
2. Recording engine: AudioWorklet PCM capture on the audio clock with latency compensation, per-track inputs,
   monitoring and input meters, multitrack arm, click and count-in, punch in/out.
3. Range selection with Start / End / Length, markers ruler, stacked bars and time rulers.
4. Instrument tracks: MIDI clips, piano roll, built-in synth and sampler, Web MIDI input.
5. Track header parity, clip fade handles and clip gain, inserts pre-fader.
6. Sends and buses, automation write modes, stem export.

## Testing without restarting shared servers

Extension JS is cached in memory by release builds of SwarmUI, so every front-end change normally needs a
restart. Instead, run a local reverse proxy that serves this branch's `Assets/` and tab HTML and forwards
everything else (including WebSockets) to a server that has an Audio Backend and models, such as the live site.
