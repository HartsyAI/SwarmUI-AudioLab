# E2E: sections

Run in the browser against a Debug dev build of SwarmUI with this branch (new script file: rebuild and restart once).
Seed with `Tests/e2e/daw-fixture.js`: `await dawFixture({ tracks: 3, seconds: 90 })`. Native `prompt` and `confirm`
can be stubbed from the page (`window.prompt = () => 'Verse'; window.confirm = () => true`) when driving by tool.

Useful reads: `document.querySelectorAll('#daw_ruler .daw-section')` (the ruler blocks, `--section-color` and
`getBoundingClientRect()`), `.daw-section-band` (the lane bands).

| # | Step | Expect |
|---|------|--------|
| 1 | Open Audio Lab | Ruler column shows Bars\|Beats, Min:Secs, Tempo, Markers, Sections; ruler height 88 px; rows line up with the ruler (no gap or clipping) |
| 2 | Double-click empty space in the Sections row | A 4-bar section ("Section 1", orange) appears at the snapped position, and a faint orange band runs through all lanes |
| 3 | Double-click empty space to the right | "Section 2" gets the next palette color; double-clicking inside an existing section renames instead of adding |
| 4 | Drag Section 2 left over Section 1 | It stops flush against Section 1's end |
| 5 | Drag Section 1's right edge toward and past Section 2 | The edge stops at Section 2's start; dragging it back left shrinks it, never below 0.1 s |
| 6 | Click a section | Playhead jumps to its start |
| 7 | Double-click a section, enter a name | Renamed on the ruler |
| 8 | Right-click a section > Color…, pick a swatch | Ruler block and lane band change color |
| 9 | Ctrl+Z, then Ctrl+Shift+Z after steps 4, 5, 7, 8 | Each is undone and redone exactly |
| 10 | Right-click a section > Select section | A selection spans the section across every track |
| 11 | Right-click a section > Loop section | LOOP turns on with the section as the loop region |
| 12 | Add two markers, right-click empty Sections row > Create sections from markers | One section per marker, running to the next marker, the last to the end; undo restores the old set |
| 13 | `{` and `}` | Playhead jumps to the previous and next section start; stays put with none left |
| 14 | Project > Save, New Project, Project > Open | Sections come back with names, colors and spans; New Project clears them and the bands |
| 15 | Open a project saved without a `sections` field | Loads normally with no sections and no errors |
| 16 | Zoom in and out | Ruler blocks and lane bands stay aligned and scale together |
| 17 | Right-click a marker > Color… | Marker color changes (and its line through the lanes), undoable |
| 18 | Record past the end of the project | The ruler's marker, tempo and section nodes are not rebuilt on each frame as the timeline grows (the same DOM nodes stay connected) |
| 19 | Narrow the pane to about 900 px | Five rows still fit; no clipping; no console errors |

Pass criteria: every row behaves as stated, and the console shows no errors.

## Run log

2026-11-17, Chromium (built-in browser pane), Debug build, 1600x900 and 900x800, 3 x 90 s project.
Rows 1-19 passed. Row 9 was exercised for color, move, rename and create-from-markers undo, not for an edge resize
(that goes through the same update path as move). Row 17 was checked for color and its undo. Row 18 by node identity while recording from
88 s of a 90 s project with a synthetic input stream: the ruler's section and tempo nodes stayed connected while the
timeline grew from 15044 to 15498 px).
Native `prompt` and `confirm` were stubbed from the page. Synthetic key presses for `{` and `}` do not reach the
handler through the browser tool, so those two were dispatched as `KeyboardEvent`s on the page.
Not run: the light theme (the new CSS uses theme variables and one fixed dark label color on light palette fills).
