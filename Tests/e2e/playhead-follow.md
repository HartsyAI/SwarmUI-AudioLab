# E2E: playhead follow

Run in the browser against a Debug dev build of SwarmUI with this branch (extension JS is cached in memory by release
builds). Seed with `Tests/e2e/daw-fixture.js`: `await dawFixture({ tracks: 3, seconds: 120 })`.

Scrolling state is read with `document.getElementById('daw_clip_lanes').scrollLeft`; the playhead's screen x is
`document.getElementById('daw_playhead').getBoundingClientRect().left`.

| # | Step | Expect |
|---|------|--------|
| 1 | Open Audio Lab, seed the fixture | FOLLOW button in the transport, active, tooltip says Page |
| 2 | Zoom so the 120 s project overflows (zoom 100), press Home, Space | Playhead moves; when it reaches the right edge the view pages forward and the playhead stays on screen |
| 3 | Right-click FOLLOW, choose Smooth, Home, Space | The playhead drifts to about a third of the width and holds there; the view scrolls continuously |
| 4 | While playing in Smooth, mouse-wheel sideways (Shift+wheel) | Follow stops; the view stays where it was. Press Space twice (stop, play): follow resumes |
| 5 | While playing, wheel vertically over the lanes with several tracks | Follow keeps working (vertical scroll does not pause it) |
| 6 | While playing, drag the horizontal scrollbar | Follow pauses until the next play or seek |
| 7 | While playing, drag a clip or a range selection | View does not jump under the drag; follow resumes after release |
| 8 | Turn LOOP on with a loop region at the start while the view is scrolled right, play | At each wrap the view returns to the loop start (page and smooth) |
| 9 | Press G | Follow toggles off (button inactive); playing past the edge no longer scrolls. G again restores the last mode |
| 10 | With follow off: Home, End, `[`, `]` and Left/Right arrows | The playhead is revealed after a jump out of view, in every mode |
| 11 | Zoom in and out with +/- while playing, and Z (fit) | Playhead keeps its screen position as before; follow is not paused by zoom |
| 12 | Record (arm a track, R) past the edge of the project | The view follows the recording playhead as the timeline grows |
| 13 | Reload the page, reopen Audio Lab | The chosen mode persists; FOLLOW reflects it |
| 14 | Resize the pane narrow (about 900 px) and back; switch theme | Button wraps cleanly, no console errors |
| 15 | Hide the Audio Lab tab while playing, show it again | No errors, follow continues |
| 16 | Undo/redo, Project save and reopen | Unaffected (follow is a viewer preference, not saved in projects) |

Pass criteria: every row behaves as stated, and the console shows no errors.

## Run log

2026-11-17, Chromium (built-in browser pane), Debug build, 1600x900 and 900x800, project of 3 x 120 s tracks.
Rows 1-11, 13, 14 (layout), 15 and 16 (follow is not part of project or undo state) passed. Row 6 initially failed:
grabbing the scrollbar seeked the playhead (an existing behavior of the lane click handler), which also resumed
follow. Fixed by ignoring scrollbar presses in the lane selection handler; row 6 then passed.
Row 12 passed with a synthetic input stream (`getUserMedia` replaced by an oscillator's `MediaStreamDestination`):
recording from 118 s of a 120 s project grew the timeline (12700 to 13057 px) and the view followed it.
Not run: the light theme (the FOLLOW button adds no CSS and reuses `.daw-btn-text`).
The pane was hidden for part of the run, so frame timing was coarse; positions, not timings, were asserted.

Review pass: the stored mode was ignored on reload (the storage key was read before its declaration, so row 13 only held for Page); fixed and rechecked with Smooth stored. Clicking or scrubbing the ruler or lanes within 24 px of an edge scrolled the view under the pointer; pointer seeks no longer reveal (keyboard and marker jumps still do, rechecked with End). A vertical wheel with a little sideways noise no longer pauses follow.
