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
| 19 | Speech to Text | The Language and Task options did nothing | The DAW sent neither, and ProcessSTT dropped `task` | 144a00e |
| 20 | Instrument notes | A note no longer than the synth's attack was silent; a note released during its attack lost the attack | Note-off canceled the whole attack ramp | d3bba9b |
| 21 | Engine options | Untouched options were sent, so every ACE-Step request asked for C major | Values were compared against the param default, which for core's Key/Scale is not one of its values | d3bba9b |
| 22 | Music engines | No ACE-Step on servers where HartsyInference serves it natively, the live site included | Only the Audio Backend's engine list was read | d3bba9b |
| 23 | Exports | Every mix came out 6 ms late and 0.5 dB louder than its tracks, with nothing near the limiter's threshold | The master limiter is a compressor node: look-ahead delay plus automatic makeup gain | 7d5499e |
| 24 | Automation lane | Dragging a line segment stopped after the first step | The drag re-rendered the lane under the pointer | 7d5499e |
| 25 | Undo | The crash-recovery autosave kept the state from before an undo | Undo and redo did not mark the session dirty | 7d5499e |

## Found in review

Found by the review bot on the open pull request, and by the tests that followed. Each row names the commit.

| # | Flow | What happened | Cause | Commit |
|---|------|---------------|-------|--------|
| 26 | Solo | Soloing an aux return silenced everything sent into it | A send was gated by its source track's solo, and solo sat on the fader, so post-fader sends were muted too | 9df89b0 |
| 27 | Solo export | Exporting a soloed return rendered silence | Tracks silent under solo were skipped, so their sends never rendered | 9df89b0 |
| 28 | Project open and resume | Aux sends lost their bus when a project opened | Saved bus ids were not mapped to the ids the session gives the restored tracks | 9df89b0 |
| 29 | Automation | Touch kept writing after switching from Latch or Write mid-pass | The open writer kept the mode it started with | 9df89b0 |
| 30 | MIDI import | A file whose first tempo event came after the start lost its opening tempo | The delayed event was taken as the session tempo | 9df89b0 |
| 31 | MIDI sustain | A sustained note kept sounding through a later clip that covered it | The covering check ran only at the note's onset | 9df89b0 |
| 32 | Inputs | A microphone opened while the DAW was hidden stayed open | Permission finished after the tab was hidden, so nothing was left to release | 9df89b0 |
| 33 | Crossfade | A crossfade with no boundary to cross still cleared the redo history | The undo step was taken before the pair check | 9df89b0 |
| 34 | Instrument playback | Stopping or closing with an empty sampler threw an error | An empty sampler voice had no kill handle | 1c82847 |
| 35 | MIDI duplicate | Duplicating a muted or colored MIDI clip lost both | The copy was built without them | 1c82847 |
| 36 | MIDI shuffle | Deleting a MIDI clip in Shuffle mode left a gap | Only the deleted clip moved | 1c82847 |
| 37 | MIDI export | Export as MIDI file wrote notes and pedal events past the clip's end | The export read the clip's whole contents, not the window playback uses | 32bfe07 |
| 38 | Piano roll grid | New notes and the grid took the playhead's tempo, not the clip's | The grid step was read at the playhead | 32bfe07 |

Rounds 1 to 3 of the same review (13 findings) were fixed in aa65437, 144a00e and d3bba9b. Each thread on the pull request names its fix.
Round 7 is rows 37 and 38.

| 39 | Save & Close | Save & Close discarded the session even when the server save failed | The save swallowed its errors, and the close went on regardless | 0f04baa |
| 40 | Clip import | Undo during a clip's decode deleted the clip's audio | The blob cleanup ignored clips that were still decoding | 0f04baa |
| 41 | Project save | A malformed save body overwrote the saved project | The file was written before the body was parsed | 0f04baa |
| 42 | Recording stop | Stopping a take could wedge recording when its capture was disposed | Disposing the capture dropped the pending stop | 0f04baa |
| 43 | Header fader | A press with no movement left an automation write open | The release came only from the change event | 0f04baa |
| 44 | Automation | A Touch grab after a Read-mode fader move used the pass-start level | The base was not refreshed when no write was open | d1d5cbd |
| 45 | Bounce | A bounce cut its release and reverb tails | The bounce rendered with exact, which skips the tail length | d1d5cbd |
| 46 | Note scheduling | Clearing the note cache started queued notes twice | The clear forgot the keys of notes still in the lookahead | 0f04baa |
| 47 | Big takes and imports | A take or import with about 125,000 notes threw | Math.max spread every note into one call | 0f04baa |
| 48 | Stream handles | A stale handle could evict a newer stream for the same device | Release deleted the entry by key, without checking identity | 0f04baa |
| 49 | Clip drag | A cancelled touch drag left its move listener attached | The drag listened for pointer up but not pointer cancel | 0f04baa |
| 50 | Sampler voices | A note held past its sample's end kept its gain node | The end handler was attached only at release | 0f04baa |
| 51 | Effects | A stored chain with an unknown effect shifted the later effects | Stored chains loaded without the known-type filter | 0f04baa |
| 52 | Codex round 8 | A saved sampler's file was dropped when this browser could not decode it | A decode failure skipped the store | 8b75be2 |
| 53 | Codex round 8 | Pruning could delete audio a save had just uploaded | Pruning had no grace period | 8b75be2 |
| 54 | Codex round 8 | Undo left the master level stale | The restore did not refresh the master gain | 8b75be2 |
| 55 | Codex round 8 | Mixer changes did not autosave | The mixer callback never scheduled a save | 8b75be2 |
| 56 | Codex round 9 | A split cleared the redo history with nothing to split | The undo step came before the split check | 52f0b14 |
| 57 | Codex round 9 | 6/8 and 7/8 bars counted the numerator as quarter notes | Bar math ignored the denominator | 52f0b14 |
| 58 | Codex round 9 | Exports cut effect tails at 5 s | The tail was a fixed five seconds | 52f0b14 |
| 59 | Codex round 9 | A shuffle delete inside a MIDI clip left the clip too long | The clip length was never reduced | 52f0b14 |
| 60 | Codex round 9 | Concurrent uploads could exceed the project quota | The quota check and the write were not serialized | 52f0b14 |

Rows 39 to 51 are the verified findings of this review. Rounds 8 and 9 of the Codex review are rows 45 and 52 to 60.

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
| Rulers | Bars and Beats, Min:Secs, Tempo, Chords, Markers | One ruler | Bars and Beats, Min:Secs, Tempo (a tempo map), Markers, drawn only for the visible slice |
| Markers | Yes | None | Add, move, rename, jump, previous and next |
| Counters | Main and Sub, selection Start / End / Length | Time and bars.beats | Main and sub (click to swap), Start / End / Length |
| Edit modes and tools | Shuffle, Spot, Slip, Grid; smart tool and more | Snap toggle | Slip, Grid, Shuffle; smart and range tools; spot to time |
| Grid and nudge | Selectable values | Followed zoom; no nudge | Bars, note values, triplets, seconds; nudge by the grid |
| Selection | Time range across tracks | Playhead and loop only | Range across tracks: delete, split, copy, cut, paste, loop, export |
| Clips | Fade handles, crossfades, clip gain | Numeric fades in the Clip Editor | On-clip fade handles with shapes, draggable clip gain, crossfades across a selection (F), short seams between layered takes |
| Takes | Playlists and comping | None | Layered takes, newest on top; bring to front or send to back; a take comped into a selected range |
| Signal flow | Inserts pre-fader | FX after the fader | Inserts pre-fader |
| Routing | Sends, aux buses, groups, VCAs | None | Sends (level, pre or post, mute) to aux buses with their own inserts; stems include bus returns |
| Automation | Read, Touch, Latch, Write, Off | Volume and pan breakpoints, drawn by hand | Read, Touch, Latch, Write and Off for volume and pan, written from the faders during playback |
| Export | Bounce a range, stems, 24-bit, 32-bit float | Full mixdown, 16-bit | Song, selection or loop; 16, 24 or 32-bit float at 44.1 or 48 kHz; stems as one ZIP |
| MIDI | Instrument tracks, piano roll | None | Instrument tracks, MIDI clips, piano roll with a velocity lane, sustain pedal (CC 64), `.mid` import and export |

## Remaining gaps

- Groups and VCAs.
- Automation of parameters beyond volume and pan (send levels, effect parameters).
- One time signature per session: the tempo map changes BPM, not the meter.
- Playlists: each track has one lane of clips, and takes are layered and comped in place.
- Very long sessions: every clip still decodes its own WaveSurfer copy.

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

Checked on this branch head, also in Chromium through the proxy:

- Tempo map: changes added, moved and removed on the Tempo row, with bars, counters, grid and click following them.
  Undo, autosave and `.mid` export carry them.
- Tempo-synced delay: the live delay time steps from 0.5 s at 120 BPM to 1.0 s at 60 BPM at a change at 4 s, and the
  export's delay ends at 1.0 s.
- Aux return: soloing a bus exports the audio sent into it (RMS 0.0155 with the bus soloed, silent before the fix).
- Save and resume: a send follows its bus to the id the bus gets when the project is restored.
- MIDI: a `.mid` with a tempo change after its first note imports with a base of 120 BPM and the change at 0.5 s. A
  note under a held pedal stops at a later clip (1.5 s, not 3 s).
- Firefox 153: the recording, WAV export, send tail, computer-keyboard MIDI and piano-roll drag checks passed with no
  page errors, and a Touch write driven by real mouse input recorded envelope points.
- Mode switch: a Latch lane switched to Touch mid-pass ends its write at the switch. The last envelope point was at
  2.60 s, the switch was at about 2.57 s, and nothing was recorded through the 4.83 s stop.
- Hidden tab: an input that finished opening while the DAW was hidden was released. It reopened when the tab showed
  again, and closed on disarm (two opens and two releases).
- Crossfade undo: F with no boundary to cross shows its notice and leaves redo intact (add a bus, undo, select all, F,
  redo brings the bus back).
- MIDI keyboard, simulated: software Web MIDI input recorded pitches, velocities and sustain pedal events into a clip.
- Reopen decode: a 40 MB clip decodes in about 0.5 s, down from 4.3 s with the per-byte callback (fixed in 354acac).
  A synthetic 140 MB decode crashed the test tab, so 140 MB is unverified in a browser.
  No hardware was involved.
- Storage: the 17 project-audio unit tests pass again, re-run in an isolated copy of the current C#.

Not verified: a real microphone, interface or MIDI keyboard (synthetic streams, the computer keyboard and simulated Web MIDI input stood in),
Safari (no WebKit build here), the Demucs install flow on a server without weights, and a SwarmUI restart loading the
new script registrations in `AudioLab.cs` and the project-audio endpoints (compile-checked only; the proxy injected the
scripts in the same order, and the project-audio tests pass in an isolated copy). YuE, YuE2 and MiniMax take the same
request path as HeartLib but were not generated. ACE-Step on the live site is served natively by HartsyInference (`AceStep15/...`), not by the Audio Backend, which
is why the Audio Backend never listed it. The DAW now offers those checkpoints. Verified live: a 10 s turbo request
with Match project tempo sent BPM 120 and meter 4, and the result's onsets fall on a 0.5 s grid (autocorrelation
peaks at 0.25, 0.5 and 1.0 s).

## Testing without restarting shared servers

Extension JS is cached in memory by release builds of SwarmUI, so every front-end change normally needs a
restart. Instead, run a local reverse proxy that serves this branch's `Assets/` and tab HTML and forwards
everything else (including WebSockets) to a server that has an Audio Backend and models, such as the live site.
