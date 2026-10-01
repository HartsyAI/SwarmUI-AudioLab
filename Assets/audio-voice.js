/**
 * AudioVoice -- the "Voice Agent" bottom tab: a live phone-style call with the server's
 * AudioLabVoiceSession WebSocket (HartsyInference.Voice, answered through LLMAssistant).
 *
 * Built once per page session (audio-daw.js calls render(container) exactly once, the same "built ONCE, not
 * in updateBottomPanel" treatment Beats/Generate/Score get -- see that file's buildBottomPanel), so a live call
 * survives switching to another bottom tab and back, instead of being torn down and losing its socket.
 *
 * Reuses SwarmUI utilities: createDiv(), createSpan(), translate(), getWSAddress(), genericRequest(),
 * doNoticePopover(). Deliberately does NOT use makeWSRequest() (site.js): that helper's onmessage assumes every
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

    let rendered = false;
    let els = {};

    // Networking + audio graph. Null whenever a call is not active.
    let ws = null;
    let audioCtx = null;
    let micStream = null;
    let micSource = null;
    let captureNode = null;
    let analyserNode = null;
    let meterRaf = null;

    // Playback scheduling: one AudioContext, sources queued back to back, each tagged with the turn it came
    // from so a `bargein` event can stop/drop only that turn's audio.
    let nextPlayTime = 0;
    let scheduledSources = []; // { turnId, source }

    let callState = 'idle'; // idle | connecting | Warming | Listening | Thinking | Speaking | ToolRunning | ended

    function render(container) {
        if (!container || rendered) {
            return;
        }
        rendered = true;
        buildUi(container);
        loadModels();
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

        // --- Setup row: model / assistant / voice / system prompt / barge-in ---
        const setup = createDiv(null, 'voice-agent-setup');
        els.model = document.createElement('select');
        els.model.className = 'form-control voice-agent-select';
        labeled(setup, 'Model', els.model);

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
        root.appendChild(setup);

        els.systemPrompt = document.createElement('textarea');
        els.systemPrompt.className = 'form-control voice-agent-prompt';
        els.systemPrompt.rows = 2;
        els.systemPrompt.placeholder = translate('System prompt (leave blank for the assistant default)');
        root.appendChild(els.systemPrompt);

        // --- Transport row: start/stop, state, mic meter ---
        const transport = createDiv(null, 'voice-agent-transport');
        els.startBtn = btn(transport, translate('Start'), 'basic-button voice-agent-start', onStartClick);
        els.stopBtn = btn(transport, translate('Stop'), 'basic-button voice-agent-stop', onStopClick);
        els.stopBtn.disabled = true;
        els.state = createSpan(null, 'voice-agent-state voice-agent-state-idle', translate('Idle'));
        transport.appendChild(els.state);
        els.meterTrack = createDiv(null, 'voice-agent-meter-track');
        els.meterFill = createDiv(null, 'voice-agent-meter-fill');
        els.meterTrack.appendChild(els.meterFill);
        transport.appendChild(els.meterTrack);
        root.appendChild(transport);

        // --- Notices ---
        els.notices = createDiv(null, 'voice-agent-notices');
        root.appendChild(els.notices);

        // --- Transcript ---
        els.transcript = createDiv(null, 'voice-agent-transcript');
        root.appendChild(els.transcript);

        // --- Metrics ---
        els.metrics = createDiv(null, 'voice-agent-metrics');
        root.appendChild(els.metrics);

        container.appendChild(root);
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
            els.model.outerHTML = '';
            els.model = document.createElement('input');
            els.model.type = 'text';
            els.model.className = 'form-control voice-agent-input';
            els.model.placeholder = translate('model id');
        });
    }

    // ===== Call lifecycle =====

    async function onStartClick() {
        if (callState !== 'idle' && callState !== 'ended') {
            return;
        }
        els.startBtn.disabled = true;
        try {
            micStream = await navigator.mediaDevices.getUserMedia({
                audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
            });
        }
        catch (e) {
            addNotice(`${translate('Could not open the microphone:')} ${e.message || e}`, true);
            els.startBtn.disabled = false;
            return;
        }
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        try {
            await audioCtx.audioWorklet.addModule(WORKLET_URL);
        }
        catch (e) {
            addNotice(`${translate('Could not load the microphone worklet:')} ${e.message || e}`, true);
            teardownAudio();
            els.startBtn.disabled = false;
            return;
        }
        micSource = audioCtx.createMediaStreamSource(micStream);
        analyserNode = audioCtx.createAnalyser();
        analyserNode.fftSize = 512;
        micSource.connect(analyserNode);
        startMeter();

        const frameSize = Math.round(audioCtx.sampleRate * (CAPTURE_FRAME_MS / 1000));
        captureNode = new AudioWorkletNode(audioCtx, 'voice-capture-processor', { processorOptions: { frameSize } });
        captureNode.port.onmessage = (ev) => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(ev.data);
            }
        };
        micSource.connect(captureNode);

        clearTranscript();
        clearMetrics();
        connectSocket(Math.round(audioCtx.sampleRate));
        els.stopBtn.disabled = false;
    }

    function onStopClick() {
        if (ws) {
            try {
                ws.send(JSON.stringify({ end: true }));
            }
            catch (e) { /* socket may already be closing */ }
            try {
                ws.close();
            }
            catch (e) { /* ignore */ }
        }
        endCallLocally();
    }

    function endCallLocally() {
        ws = null;
        teardownAudio();
        flushAllScheduledAudio();
        setState('idle');
        els.startBtn.disabled = false;
        els.stopBtn.disabled = true;
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
        analyserNode = null;
        // The playback AudioContext stays alive across a call (nextPlayTime/scheduled sources use it, and a
        // fresh one would need another user gesture to unlock); it is only ever torn down implicitly by the
        // page unloading.
    }

    // ===== WebSocket =====

    function connectSocket(inputRate) {
        setState('connecting');
        const address = getWSAddress();
        if (!address) {
            addNotice(translate('Could not determine the WebSocket address for this server.'), true);
            endCallLocally();
            return;
        }
        ws = new WebSocket(`${address}/API/AudioLabVoiceSession`);
        ws.binaryType = 'arraybuffer';
        ws.onopen = () => {
            ws.send(JSON.stringify({
                session_id: session_id,
                model: els.model.value,
                assistantId: els.assistantId.value.trim() || undefined,
                voice: els.voice.value,
                systemPrompt: els.systemPrompt.value.trim() || undefined,
                bargeIn: els.bargeIn.checked,
                inputRate: inputRate,
            }));
        };
        ws.onmessage = onSocketMessage;
        ws.onerror = () => {
            addNotice(translate('The voice connection reported an error.'), true);
        };
        ws.onclose = () => {
            if (ws !== null) { // the user didn't request Stop; the server/connection ended the call
                endCallLocally();
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
        }
        else if (data.tool_call) {
            addNotice(`${translate('Tool call:')} ${data.tool_call.name} ${data.tool_call.arguments || ''}`);
        }
        else if (data.tool_result) {
            addNotice(`${translate('Tool result:')} ${data.tool_result.name} -> ${data.tool_result.result || ''}`);
        }
        else if (data.notice) {
            addNotice(data.notice);
        }
        else if (data.metrics) {
            addMetrics(data.metrics);
        }
        else if (data.error) {
            addNotice(data.error, true);
        }
    }

    // ===== Outbound playback (turn-tagged PCM16 @ 24 kHz) =====

    function playOutboundFrame(buffer) {
        if (buffer.byteLength <= 4) {
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
        source.connect(audioCtx.destination);
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

    // ===== Mic level meter =====

    function startMeter() {
        const data = new Uint8Array(analyserNode.fftSize);
        const step = () => {
            if (!analyserNode) {
                return;
            }
            analyserNode.getByteTimeDomainData(data);
            let peak = 0;
            for (let i = 0; i < data.length; i++) {
                peak = Math.max(peak, Math.abs(data[i] - 128));
            }
            const level = Math.min(1, peak / 100);
            els.meterFill.style.width = `${Math.round(level * 100)}%`;
            meterRaf = requestAnimationFrame(step);
        };
        meterRaf = requestAnimationFrame(step);
    }

    // ===== Transcript / notices / metrics =====

    function setState(state) {
        callState = state;
        els.state.innerText = translate(state);
        els.state.className = `voice-agent-state voice-agent-state-${state.toLowerCase()}`;
    }

    function clearTranscript() {
        els.transcript.innerHTML = '';
    }

    function addTranscript(role, text, turnId) {
        const line = createDiv(null, `voice-agent-line voice-agent-line-${role}`);
        line.appendChild(createSpan(null, 'voice-agent-line-role', role === 'user' ? translate('You') : translate('Agent')));
        line.appendChild(createSpan(null, 'voice-agent-line-text', ` ${text}`));
        els.transcript.appendChild(line);
        els.transcript.scrollTop = els.transcript.scrollHeight;
    }

    function addNotice(text, isError) {
        const line = createDiv(null, isError ? 'voice-agent-notice voice-agent-notice-error' : 'voice-agent-notice');
        line.innerText = text;
        els.notices.appendChild(line);
        els.notices.scrollTop = els.notices.scrollHeight;
        if (typeof doNoticePopover === 'function') {
            doNoticePopover(text, isError ? 'notice-pop-red' : 'notice-pop-yellow');
        }
    }

    function clearMetrics() {
        els.metrics.innerHTML = '';
    }

    function addMetrics(metrics) {
        const row = createDiv(null, 'voice-agent-metrics-row');
        const fields = [
            ['stt', metrics['voice.stt.ms']],
            ['llm ttft', metrics['voice.llm.ttft_ms']],
            ['tts first chunk', metrics['voice.tts.first_chunk_ms']],
            ['turn total', metrics['voice.turn.total_ms']],
        ];
        for (const [label, value] of fields) {
            const cell = createSpan(null, 'voice-agent-metrics-cell');
            cell.innerText = `${label}: ${value == null ? '-' : `${Math.round(value)}ms`}`;
            row.appendChild(cell);
        }
        els.metrics.appendChild(row);
        els.metrics.scrollTop = els.metrics.scrollHeight;
    }

    return { render };
})();
