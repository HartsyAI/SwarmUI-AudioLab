# E2E: overview strip

Run in the browser against a Debug dev build of SwarmUI with this branch (new script file: rebuild and restart once).
Seed with `Tests/e2e/daw-fixture.js`: `await dawFixture({ tracks: 4, seconds: 120 })`.

The strip is a canvas, so the checks read pixels: the window frame is drawn in `--emphasis`, the playhead in red, the
sections in their own colors. Convert with the strip's CSS width W and the span (the content length, or the width of the
lanes' view in seconds if that is longer, so scrolling never changes it): x = seconds / span * W.

| # | Step | Expect |
|---|------|--------|
| 1 | Open Audio Lab | A 40 px strip above the rulers, labelled Overview, a colored row per track, a framed window at the left; MAP is lit |
| 2 | The frame's left and right edges | At scrollLeft / zoom and (scrollLeft + view width) / zoom, scaled to the strip (checked by scanning the frame color along the top row) |
| 3 | Drag the frame 300 px right | The lanes scroll by 300 / W * span * zoom (2618 px for W = 1375, 120 s, zoom 100) and the ruler follows |
| 4 | Click the strip outside the frame | The view recenters on the click |
| 5 | Double-click the strip | The playhead moves to that time and the view shows it |
| 6 | Scroll the lanes (wheel, scrollbar, scrollLeft), all the way to the right end | The frame follows; the blocks do not shift or rescale, and at the far right the frame rests against the strip's right edge |
| 7 | Zoom in and out | The frame narrows and widens; zooming out past the end keeps the frame inside the strip |
| 8 | Add a section and a marker | The section band shows in its color along the top of the strip; the marker tick appears |
| 9 | Duplicate a track | A new row of blocks appears |
| 10 | Group two tracks (once the group tint from the icons PR is merged) | Their blocks draw in the group color when the group's tint is on; before that they keep their track colors |
| 11 | Move or trim a clip | Its block moves with it |
| 12 | Play | The red playhead line moves along the strip and matches the lanes' playhead |
| 13 | MAP off, then on | Off collapses the row (the lanes get 40 px taller) and hides the strip; on shows it again with current data (duplicate a track while it is off to see this) |
| 14 | Reload with MAP off | It stays off; the preference is not in the project |
| 15 | Resize the pane, drag the header splitter | The canvas resizes with the strip and the frame is recomputed |
| 16 | Narrow the pane to about 900 px | The strip still fits; no console errors |
| 17 | Close the DAW (X), reopen it from a file, then drag the frame and press MAP off and on | One strip, one canvas in `#daw_overview`; the drag scrolls once (not twice per move); it still redraws when the pane is resized |

Pass criteria: every row behaves as stated, and the console shows no errors.

## Run log

2026-11-17, Chromium (built-in browser pane), Debug build, 1600x900, 1100x800 and 900x800, 4 x 120 s project.
Rows 1-9 and 11-16 passed; the strip's pixels were read from the canvas (the frame, a section band, block rows, the
playhead). Row 10 was not run: group tint is in the icons PR, not on this branch (the strip already reads
`track.displayColor` when it exists). Not exercised: recording while the strip shows, the light theme, a project of
hundreds of clips (the blocks are cached and redrawn only when the data, size or scale changes).
