# E2E: Voice Agent call view

No speech models or LLM needed: `Tests/e2e/voice-fake-socket.js` replaces the microphone and the `AudioLabVoiceSession`
socket. Open the DAW, open the Voice Agent bottom tab, paste the fixture into the page, run `voiceFake.install()`, press
Start, then drive the server side with `voiceFake.state(...)`, `say(...)`, `pcm(...)`, `send(...)`, `drop(...)` or the whole
`await voiceFake.script()`. Pure logic (turn merging, latency, notices, error text, export) runs in Node with `vm`
(`AudioVoiceState` has no DOM).

| # | Step | Expect |
|---|------|--------|
| 1 | Open the tab before any call | State pill "Idle", hint "Press Start to talk.", orb grey, no timer; Start visible, Stop and Reconnect hidden; Copy, Clear, Export disabled; the settings fold is open |
| 2 | `voiceFake.install()`, Start | Pill "Connecting", settings fold closes, timer starts at 0:00 and counts; Stop shown, Start hidden; `voiceFake.setup` holds model, voice, bargeIn, inputRate |
| 3 | `state('Warming')`, then `state('Listening')` | Pill and orb change tone (yellow then accent); hint changes; orb ring swells with the stubbed mic tone |
| 4 | `say('user', ...)` then `say('assistant', ...)` for turn 1 | You bubble right, Agent bubble left, each with a time; the empty-state text is gone |
| 5 | A second `say('assistant', ...)` for the same turn | The same bubble grows; no new bubble |
| 6 | `pcm(1, 700)` while Speaking | Tone plays; the orb swells with the reply; pill "Speaking" |
| 7 | `send({ metrics: { turnId: 1, ... } })` | The Agent bubble shows heard / thought / first sound / turn with ms or s values; metrics sent before the bubble exist still land when it arrives |
| 8 | `tool_call` then `tool_result` with one id | One collapsed "Tool: name" row; its summary loses the ellipsis when the result arrives; open it to see arguments and result |
| 9 | `bargein` for the playing turn | Audio stops; an "Interrupted" chip appears and leaves after about 8 s |
| 10 | Send the same `notice` twice | One chip with (×2); the close button removes it |
| 11 | `send({ error: ... })` | A red chip that stays until dismissed |
| 12 | Scroll the transcript up, then send another turn | The view does not jump; **Jump to latest** appears; clicking it scrolls to the end and hides the button |
| 13 | Copy | Clipboard holds `[time] You: ...` lines, tool rows included; a "Copied" chip |
| 14 | Export Markdown, Text, JSON | Each downloads `voice-call.<ext>`; Markdown has bold speakers and the latency line; JSON has raw metrics |
| 15 | Clear | Transcript empties, empty-state text returns, Copy/Clear/Export disable |
| 16 | `drop(1006, '')` during a call | Pill "Call ended", a red "disconnected" chip with the Reconnect hint, Reconnect shown instead of Start, settings fold reopens, timer stops |
| 17 | Reconnect | A fresh call starts, the old transcript is cleared |
| 18 | Stop | Socket sends `{ end: true }`; pill "Idle"; Start returns; mic released (no tracks live) |
| 19 | `install({ micError: 'NotAllowedError' })`, Start | A red chip says the microphone is blocked and what to change; Start stays usable; no socket was opened |
| 20 | Same with `NotFoundError` | The chip says no microphone was found |
| 21 | Switch to another bottom tab and back during a call | The call, timer and transcript are unchanged |
| 22 | Narrow the pane to about 900 px, short bottom panel | Header wraps cleanly, the transcript scrolls inside, nothing overflows horizontally, no console errors |
| 23 | Light theme | Pills, bubbles, chips and the orb stay readable |

## Run log
Filled in per run below.

### 2026-11-18, dev server on 7801, built-in browser, fake socket and stubbed microphone (no live call)
Rows 1-20 and 22 passed at 1600 px and 900 px (headless: DOM and computed styles read with the JavaScript runner, screenshots at 900 px).
Row 21 (tab switch) was not run separately: the tab is built once, so the call lives outside the tab content.
Row 23 (light theme) was not verified: the theme swap in the test page did not take effect; every rule uses theme variables only.
Found and fixed during the run: the Reconnect button stayed disabled after a first successful Start; the idle hint was empty
until the first state event; Copy now uses core's `copyText` (it also works over plain-HTTP LAN, where `navigator.clipboard`
does not exist); the Export select ignored the theme; the transcript got too little height in the short panel (tools moved
into the header row).
Not covered here: a real call (needs Whisper small.en, Kokoro and an LLMAssistant model).
