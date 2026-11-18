# E2E: track icons, compact heights, group-colored and section-named clips

Run in the browser against a Debug dev build of SwarmUI with this branch (new script file: rebuild and restart once).
Seed with `Tests/e2e/daw-fixture.js`: `await dawFixture({ tracks: 4, seconds: 20 })`. Stub `window.prompt` / `window.confirm`
from the page when driving by tool. To drive the image upload without a file dialog, replace
`HTMLInputElement.prototype.click` for `type=file` inputs so it assigns a `File` made from a canvas and dispatches `change`.

| # | Step | Expect |
|---|------|--------|
| 1 | Right-click Track 1 > Icon…, pick an emoji | The picker shows 24 presets and "Upload an image…"; the emoji appears in the header between the number and the name |
| 2 | Track 2 > Icon… > Upload an image… with a 120x60 PNG | A 48x48 webp (under 1 KB) shows at 20 px in the header |
| 3 | Ctrl+Z, Ctrl+Shift+Z | The upload is undone and redone |
| 4 | Icon… on Track 1 shows "Remove the icon"; use it | The icon goes and the header closes up |
| 5 | Open the Mixer tab | The strip name carries the same icon (emoji or image) |
| 6 | Track menu > Tiny, then Compact | Header and lane are 30 and 44 px, aligned; the header shows number, icon, name, S and M only, with no overflow; the lane has no fade handles or gain readout |
| 7 | Set Small or Medium again | The full header returns |
| 7b | On a compact track, menu > Show the automation lane (twice) | The lane shows and hides although the header has no A button |
| 8 | Ctrl+Z after a height change, and after a color change | Height and color are restored (neither was undoable before) |
| 9 | Group two tracks; look at their clips | The members' clips draw in the group color; a track outside the group keeps its own |
| 10 | Group menu > Draw its clips in the group color (switch off) | Members' clips return to their track colors |
| 11 | Add a section over the clips, rename it "Drop 1", then Sections row menu > Name clips after their sections | Each clip becomes "Drop 1 - Track N"; with no clip inside a section the menu says so |
| 12 | Section menu > Name its clips after it | Only that section's clips change |
| 13 | Ctrl+Z, Ctrl+Shift+Z after the naming | Names revert and return |
| 13b | Split a clip into stems (track "Vocals: <clip>"), then name clips after sections | The stem track becomes "Vocals: <section> - <track>" and its own clip "<section> - Vocals: <section> - <track>" (the new track name) |
| 14 | Save, New Project, Open | Icons (emoji and image), heights and the tint switch come back; compact tracks are compact |
| 15 | Open a project whose icons are an svg data URL, an HTML string, or a number | Loads with no icons and no errors |
| 16 | Narrow the pane to about 900 px | No overflow in compact headers; no console errors |

Pass criteria: every row behaves as stated, and the console shows no errors.

## Run log

2026-11-17, Chromium (built-in browser pane), Debug build, 1600x900 and 900x800, 4 x 20 s project.
Rows 1-16 passed. The image upload was driven by replacing the file input's `click` with one that assigns a canvas-made
PNG (a real file dialog is not scriptable here): the result was a 48x48 webp of 799 characters. Hostile icons in a saved
project (an svg data URL, an HTML string, a number) were all dropped on load.
Not exercised: MIDI or instrument tracks in a tinted group (the MIDI clip block and note colors read the same
`displayColor` and were checked by reading), recording on a compact track, the light theme, and uploading a very large
or animated image (the picker accepts png, jpeg, webp and gif; a gif becomes a still frame).
