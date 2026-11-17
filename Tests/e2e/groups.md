# E2E: track groups

Run in the browser against a Debug dev build of SwarmUI with this branch (new script file: rebuild and restart once).
Seed with `Tests/e2e/daw-fixture.js` using four tones: `await dawFixture({ tracks: 4, seconds: 20 })` (220, 330, 440 and
550 Hz). Stub `window.prompt` / `window.confirm` from the page when driving by tool.

For the audible rows, wrap `URL.createObjectURL` to capture the blob the export hands to the download, decode it, and
measure each tone with a windowed Goertzel over a few seconds. Baseline: all four tones read the same.

| # | Step | Expect |
|---|------|--------|
| 1 | Press A on a track, then re-render the lanes several times (click clips) | One automation lane in the lanes for one spacer in the headers; closing A leaves none (a stale lane used to be added per render) |
| 2 | Right-click Track 1 > New group with this track | A "Group 1" row appears above it; its bar spans the lanes; header and lane rows are the same height |
| 3 | Right-click Track 2 > Move to group: Group 1 | Track 2 joins and sits under Track 1; both are indented with the group color |
| 4 | Mute the group (M) | Members dim; export: the group's two tones read ~0, the other two unchanged |
| 5 | Unmute, solo the group (S) | Export: the group's tones unchanged, the others ~0 |
| 6 | Unsolo, set the group fader to about 0.5 | Export: the group's tones read about half; the others unchanged |
| 7 | Stems ZIP with the fader at 0.5 | Each member's stem reads about half, the non-members full; with the group muted its stems are left out |
| 8 | Fold the group (caret) | Members and their automation lanes fold to zero height, the rows below move up, header and lane rows stay aligned; unfold restores them with their waveforms |
| 9 | New group on Track 3, then Ctrl+Z and Ctrl+Shift+Z | Undone and redone; Group 1's folded state is not changed by undo |
| 10 | Track menu > Move up / Move down | A grouped track steps within its group and is disabled at the ends; an ungrouped track steps over a whole group |
| 11 | Group menu > Move group down | The group and its members move together past the next block |
| 12 | Group menu > Ungroup | The group row goes, the tracks stay |
| 13 | Duplicate a grouped track | The copy lands in the same group right below it |
| 14 | Group menu > Rename (double-click the name), Color…, group menu > Select the tracks in this group | Renamed and recolored; selection covers every member, folded ones included |
| 15 | Delete the last member of a group | The group row and bar go with it; undo brings both back |
| 16 | Drag a grouped track's clip | The group bar's block follows the clip |
| 17 | Zoom in and out | Group bar blocks scale with the lanes |
| 18 | Save, New Project, Open | Group name, color, fader, M/S, folded state and membership come back; members of a group saved folded still show their waveforms when unfolded |
| 19 | Open a project saved without a `groups` field or any `groupId` | Loads normally with no groups |
| 20 | Crash resume (reload, Resume) | Groups come back |
| 21 | Open the Mixer tab with a group present; narrow the pane to about 900 px | No errors; the group row fits without overflow |

Pass criteria: every row behaves as stated, and the console shows no errors.

## Run log

2026-11-17, Chromium (built-in browser pane), Debug build, 1600x900 and 900x800, 4 x 20 s project.
Rows 1-21 passed. The audible rows were measured on exported WAVs and the stems ZIP (baseline 0.227 per tone; group muted:
220 and 330 Hz at 0, 440 and 550 Hz at 0.227; group soloed: the inverse; group fader at 0.508: group tones at 0.115, others
unchanged; stems at 0.115 for members and 0.226 for non-members; with the group muted its two stems are absent from the ZIP).
Row 1 was confirmed as a real bug on `main` first (3 automation lanes in the lanes for 1 header spacer) and fixed here.
Not exercised: dragging a clip across lanes with a group present (that path keys on `.daw-track-lane`, which the group
bar does not carry), recording while a group is soloed or muted, the light theme, and a project saved with a group folded
and reopened while its tracks have automation visible.
Header drag-reorder was left out on purpose: reordering is by the track menu and the group menu.
