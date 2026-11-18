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
| 24 | Open the tab with LLMAssistant reporting no model (`LLMAssistantGetModels` returns `models: []`) | The model list is empty; a red line says LLMAssistant has no language model and what to do; Refresh is shown; Start is disabled with the reason as its tooltip and in the idle hint |
| 25 | Start a provider (or stub a model into the response), press Refresh | The list fills, the status line goes, Start enables, the idle hint returns to "Press Start to talk." |
| 26 | Open the tab with the `LLMAssistantGetModels` route missing (`AudioLabVoiceAgentOptions` says `llm_available: false`) | "LLMAssistant is not installed" line, Refresh hidden, Start disabled |
| 27 | Voice list | Every Kokoro voice, grouped by language in optgroups; the default is af_heart; **Preview** plays a short line (or a red chip says it needs the Kokoro model) and is disabled while loading |
| 28 | Assistant list | "(default assistant)" plus LLMAssistant's assistants; an unknown saved assistant stays selectable |
| 29 | Change settings, reload the page | The model, assistant, voice, prompt and barge-in come back; a saved model that is no longer offered falls back to the first with an info chip |
| 30 | Name a preset and Save, change settings, pick the preset | Settings return to the saved ones; Save with an existing name (any case) overwrites; Rename and Delete follow the chosen preset; Rename onto another preset's name is refused with a chip |
| 31 | `localStorage` blocked (`Storage.prototype.setItem` throws) | The tab still works; settings just do not persist; no console errors |
| 32 | Start a call with a preset loaded | The setup frame (`voiceFake.setup`) holds that preset's model, assistantId, voice, systemPrompt and bargeIn |
| 33 | Open the tab | **Hears you** lists the 7 recognizers (Whisper small.en selected); **Speaks with** lists Kokoro and Piper (Kokoro selected); a note line says what they are good for and that a new model downloads on first call |
| 34 | Choose Piper | The voice list becomes Piper's English voices, grouped by locale, with en_US-lessac-medium selected; Preview uses the Piper provider |
| 35 | Choose a recognizer, then reload | The recognizer and engine come back; a preset made before this change (no recognizer or engine) loads with the defaults |
| 36 | Start a call with Moonshine tiny and Piper chosen | `voiceFake.setup` holds `sttModel: 'moonshine:tiny'`, `ttsEngine: 'piper'` and a Piper voice; with the defaults chosen it holds the default tokens |
| 37 | Options route fails (old page against a new server) | The pickers stay empty, the call still starts and the setup frame omits `sttModel` and `ttsEngine` (server defaults) |

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
Review round (PR 99): late frames after Stop no longer revive the live layout; a server `Ended`/`Idle` state now releases the mic and socket; a failed Start after a dropped call keeps Reconnect; double-clicking Start opens one socket; informational chips expire after the call ends; text after a tool call starts a new bubble (order stays true in copy and export); one error chip, not two, on a failed connect. Re-checked in the browser with the fake socket; Node checks of `AudioVoiceState` extended.

### 2026-11-18, PR 2 (setup panel, options route, presets): dev server on 7801, built-in browser
Rows 24, 25, 27 to 30, 31 and 32 passed with `LLMAssistantGetModels`/`GetAssistants` stubbed in the page (this machine's LLMAssistant has no model, which is row 24 as it really is) and the real `AudioLabVoiceAgentOptions` route (54 voices in 9 language groups, default af_heart). Row 27's Preview ran for real: `AudioLabSpeakRaw` with Kokoro, the raw clip fetched and played to the end, the button disabled while it loaded (first use downloaded Kokoro-82M and the dictionaries). Row 32: the setup frame carried the loaded preset's model, assistantId, voice and barge-in; an empty prompt is omitted. Row 31: a throwing `setItem` left the tab working. At 900 px there is no horizontal overflow.
Not run: row 26 (`llm_available: false` needs the route absent), row 23 (light theme), a real call (needs Whisper small.en and an LLMAssistant model).
Found and fixed: the idle hint stayed on "Loading the language models…" after the list came back; Refresh now reloads the assistants too; the setup inputs were white on the dark theme (`.form-control`), now themed like the other DAW selects.

### 2026-11-18, PR 3 (recognizer and speech engine choice): dev server on 7801, built-in browser
Rows 33 to 36 passed with LLMAssistant stubbed and the real options route: 7 recognizers and Kokoro/Piper listed with the defaults selected, Piper's 37 voices grouped by locale with en_US-lessac-medium selected, a pre-PR-3 saved state and a preset naming an engine and recognizer the server does not list both fall back to the defaults, and the setup frame (stubbed socket) carried `sttModel: 'moonshine:tiny'`, `ttsEngine: 'piper'`, `voice: 'en_US-amy-low'`. Against the real route, a setup frame with an unlisted `sttModel` (a path), an unlisted `ttsEngine`, or a Kokoro voice with Piper was refused with a readable error.
Not run: row 37, a Piper voice preview (it downloads a voice), and any real call: Whisper, Moonshine, Distil-Whisper and Piper through the voice package are unverified live on this machine (xunit covers the catalog, the allow-list and the policy only).
