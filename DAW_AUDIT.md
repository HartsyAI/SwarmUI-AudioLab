# Audio Lab DAW audit (2026-10-20)

Scope: the DAW (`Assets/audio-daw*.js`, `audio-editor.js`, `audio-api.js`, `Tabs/Text2Image/Audio Lab.html`) and the
server paths it calls. Every DAW module was read in full. The live site (swarm.hartsy.ai) served exactly
origin/main's assets (byte-compared), so these findings applied to production.

Status: every bug below is fixed on the `daw/pro-overhaul` branch; the last column names the commit. What is still
open is under [Remaining gaps](#remaining-gaps).

## Broken flows found by the audit

| # | Flow | What happened | Cause | Fix | Commit |
|---|------|---------------|-------|-----|--------|
| 1 | Every Generate flow with no Audio Backend | "No engine installed" even with weights on disk; a direct generation failed with `Invalid model value ... 'Audio Models/Piper/default'` | `installed` comes from the backend, and the `Audio Models/...` names only exist once an Audio Backend registers them. The DAW ignored `backend_status: NOT_FOUND` | A setup card linking to Server > Backends | ebcfcc0 |
| 2 | Music with "Match project tempo" (on by default), every Sound Palette loop | `Invalid value for param Text2Audio Time Signature - '4/4'` | Core only accepts `2`, `3`, `4`, `6` (beats per bar) | Send the numerator, only to engines that read it | ebcfcc0 |
| 3 | MusicGen, Stable Audio | Generated from an empty prompt | The description went to Text2Audio Style and Prompt carried the (empty) lyrics. Only ACE-Step, YuE, YuE2, HeartLib and MiniMax read Style | Route prompt and style per engine | ebcfcc0 |
| 4 | Project load, crash resume | Drum machine pads went silent | `restoreProject` filled `blobStore`, then cleared it | Clear first | ebcfcc0 |
| 5 | Stems | "HTDemucs Fine-tuned" offered, but the engine only ships `htdemucs` and `htdemucs_6s` | Hard-coded list | Built from the engine list | ebcfcc0 |
| 6 | Transport under ~1500 px wide | BPM, snap, Project, Import, Export and Close clipped and unreachable | `.daw-transport` never wrapped inside an `overflow: hidden` container | Wraps, denser controls | ebcfcc0, 4058939 |
| 7 | First use | No way to open the DAW | Tab hidden until an autosave, project or audio result existed | Always reachable | ebcfcc0 |
| 8 | Sound Palette variation | Wrong duration | Estimated from blob size as if raw float audio | Decoded duration | ebcfcc0 |
| 9 | Export vs playback | Reverb differed between them | Impulse response from `Math.random()` per chain | Seeded generator | ebcfcc0 |
| 10 | Undo | Time signature and drum pattern not restored | Not in the snapshot | Added | ebcfcc0 |

Dropped from the first draft: `buildClipScoreMeta` reading `params.textaudiostyle` is correct. Core strips digits
from parameter ids, so `Text2Audio Style` is `textaudiostyle`.

## Found during the fix and test pass

| # | Flow | What happened | Cause | Commit |
|---|------|---------------|-------|--------|
| 11 | Drum machine | Pad audio vanished after the next undoable edit | Blob cleanup only counted clips as references | ebcfcc0 |
| 12 | Speech to Text | The model picker was ignored | `processSTT` never sent `model` | ebcfcc0 |
| 13 | Speech to Text | SheetSage2, a score transcriber, was offered as a speech engine | It registers in the STT category | ebcfcc0 |
| 14 | Sound Palette loops | Always ACE-Step; any failure showed a generic message | Hard-coded engine, error swallowed | ebcfcc0 |
| 15 | STT, stems, conform and pads on a trimmed or split clip | Processed the whole source file | Sent the clip's full blob | ebcfcc0 |
| 16 | Effects | A compressor reacted to fader moves | Inserts sat after the fader | ebcfcc0 |
| 17 | Loop region | Handles never appeared and did not follow zoom | The stylesheet hides handles, and clearing the inline display kept them hidden | 8785951 |
| 18 | Overlapping clips | Every overlapping take played at once | No layering | 278f511 |

## Mics and instruments (the input gap), now closed

Before: MediaRecorder (lossy Opus), takes placed at the button press with no latency compensation, only the first
armed track recorded, one global input, no monitoring, no click, and a MIDI keyboard could only write into the
Score tab. Now:

- AudioWorklet capture on the AudioContext clock: lossless PCM between exact frames, saved as 32-bit float WAV.
- Latency compensation from the device's input latency plus the context's output latency, with a manual offset.
- Every armed track records its own take at once.
- Per-track input: device, and mono sum, stereo or a single channel of a multi-input interface.
- Input monitoring through the track's own inserts, fader and pan; input meters on armed tracks.
- Metronome, count-in, punch over the loop range with pre-roll, and punch in on the fly.
- Instrument tracks: a two-oscillator synth with seven presets, or a sampler built from any clip or a generated
  sound. Played from the on-screen keys, the computer keyboard or a Web MIDI keyboard, recorded into MIDI clips
  (latency-compensated, alongside armed audio tracks), edited in a piano roll, imported and exported as `.mid`, and
  bounced to audio.

## Against Pro Tools

| Area | Pro Tools | Before | Now |
|------|-----------|--------|-----|
| Track header | Number, R / I / S / M, view selector, automation mode, inserts, I/O, volume and pan readouts | Name, M, S, volume, R, pan, automation toggle, meter | Number, R / I / S / M / A, pan, volume, input chooser, inserts count, dB readout, meter, right-click menu |
| Rulers | Bars and Beats, Min:Secs, Tempo, Chords, Markers | One ruler | Bars and Beats, Min:Secs, Markers, drawn only for the visible slice |
| Markers | Yes | None | Add, move, rename, jump, previous and next |
| Counters | Main and Sub, selection Start / End / Length | Time and bars.beats | Main and sub (click to swap), Start / End / Length |
| Edit modes and tools | Shuffle, Spot, Slip, Grid; smart tool and more | Snap toggle | Slip, Grid, Shuffle; smart and range tools; spot to time |
| Grid and nudge | Selectable values | Followed zoom; no nudge | Bars, note values, triplets, seconds; nudge by the grid |
| Selection | Time range across tracks | Playhead and loop only | Range across tracks: delete, split, copy, cut, paste, loop, export |
| Clips | Fade handles, crossfades, clip gain | Numeric fades in the Clip Editor | On-clip fade handles with shapes, draggable clip gain, short seams between layered takes |
| Takes | Playlists and comping | None | Layered takes, newest on top; bring to front or send to back |
| Signal flow | Inserts pre-fader | FX after the fader | Inserts pre-fader |
| Export | Bounce a range, stems, 24-bit, 32-bit float | Full mixdown, 16-bit | Song, selection or loop; 16, 24 or 32-bit float at 44.1 or 48 kHz; stems as one ZIP |
| MIDI | Instrument tracks, piano roll | None | Instrument tracks, MIDI clips, piano roll, `.mid` import and export |

## Remaining gaps

- Sends, aux buses, groups and VCAs.
- Automation write modes (Touch, Latch, Write) and parameters beyond volume and pan.
- Tempo map and tempo ruler; the session has one BPM and meter.
- Crossfades you can shape; layered takes only get short automatic seams.
- Take comping across playlists.
- MIDI: sustain pedal and CC, a velocity lane, and note chase (a note that started before the playhead stays
  silent when playback starts inside it).
- Very long sessions: every clip still decodes its own WaveSurfer copy.
- Server-side projects cap at 40 MB of audio (64 MB of JSON, audio inlined as base64). Lossless takes reach that
  after about 3.5 minutes of mono recording; autosave and local saves are unaffected. Storing project audio as
  separate, compressed files would lift it.

## Verification

Tested in Chromium through the proxy below, against the live site for everything that generates:

- Generate: Kokoro TTS with engine options (voice and speed), MusicGen (with Match project tempo), Stable Audio,
  AudioGen, Whisper STT with a chosen model, Pocket TTS voice cloning from the selected clip with its transcript
  filled automatically, placement on the selected track. HeartLib for the lyric engines: the request carried the
  lyrics as Prompt and the description as Text2Audio Style, and 10 s of tonal, rhythmic audio came back (Whisper
  heard one word of the lyric, so sung lyrics are not confirmed).
- No Audio Backend: the setup card, against a server without one; Open Backends lands on Server > Backends.
- Palette loops and variations, stems (Karaoke on a split clip exports exactly its piece), drum pads (generate,
  steps, undo, render, survive cleanup and project load), conform 100 to 120 BPM (6 s to 5 s).
- Projects: save, open and new round trip; Save WAV to Swarm Outputs; import from outputs.
- Recording with a synthetic input stream: both tones verified in the export, monitoring on the master meter,
  click spacing and downbeat accents, count-in, punch (a take of exactly 1.000 s at 1.0 s, auto-stop), layering
  (a Goertzel check proves only the top take plays).
- Editing: range select, delete and shuffle, markers and jumps, copy and paste, fade and gain drags, selection
  export (1.0000 s, 24-bit), stems ZIP.
- Instruments: computer-keyboard MIDI recording, piano roll edits, exported pitches, live scheduling across loop
  passes, `.mid` round trip, bounce, sampler pitching (C5 from a C4 sample).
- Every transport, header, menu and bottom-tab button, at 1600x900 and at a narrow pane.

Not verified: a real microphone, interface or MIDI keyboard (synthetic streams and the computer keyboard stood in),
Firefox and Safari, the Demucs install flow on a server without weights, and a SwarmUI restart loading the new
script registrations in `AudioLab.cs` (compile-checked only; the proxy injected them in the same order). YuE, YuE2
and MiniMax take the same request path as HeartLib but were not generated. The BPM and meter params only go to
ACE-Step, which the live site does not offer: nine of its variants have weights on disk, but the Audio Backend has
not registered it, so no `Audio Models/ACE-Step/...` name exists and the DAW hides it.

## Testing without restarting shared servers

Extension JS is cached in memory by release builds of SwarmUI, so every front-end change normally needs a
restart. Instead, run a local reverse proxy that serves this branch's `Assets/` and tab HTML and forwards
everything else (including WebSockets) to a server that has an Audio Backend and models, such as the live site.
