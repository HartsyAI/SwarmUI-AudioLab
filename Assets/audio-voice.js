/**
 * AudioVoice -- the "Voice Agent" bottom tab: a live phone-style call with the server's
 * AudioLabVoiceSession WebSocket (HartsyInference.Voice, answered through LLMAssistant).
 *
 * Built once per page session (audio-daw.js calls render(container) exactly once, the same "built ONCE, not
 * in updateBottomPanel" treatment Beats/Generate/Score get -- see that file's buildBottomPanel), so a live call
 * survives switching to another bottom tab and back, instead of being torn down and losing its socket.
 *
 * The rules for what the call shows (turns, latency, notices, what an error means) live in AudioVoiceState (pure);
 * this file owns the socket, the audio graph and the DOM.
 *
 * Reuses SwarmUI utilities: createDiv(), createSpan(), translate(), getWSAddress(), genericRequest().
 * Deliberately does NOT use makeWSRequest() (site.js): that helper's onmessage assumes every
 * frame is JSON text, and this socket's replies are a mix of JSON events and binary PCM16 audio frames.
 */
const AudioVoice = (() => {
    'use strict';

    // Derived from this file's own <script src>, not a written-out path -- core serves extension files under
    // the extension CLASS name, not the folder name (see audio-daw-score.js's identical SOUNDFONT_URL comment).
    const WORKLET_URL = (document.querySelector('script[src*="audio-voice.js"]')?.src || '')
        .replace(/audio-voice\.js.*$/, 'audio-voice-worklet.js');

    const OUTBOUND_SAMPLE_RATE = 24000; // Kokoro's own rate; the server sends it unresampled (see the PR).
    const CAPTURE_FRAME_MS = 20;

    /** How close to the bottom the transcript counts as "following" the conversation, in pixels. */
    const STICK_SLACK_PX = 40;

    let rendered = false;
    let els = {};

    // Networking + audio graph. Null whenever a call is not active.
    let ws = null;
    let audioCtx = null;
    let micStream = null;
    let micSource = null;
    let captureNode = null;
    let analyserNode = null;
    let outGain = null;
    let outAnalyser = null;
    let meterRaf = null;

    // Playback scheduling: one AudioContext, sources queued back to back, each tagged with the turn it came
    // from so a `bargein` event can stop/drop only that turn's audio.
    let nextPlayTime = 0;
    let scheduledSources = []; // { turnId, source }

    let callState = 'idle'; // idle | connecting | Warming | Listening | Thinking | Speaking | ToolRunning | ended

    // What the tab shows (AudioVoiceState owns the rules)
    let transcript = AudioVoiceState.createTranscript();
    const notices = AudioVoiceState.createNotices();
    const bubbleEls = new Map(); // transcript entry -> its element
    let callStartedAt = 0;
    let tickTimer = null;
    let stickToBottom = true;
    let micLevel = 0;
    let outLevel = 0;
    let canReconnect = false;

    function render(container) {
        if (!container || rendered) {
            return;
        }
        rendered = true;
        buildUi(container);
        loadModels();
        setState(callState);
    }

    // ===== UI =====

    function btn(parent, text, cls, onClick) {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = cls;
        b.innerText = text;
        if (onClick) {
            b.addEventListener('click', onClick);
        }
        parent.appendChild(b);
        return b;
    }

    function labeled(parent, labelText, input) {
        const wrap = createDiv(null, 'voice-agent-field');
        const label = createSpan(null, 'voice-agent-field-label', translate(labelText));
        wrap.appendChild(label);
        wrap.appendChild(input);
        parent.appendChild(wrap);
        return wrap;
    }

    function buildUi(container) {
        container.innerHTML = '';
        const root = createDiv(null, 'voice-agent-root');

        // --- Header: the orb, the state, the timer, and the call buttons ---
        const head = createDiv(null, 'voice-agent-head');
        els.orb = createDiv(null, 'voice-agent-orb voice-agent-tone-idle');
        els.orb.appendChild(createDiv(null, 'voice-agent-orb-ring'));
        els.orb.appendChild(createDiv(null, 'voice-agent-orb-core'));
        head.appendChild(els.orb);
        const status = createDiv(null, 'voice-agent-status');
        const stateRow = createDiv(null, 'voice-agent-state-row');
        els.state = createSpan(null, 'voice-agent-state voice-agent-state-idle', translate('Idle'));
        els.timer = createSpan(null, 'voice-agent-timer', '');
        stateRow.appendChild(els.state);
        stateRow.appendChild(els.timer);
        status.appendChild(stateRow);
        els.hint = createDiv(null, 'voice-agent-hint');
        status.appendChild(els.hint);
        head.appendChild(status);
        const buttons = createDiv(null, 'voice-agent-buttons');
        els.startBtn = btn(buttons, translate('Start'), 'basic-button voice-agent-start', onStartClick);
        els.stopBtn = btn(buttons, translate('Stop'), 'basic-button voice-agent-stop', onStopClick);
        els.reconnectBtn = btn(buttons, translate('Reconnect'), 'basic-button voice-agent-reconnect', onStartClick);
        head.appendChild(buttons);
        root.appendChild(head);

        // --- Settings (folds away during a call) ---
        els.settings = document.createElement('details');
        els.settings.className = 'voice-agent-settings';
        els.settings.open = true;
        const summary = document.createElement('summary');
        summary.className = 'voice-agent-settings-summary';
        summary.innerText = translate('Call settings');
        els.settings.appendChild(summary);
        const setup = createDiv(null, 'voice-agent-setup');
        els.model = document.createElement('select');
        els.model.className = 'form-control voice-agent-select';
        els.modelField = labeled(setup, 'Model', els.model);

        els.assistantId = document.createElement('input');
        els.assistantId.type = 'text';
        els.assistantId.className = 'form-control voice-agent-input';
        els.assistantId.placeholder = translate('(default assistant)');
        labeled(setup, 'Assistant', els.assistantId);

        els.voice = document.createElement('select');
        els.voice.className = 'form-control voice-agent-select';
        for (const voice of ['af_heart', 'af_bella', 'af_nicole', 'am_adam', 'am_michael']) {
            const opt = document.createElement('option');
            opt.value = voice;
            opt.innerText = voice;
            els.voice.appendChild(opt);
        }
        labeled(setup, 'Voice', els.voice);

        els.bargeIn = document.createElement('input');
        els.bargeIn.type = 'checkbox';
        els.bargeIn.checked = true;
        els.bargeIn.className = 'voice-agent-checkbox';
        const bargeWrap = createDiv(null, 'voice-agent-field voice-agent-field-inline');
        bargeWrap.appendChild(els.bargeIn);
        bargeWrap.appendChild(createSpan(null, 'voice-agent-field-label', translate('Allow barge-in')));
        setup.appendChild(bargeWrap);
        els.settings.appendChild(setup);

        els.systemPrompt = document.createElement('textarea');
        els.systemPrompt.className = 'form-control voice-agent-prompt';
        els.systemPrompt.rows = 2;
        els.systemPrompt.placeholder = translate('System prompt (leave blank for the assistant default)');
        els.settings.appendChild(els.systemPrompt);
        root.appendChild(els.settings);

        // --- Notices: chips you can dismiss ---
        els.notices = createDiv(null, 'voice-agent-notices');
        root.appendChild(els.notices);

        // --- Transcript ---
        const flowWrap = createDiv(null, 'voice-agent-flow-wrap');
        els.transcript = createDiv(null, 'voice-agent-transcript');
        els.transcript.addEventListener('scroll', () => {
            stickToBottom = els.transcript.scrollHeight - els.transcript.scrollTop - els.transcript.clientHeight <= STICK_SLACK_PX;
            if (stickToBottom) {
                els.jump.hidden = true;
            }
        });
        els.empty = createDiv(null, 'voice-agent-empty', translate('The conversation appears here. Press Start and speak.'));
        els.transcript.appendChild(els.empty);
        flowWrap.appendChild(els.transcript);
        els.jump = btn(flowWrap, translate('Jump to latest'), 'basic-button voice-agent-jump', () => scrollToLatest(true));
        els.jump.hidden = true;
        root.appendChild(flowWrap);

        // --- Transcript tools ---
        const tools = createDiv(null, 'voice-agent-tools');
        els.copyBtn = btn(tools, translate('Copy'), 'basic-button voice-agent-tool', onCopyClick);
        els.clearBtn = btn(tools, translate('Clear'), 'basic-button voice-agent-tool', onClearClick);
        els.exportSel = document.createElement('select');
        els.exportSel.className = 'form-control voice-agent-export';
        for (const [value, label] of [['', translate('Export…')], ['md', 'Markdown'], ['txt', translate('Text')], ['json', 'JSON']]) {
            els.exportSel.add(new Option(label, value));
        }
        els.exportSel.addEventListener('change', () => {
            onExport(els.exportSel.value);
            els.exportSel.value = '';
        });
        tools.appendChild(els.exportSel);
        head.insertBefore(tools, buttons); // beside the call buttons: the bottom panel is short, the transcript needs the height

        container.appendChild(root);
    }

    /** A model list that failed to load becomes a free-text id; the dropdown is swapped in place, not orphaned. */
    function useFreeTextModel() {
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-control voice-agent-input';
        input.placeholder = translate('model id');
        els.model.replaceWith(input);
        els.model = input;
    }

    function loadModels() {
        genericRequest('LLMAssistantGetModels', {}, data => {
            els.model.innerHTML = '';
            for (const model of (data.models || [])) {
                const opt = document.createElement('option');
                opt.value = model.id;
                opt.innerText = model.title || model.name || model.id;
                els.model.appendChild(opt);
            }
            if (els.model.options.length === 0) {
                const opt = document.createElement('option');
                opt.value = '';
                opt.innerText = translate('(no models reported -- type one in Assistant above if needed)');
                els.model.appendChild(opt);
            }
        }, 0, () => {
            // LLMAssistant may not be installed, or the route name may have moved; degrade to a free-text
            // model id rather than leaving a dead dropdown.
            useFreeTextModel();
        });
    }

    /** Which buttons make sense for the call as it is now. */
    function refreshControls() {
        const live = AudioVoiceState.describeState(callState).live;
        els.startBtn.hidden = live || canReconnect;
        els.startBtn.disabled = false;
        els.reconnectBtn.disabled = false;
        els.reconnectBtn.hidden = live || !canReconnect;
        els.stopBtn.hidden = !live;
        els.copyBtn.disabled = els.clearBtn.disabled = transcript.entries.length === 0;
        els.exportSel.disabled = transcript.entries.length === 0;
    }

    // ===== Call lifecycle =====

    async function onStartClick() {
        if (callState !== 'idle' && callState !== 'ended') {
            return;
        }
        canReconnect = false;
        els.startBtn.disabled = true;
        els.reconnectBtn.disabled = true;
        try {
            micStream = await navigator.mediaDevices.getUserMedia({
                audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
            });
        }
        catch (e) {
            reportError('mic', e);
            els.startBtn.disabled = false;
            els.reconnectBtn.disabled = false;
            return;
        }
        audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
        if (audioCtx.state === 'suspended') {
            await audioCtx.resume();
        }
        try {
            await audioCtx.audioWorklet.addModule(WORKLET_URL);
        }
        catch (e) {
            reportError('worklet', e);
            teardownAudio();
            els.startBtn.disabled = false;
            els.reconnectBtn.disabled = false;
            return;
        }
        micSource = audioCtx.createMediaStreamSource(micStream);
        analyserNode = audioCtx.createAnalyser();
        analyserNode.fftSize = 512;
        micSource.connect(analyserNode);
        outGain = audioCtx.createGain();
        outAnalyser = audioCtx.createAnalyser();
        outAnalyser.fftSize = 512;
        outGain.connect(audioCtx.destination);
        outGain.connect(outAnalyser);
        startMeter();

        const frameSize = Math.round(audioCtx.sampleRate * (CAPTURE_FRAME_MS / 1000));
        captureNode = new AudioWorkletNode(audioCtx, 'voice-capture-processor', { processorOptions: { frameSize } });
        captureNode.port.onmessage = (ev) => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(ev.data);
            }
        };
        micSource.connect(captureNode);

        resetCall();
        connectSocket(Math.round(audioCtx.sampleRate));
    }

    function onStopClick() {
        if (ws) {
            const closing = ws;
            ws = null; // the close handler reads this to tell a requested stop from a dropped connection
            try {
                closing.send(JSON.stringify({ end: true }));
            }
            catch (e) { /* socket may already be closing */ }
            try {
                closing.close();
            }
            catch (e) { /* ignore */ }
        }
        endCallLocally(false);
    }

    /** Clear the previous call's transcript and start the clock. */
    function resetCall() {
        clearTranscript();
        callStartedAt = Date.now();
        els.settings.open = false;
        if (!tickTimer) {
            tickTimer = setInterval(tick, 1000);
        }
        tick();
    }

    function endCallLocally(dropped) {
        ws = null;
        teardownAudio();
        flushAllScheduledAudio();
        if (tickTimer) {
            clearInterval(tickTimer);
            tickTimer = null;
        }
        canReconnect = !!dropped;
        setState(dropped ? 'ended' : 'idle');
        els.settings.open = true;
        refreshControls();
    }

    /** Once a second: the call timer, and old informational notices leaving. */
    function tick() {
        if (callStartedAt && AudioVoiceState.describeState(callState).live) {
            els.timer.innerText = AudioVoiceState.clock((Date.now() - callStartedAt) / 1000);
        }
        if (AudioVoiceState.expireNotices(notices)) {
            renderNotices();
        }
    }

    function teardownAudio() {
        if (meterRaf) {
            cancelAnimationFrame(meterRaf);
            meterRaf = null;
        }
        if (captureNode) {
            try { captureNode.disconnect(); } catch (e) { /* ignore */ }
            captureNode = null;
        }
        if (micSource) {
            try { micSource.disconnect(); } catch (e) { /* ignore */ }
            micSource = null;
        }
        if (micStream) {
            for (const track of micStream.getTracks()) {
                track.stop();
            }
            micStream = null;
        }
        if (outGain) {
            try { outGain.disconnect(); } catch (e) { /* ignore */ }
            outGain = null;
            outAnalyser = null;
        }
        analyserNode = null;
        micLevel = outLevel = 0;
        els.orb.style.setProperty('--mic', '0');
        els.orb.style.setProperty('--out', '0');
        // The playback AudioContext stays alive across a call (nextPlayTime/scheduled sources use it, and a
        // fresh one would need another user gesture to unlock); it is only ever torn down implicitly by the
        // page unloading.
    }

    // ===== WebSocket =====

    function connectSocket(inputRate) {
        setState('connecting');
        const address = getWSAddress();
        if (!address) {
            reportError('address');
            endCallLocally(true);
            return;
        }
        const socket = new WebSocket(`${address}/API/AudioLabVoiceSession`);
        ws = socket;
        socket.binaryType = 'arraybuffer';
        socket.onopen = () => {
            socket.send(JSON.stringify({
                session_id: session_id,
                model: els.model.value,
                assistantId: els.assistantId.value.trim() || undefined,
                voice: els.voice.value,
                systemPrompt: els.systemPrompt.value.trim() || undefined,
                bargeIn: els.bargeIn.checked,
                inputRate: inputRate,
            }));
        };
        socket.onmessage = onSocketMessage;
        socket.onerror = () => {
            pushNotice(translate('The voice connection reported an error.'), 'error');
        };
        socket.onclose = (ev) => {
            if (ws === socket) { // the user didn't request Stop; the server/connection ended the call
                const why = AudioVoiceState.explainError('socket', ev);
                pushNotice(`${translate(why.title)} ${why.hint ? translate(why.hint) : ''}`.trim(), 'error');
                endCallLocally(true);
            }
        };
    }

    function onSocketMessage(ev) {
        if (ev.data instanceof ArrayBuffer) {
            playOutboundFrame(ev.data);
            return;
        }
        let data;
        try {
            data = JSON.parse(ev.data);
        }
        catch (e) {
            return;
        }
        if (data.state) {
            setState(data.state);
        }
        else if (data.transcript) {
            addTranscript(data.transcript.role, data.transcript.text, data.transcript.turnId);
        }
        else if (data.bargein) {
            flushTurn(data.bargein.turnId);
            pushNotice(translate('Interrupted: the agent stopped speaking.'), 'info');
        }
        else if (data.tool_call) {
            addTool({ id: data.tool_call.id, name: data.tool_call.name, args: data.tool_call.arguments || '', turnId: data.tool_call.turnId });
        }
        else if (data.tool_result) {
            addTool({ id: data.tool_result.id, name: data.tool_result.name, result: data.tool_result.result || '', turnId: data.tool_result.turnId });
        }
        else if (data.notice) {
            pushNotice(data.notice, 'info');
        }
        else if (data.metrics) {
            addMetrics(data.metrics);
        }
        else if (data.error) {
            pushNotice(data.error, 'error');
        }
    }

    // ===== Outbound playback (turn-tagged PCM16 @ 24 kHz) =====

    function playOutboundFrame(buffer) {
        if (buffer.byteLength <= 4 || !audioCtx) {
            return;
        }
        const view = new DataView(buffer);
        const turnId = view.getInt32(0, true); // little-endian, matches VoiceOutboundFrame.Encode
        const pcm = new Int16Array(buffer, 4);
        const floats = new Float32Array(pcm.length);
        for (let i = 0; i < pcm.length; i++) {
            floats[i] = pcm[i] / 32768;
        }
        const audioBuffer = audioCtx.createBuffer(1, floats.length, OUTBOUND_SAMPLE_RATE);
        audioBuffer.copyToChannel(floats, 0);
        const source = audioCtx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(outGain || audioCtx.destination);
        const now = audioCtx.currentTime;
        if (nextPlayTime < now) {
            nextPlayTime = now;
        }
        source.start(nextPlayTime);
        const entry = { turnId: turnId, source: source };
        scheduledSources.push(entry);
        source.onended = () => {
            const idx = scheduledSources.indexOf(entry);
            if (idx >= 0) {
                scheduledSources.splice(idx, 1);
            }
        };
        nextPlayTime += audioBuffer.duration;
    }

    /** Drops queued/playing audio for turnId and anything older, same rule the server's own pump uses. */
    function flushTurn(turnId) {
        for (let i = scheduledSources.length - 1; i >= 0; i--) {
            if (scheduledSources[i].turnId !== 0 && scheduledSources[i].turnId <= turnId) {
                try { scheduledSources[i].source.stop(); } catch (e) { /* already finished */ }
                scheduledSources.splice(i, 1);
            }
        }
        if (audioCtx) {
            nextPlayTime = audioCtx.currentTime;
        }
    }

    function flushAllScheduledAudio() {
        for (const entry of scheduledSources) {
            try { entry.source.stop(); } catch (e) { /* already finished */ }
        }
        scheduledSources = [];
        nextPlayTime = 0;
    }

    // ===== Level meter and orb =====

    /** The loudest sample of an analyser's window, 0 to 1. */
    function peakOf(analyser, data) {
        analyser.getByteTimeDomainData(data);
        let peak = 0;
        for (let i = 0; i < data.length; i++) {
            peak = Math.max(peak, Math.abs(data[i] - 128));
        }
        return Math.min(1, peak / 100);
    }

    function startMeter() {
        const micData = new Uint8Array(analyserNode.fftSize);
        const outData = new Uint8Array(outAnalyser.fftSize);
        const step = () => {
            if (!analyserNode) {
                return;
            }
            micLevel = AudioVoiceState.smoothLevel(micLevel, peakOf(analyserNode, micData));
            outLevel = AudioVoiceState.smoothLevel(outLevel, outAnalyser ? peakOf(outAnalyser, outData) : 0);
            els.orb.style.setProperty('--mic', micLevel.toFixed(3));
            els.orb.style.setProperty('--out', outLevel.toFixed(3));
            meterRaf = requestAnimationFrame(step);
        };
        meterRaf = requestAnimationFrame(step);
    }

    // ===== State, transcript, notices =====

    function setState(state) {
        callState = state;
        const info = AudioVoiceState.describeState(state);
        els.state.innerText = translate(info.label);
        els.state.className = `voice-agent-state voice-agent-state-${info.key}`;
        els.orb.className = `voice-agent-orb voice-agent-tone-${info.tone}`;
        els.hint.innerText = translate(info.hint);
        if (!info.live) {
            els.timer.innerText = '';
        }
        refreshControls();
    }

    function clearTranscript() {
        transcript = AudioVoiceState.createTranscript();
        bubbleEls.clear();
        els.transcript.innerHTML = '';
        els.transcript.appendChild(els.empty);
        els.empty.hidden = false;
        stickToBottom = true;
        els.jump.hidden = true;
        refreshControls();
    }

    function onClearClick() {
        clearTranscript();
    }

    /** Keep the newest bubble in view while the user is following; otherwise offer a way back. */
    function scrollToLatest(force) {
        if (force || stickToBottom) {
            els.transcript.scrollTop = els.transcript.scrollHeight;
            stickToBottom = true;
            els.jump.hidden = true;
        }
        else {
            els.jump.hidden = false;
        }
    }

    /** Make or refresh the element for a bubble. */
    function drawBubble(entry) {
        let el = bubbleEls.get(entry);
        if (!el) {
            el = createDiv(null, `voice-agent-bubble voice-agent-bubble-${entry.kind}`);
            const who = createSpan(null, 'voice-agent-bubble-who');
            who.innerText = entry.kind === 'user' ? translate('You') : translate('Agent');
            const when = createSpan(null, 'voice-agent-bubble-time', AudioVoiceState.timeOfDay(entry.time));
            const head = createDiv(null, 'voice-agent-bubble-head');
            head.appendChild(who);
            head.appendChild(when);
            el.appendChild(head);
            el.appendChild(createDiv(null, 'voice-agent-bubble-text'));
            el.appendChild(createDiv(null, 'voice-agent-bubble-latency'));
            bubbleEls.set(entry, el);
            els.transcript.appendChild(el);
        }
        el.querySelector('.voice-agent-bubble-text').innerText = AudioVoiceState.textOf(entry);
        const lat = el.querySelector('.voice-agent-bubble-latency');
        lat.textContent = '';
        for (const item of AudioVoiceState.latencyOf(entry.metrics)) {
            lat.appendChild(createSpan(null, 'voice-agent-latency-cell', `${translate(item.label)} ${item.text}`));
        }
        return el;
    }

    function addTranscript(role, text, turnId) {
        const { entry } = AudioVoiceState.addTranscript(transcript, role, text, turnId);
        els.empty.hidden = true;
        if (entry.kind === 'assistant') {
            const held = AudioVoiceState.takePendingMetrics(transcript, entry.turnId);
            if (held) {
                entry.metrics = held;
            }
        }
        drawBubble(entry);
        scrollToLatest(false);
        refreshControls();
    }

    function addTool(call) {
        const entry = AudioVoiceState.addTool(transcript, call);
        els.empty.hidden = true;
        let el = bubbleEls.get(entry);
        if (!el) {
            el = document.createElement('details');
            el.className = 'voice-agent-tool-row';
            el.appendChild(document.createElement('summary'));
            el.appendChild(createDiv(null, 'voice-agent-tool-body'));
            bubbleEls.set(entry, el);
            els.transcript.appendChild(el);
        }
        el.querySelector('summary').innerText = `${translate('Tool')}: ${entry.name}${entry.result === null ? '…' : ''}`;
        el.querySelector('.voice-agent-tool-body').innerText = `${entry.args || ''}${entry.result !== null ? `\n→ ${entry.result}` : ''}`;
        scrollToLatest(false);
        refreshControls();
    }

    function addMetrics(metrics) {
        const bubble = AudioVoiceState.addMetrics(transcript, metrics);
        if (bubble) {
            drawBubble(bubble);
        }
    }

    function pushNotice(text, severity) {
        AudioVoiceState.pushNotice(notices, text, severity);
        renderNotices();
    }

    /** Report a failure the way the user can act on it: what happened, then what to do. */
    function reportError(kind, error) {
        const why = AudioVoiceState.explainError(kind, error);
        pushNotice(`${translate(why.title)}${why.hint ? ' ' + translate(why.hint) : ''}`, 'error');
    }

    function renderNotices() {
        els.notices.textContent = '';
        for (const notice of notices.list) {
            const chip = createDiv(null, `voice-agent-notice voice-agent-notice-${notice.severity}`);
            chip.appendChild(createSpan(null, 'voice-agent-notice-text', notice.count > 1 ? `${notice.text} (×${notice.count})` : notice.text));
            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'voice-agent-notice-close';
            close.innerText = '×';
            close.title = translate('Dismiss');
            close.addEventListener('click', () => {
                AudioVoiceState.dismissNotice(notices, notice.id);
                renderNotices();
            });
            chip.appendChild(close);
            els.notices.appendChild(chip);
        }
    }

    // ===== Transcript tools =====

    function onCopyClick() {
        const text = AudioVoiceState.toText(transcript, { user: translate('You'), assistant: translate('Agent') });
        copyText(text); // core helper: also works over plain-HTTP LAN, where navigator.clipboard does not exist
        pushNotice(translate('Copied the conversation.'), 'info');
    }

    function onExport(kind) {
        if (!kind) {
            return;
        }
        const labels = { user: translate('You'), assistant: translate('Agent') };
        const body = kind === 'json' ? AudioVoiceState.toJson(transcript)
            : kind === 'md' ? AudioVoiceState.toMarkdown(transcript, labels) : AudioVoiceState.toText(transcript, labels);
        const url = URL.createObjectURL(new Blob([body], { type: kind === 'json' ? 'application/json' : 'text/plain' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = `voice-call.${kind}`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    return { render };
})();
