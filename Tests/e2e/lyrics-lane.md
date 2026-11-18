# E2E: lyrics lane

Needs the timed-words routes (PR 96) and Whisper `base` in the engine's model cache. Seed a speech clip: a vocal recording
(words spoken one at a time with pauses make the times easy to judge), opened with `await AudioDaw.open('<url>')`. Native
`prompt` and `confirm` can be stubbed from the page. Pure logic can be checked in Node with the `vm` module (no DOM).

| # | Step | Expect |
|---|------|--------|
| 1 | Open the clip, Lyrics tab | Tab shows the clip's name, an empty text box, two model pickers (both Whisper), the buttons; no strip yet |
| 2 | Type lyrics with `[Verse 1]` and `[Chorus]` lines and blank lines, **Time the words** | Status "Timed N words (aligned)"; N counts only sung words (no tags); the word table fills; the strip appears |
| 3 | Play from the start | The current line fills word by word in step with the voice. At a line change the next line scrolls up into the current slot (growing, fill carried over) while the finished one slides out of the top and fades, and the one after rises into the next slot; nothing is cleared and redrawn |
| 4 | Click a word in the strip | Playhead jumps to that word and the strip shows its line |
| 5 | Clear the text box, **Time the words** | The clip is transcribed: words appear, lines break at pauses, the text box is filled |
| 6 | **Estimate timing** on typed lyrics | Words are spread over the clip; the strip works; status says `estimated` |
| 7 | Nudge +0.25 s | Every start and end moves by 0.25 s |
| 8 | Edit one word's start in the table | Only that word moves, never past its neighbours |
| 9 | Ctrl+Z after rows 7 and 8, then Ctrl+Shift+Z | Each is undone and redone exactly; the table follows |
| 10 | Split the clip at the playhead | Both halves keep the lyrics; each shows only the words inside its window; word times on the timeline are unchanged |
| 11 | Move or trim a clip | The strip's words move with it |
| 12 | **Export LRC**, **Export SRT** | Files hold the selected clip's lines and times (LRC has `<mm:ss.xx>` before each word) |
| 13 | **Import LRC** with `[00:02.00]` and `[00:06.00]` lines | Words appear on the timeline at 2 s and 6 s; status says `lrc` |
| 14 | Save the project, reload, resume or open | Lyrics, times and source come back; the strip shows |
| 15 | Mute the clip | Its lyrics leave the strip |
| 16 | Stems with a vocals stem, then Time the words on the original | The vocals stem carries the lyrics (the muted original no longer shows them); timing uses the stem |
| 17 | Generate with a lyric engine and typed lyrics | The new clip has the lyrics in its Lyrics tab, untimed |
| 18 | Pick a different speech model for "Hears the words" | The choice is kept on reload; the request names it |
| 19 | Drop a second clip with lyrics over half of the first (same track) | Words under the covering clip's span show once, from the covering clip only; the strip does not flicker between two copies of a line |
| 20 | Stems, then Time the words on the muted original | The vocals stem gets the new words (the strip follows the stem); Export LRC on the muted original still writes its lines |
| 21 | Narrow the pane to about 900 px | One column, scrollable; the strip fits; no console errors |
| 22 | Time typed lyrics that include lines the clip never sings (a made-up extra stanza at the end) | Those words are flagged unsure: struck through in the table, left off the strip, counted in the status line; the sung words keep their times |
| 23 | Seek across several lines while the strip is showing | The lines jump into place without a long slide; a seek of one line still scrolls |
| 24 | Set a time on an unsure word, save and reload | The word is no longer unsure and shows on the strip; unsure flags survive a reload |

Pass criteria: every row behaves as stated and the console shows no errors.

## Run log

2026-11-18, Chromium (built-in browser pane), Debug build against a local build of the engine PR, GPU, `whisper_stt` `base`, a
13-word clip (16.4 s) with each word spoken on its own.
Rows 1-4 and 6-11 (split only; move and trim share the same source-time mapping, which a Node check covers), 12-14 passed.
Row 2: the first run sent the section tags to the aligner and they came back as words; fixed by sending only the sung lines.
Row 3: sampled at 450 ms during real playback, fills advance word by word (e.g. `100%,100%,71%,0%` then `100%,100%,100%,15%`) and the
line switches at its end. Row 9: nudge and a word edit undone and redone in order. Row 14: autosave resume restored 13 timed words.
Pure logic (`AudioDawLyrics`) checked in Node: line and tag handling, alignment to lines, source-time mapping under move and split,
line lookup, fills, LRC and SRT, import, edits, restore from junk.
Row 5: a half of the clip transcribed to 10 words, broken into lines at pauses, text box filled, status `stt`.
Not run: rows 19 and 20 (added in code review; only the pure logic and the strip's frame handling were checked in Node), 15 (covered by a Node check), 16, 17, 18 (16 and 17 need a separation model and a lyric music engine, neither installed here), row 11 move
and trim in the browser, the light theme.

2026-11-18 (later), Debug build, GPU, ACE-Step 1.5 Turbo song (30 s, fast rap, generated from the Generate tab, typed lyrics carried onto the clip) timed with `distilwhisper_stt` `large-v3`.
Row 22: four never-sung lines appended to the typed lyrics came back as 30 flagged words (25 caught by the collapsed-run rule, 5 more at the edge of the squeeze by the edge rule); all 73 sung words kept their times. Row 24 (flags survive reload): the status line after a resume still reported the flagged words. Row 3: with the new scrolling strip, sampled through a line change, `Nothing` went current to prev (y 4 to -24 px, scale 1 to 0.72, opacity 1 to 0), `Keep` next to current (y 38 to 4, scale 0.73 to 1), `Hands` after to next, `Beat` entered below; fills carried over. Row 23: a seek across several lines laid the strip out at once (`Beat` current at y 4, `Feel` next) with no slide. Row 24 (edit half): setting a start on the flagged word `This` cleared its flag (74 timed, 29 unsure). The light theme was not checked.
