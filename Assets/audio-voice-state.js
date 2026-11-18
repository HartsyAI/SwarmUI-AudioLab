/**
 * Audio Lab Voice Agent: call state, transcript and notices.
 *
 * Pure logic over plain objects: no DOM, no audio, no socket. The Voice Agent tab (`audio-voice.js`) feeds it what the
 * server's `AudioLabVoiceSession` socket sends and draws what it returns, so the rules (which events make a bubble, where a
 * turn's latency numbers land, how a notice repeats, what an error means to the user) can be exercised from a browser console:
 * `AudioVoiceState.describeState('Thinking')`.
 *
 * Transcript shape: `{ entries: [] }`. An entry is `{ kind: 'user' | 'assistant', turnId, parts: [text], time, metrics }` or
 * `{ kind: 'tool', turnId, id, name, args, result, time }`. Several `transcript` events for one role and turn extend one bubble.
 */
const AudioVoiceState = (() => {
    'use strict';

    /** How long an informational notice stays on screen, in milliseconds; errors stay until dismissed. */
    const NOTICE_LIFETIME_MS = 8000;

    /** The server's call states and what the tab shows for each. `tone` picks the colour, `listening` marks a state where the mic matters. */
    const STATES = {
        idle: { label: 'Idle', tone: 'idle', hint: 'Press Start to talk.', live: false },
        connecting: { label: 'Connecting', tone: 'busy', hint: 'Opening the call…', live: true },
        warming: { label: 'Warming up', tone: 'busy', hint: 'The speech models are loading; the first call takes a few seconds longer.', live: true },
        listening: { label: 'Listening', tone: 'listen', hint: 'Speak now.', live: true },
        thinking: { label: 'Thinking', tone: 'think', hint: 'The assistant is working on a reply.', live: true },
        toolrunning: { label: 'Using a tool', tone: 'think', hint: 'The assistant is running a tool.', live: true },
        speaking: { label: 'Speaking', tone: 'speak', hint: 'Talk over it to interrupt (barge-in).', live: true },
        ended: { label: 'Call ended', tone: 'idle', hint: 'The call is over.', live: false }
    };

    /** The words for a server state, with a tone for its colour. Unknown states fall back to a neutral label. */
    function describeState(state) {
        const key = String(state || 'idle').toLowerCase();
        const found = STATES[key];
        return found ? { key, ...found } : { key, label: String(state), tone: 'busy', hint: '', live: true };
    }

    /** Seconds as "m:ss" for the call timer. */
    function clock(seconds) {
        const total = Math.max(0, Math.floor(seconds || 0));
        return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
    }

    /** A fresh, empty transcript. */
    function createTranscript() {
        return { entries: [] };
    }

    /** The last bubble of this kind for this turn, if any. */
    function findBubble(transcript, kind, turnId) {
        for (let i = transcript.entries.length - 1; i >= 0; i--) {
            const e = transcript.entries[i];
            if (e.kind === kind && e.turnId === turnId) return e;
        }
        return null;
    }

    /** Whether a tool call was added after this entry, so later text of the turn must start a new bubble to keep the order true. */
    function toolSince(transcript, entry) {
        return transcript.entries.slice(transcript.entries.indexOf(entry) + 1).some(e => e.kind === 'tool');
    }

    /**
     * Add a `transcript` event. A second event for the same role and turn extends that bubble rather than starting a new one.
     * @returns {{entry: object, created: boolean}}
     */
    function addTranscript(transcript, role, text, turnId, now = Date.now()) {
        const kind = role === 'user' ? 'user' : 'assistant';
        const clean = String(text || '').trim();
        const id = Number.isFinite(turnId) ? turnId : 0;
        const found = id > 0 ? findBubble(transcript, kind, id) : null;
        const existing = found && !toolSince(transcript, found) ? found : null;
        if (existing) {
            if (clean) existing.parts.push(clean);
            return { entry: existing, created: false };
        }
        const entry = { kind, turnId: id, parts: clean ? [clean] : [], time: now, metrics: null };
        transcript.entries.push(entry);
        return { entry, created: true };
    }

    /** The text of a bubble, its parts joined by a space. */
    function textOf(entry) {
        return entry.parts.join(' ');
    }

    /** Add a tool call, or fill in the result of the call with the same id. */
    function addTool(transcript, { id, name, args, result, turnId }, now = Date.now()) {
        let entry = id ? transcript.entries.find(e => e.kind === 'tool' && e.id === id) : null;
        if (!entry) {
            entry = { kind: 'tool', turnId: turnId || 0, id: id || '', name: name || '', args: '', result: null, time: now };
            transcript.entries.push(entry);
        }
        if (name) entry.name = name;
        if (args !== undefined && args !== null) entry.args = String(args);
        if (result !== undefined && result !== null) entry.result = String(result);
        return entry;
    }

    /**
     * Put a `metrics` event's numbers on the assistant bubble of the same turn (or on the bubble that arrives for it later:
     * numbers for a turn with no bubble yet are held in `transcript.pendingMetrics`).
     * @returns {object|null} the bubble that now carries them
     */
    function addMetrics(transcript, metrics) {
        if (!metrics || typeof metrics !== 'object') return null;
        const turnId = Number.isFinite(metrics.turnId) ? metrics.turnId : 0;
        const bubble = turnId > 0 ? findBubble(transcript, 'assistant', turnId) : null;
        if (bubble) {
            bubble.metrics = metrics;
            return bubble;
        }
        (transcript.pendingMetrics ||= new Map()).set(turnId, metrics);
        return null;
    }

    /** Hand over metrics that arrived before their bubble did, once the assistant bubble for `turnId` exists. */
    function takePendingMetrics(transcript, turnId) {
        const held = transcript.pendingMetrics?.get(turnId) || null;
        if (held) transcript.pendingMetrics.delete(turnId);
        return held;
    }

    /** One short duration: "139 ms", "1.2 s", or "-" when the server did not report it. */
    function formatMs(value) {
        if (!Number.isFinite(value)) return '-';
        return value >= 1000 ? `${(value / 1000).toFixed(1)} s` : `${Math.round(value)} ms`;
    }

    /** The latency numbers of a turn as `[{label, text}]`, in call order: hearing, thinking, first sound, whole turn. */
    function latencyOf(metrics) {
        if (!metrics) return [];
        return [
            { label: 'heard', text: formatMs(metrics['voice.stt.ms']) },
            { label: 'thought', text: formatMs(metrics['voice.llm.ttft_ms']) },
            { label: 'first sound', text: formatMs(metrics['voice.tts.first_chunk_ms']) },
            { label: 'turn', text: formatMs(metrics['voice.turn.total_ms']) }
        ];
    }

    /**
     * A queue of notices. `push` repeats of the same text and severity within the queue count up instead of stacking, errors
     * stay until `dismiss`, and `expire` drops the informational ones older than `NOTICE_LIFETIME_MS`.
     */
    function createNotices() {
        return { list: [], seq: 0 };
    }

    function pushNotice(notices, text, severity = 'info', now = Date.now()) {
        const clean = String(text || '').trim();
        if (!clean) return null;
        const same = notices.list.find(n => n.text === clean && n.severity === severity);
        if (same) {
            same.count++;
            same.time = now;
            return same;
        }
        const notice = { id: ++notices.seq, text: clean, severity, count: 1, time: now };
        notices.list.push(notice);
        return notice;
    }

    function dismissNotice(notices, id) {
        const i = notices.list.findIndex(n => n.id === id);
        if (i >= 0) notices.list.splice(i, 1);
    }

    /** Drop informational notices older than the lifetime; returns whether anything went. */
    function expireNotices(notices, now = Date.now()) {
        const before = notices.list.length;
        notices.list = notices.list.filter(n => n.severity === 'error' || now - n.time < NOTICE_LIFETIME_MS);
        return notices.list.length !== before;
    }

    /**
     * What a failure means to the person using the tab: a title and what to do next.
     * @param {'mic'|'worklet'|'socket'|'server'|'address'} kind
     * @param {*} [error] - the exception or the socket close event (`code`, `reason`)
     */
    function explainError(kind, error) {
        const name = String(error?.name || '');
        const detail = String(error?.message || error?.reason || '').trim();
        switch (kind) {
            case 'mic':
                if (name === 'NotAllowedError' || name === 'SecurityError') {
                    return { title: 'The microphone is blocked.', hint: 'Allow microphone access for this page in the browser\'s site settings, then press Start again.' };
                }
                if (name === 'NotFoundError' || name === 'OverconstrainedError') {
                    return { title: 'No microphone was found.', hint: 'Plug one in or choose an input in your system settings, then press Start again.' };
                }
                if (name === 'NotReadableError') {
                    return { title: 'The microphone is in use by another app.', hint: 'Close the other app and press Start again.' };
                }
                return { title: 'Could not open the microphone.', hint: detail };
            case 'worklet':
                return { title: 'The audio capture module did not load.', hint: detail || 'Reload the page and try again.' };
            case 'address':
                return { title: 'Could not find the server\'s WebSocket address.', hint: 'Reload the page and try again.' };
            case 'socket': {
                const code = Number(error?.code);
                const why = detail || (code === 1006 ? 'The connection dropped without a reason.' : '');
                return { title: 'The call was disconnected.', hint: why ? `${why} Press Reconnect to start again.` : 'Press Reconnect to start again.' };
            }
            default:
                return { title: 'The voice server reported an error.', hint: detail };
        }
    }

    /** One step of a level smoother: rises quickly, falls slowly, so a meter reads as speech instead of flicker. */
    function smoothLevel(previous, next, attack = 0.6, release = 0.12) {
        const k = next > previous ? attack : release;
        return previous + (next - previous) * k;
    }

    /** "12:03:44" style time of day for a bubble. */
    function timeOfDay(ms) {
        const d = new Date(ms);
        return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
    }

    /** The transcript as plain text: one "[time] You: ..." line per bubble, tool calls as indented lines. */
    function toText(transcript, labels = { user: 'You', assistant: 'Agent' }) {
        return transcript.entries.map(e => {
            if (e.kind === 'tool') return `[${timeOfDay(e.time)}] Tool ${e.name}(${e.args})${e.result !== null ? ` -> ${e.result}` : ''}`;
            return `[${timeOfDay(e.time)}] ${labels[e.kind]}: ${textOf(e)}`;
        }).join('\n');
    }

    /** The transcript as Markdown: bold speaker, the latency of each agent reply in italics. */
    function toMarkdown(transcript, labels = { user: 'You', assistant: 'Agent' }) {
        return transcript.entries.map(e => {
            if (e.kind === 'tool') {
                const flat = (v) => String(v).replace(/\s+/g, ' ').replace(/`/g, "'"); // one line, no stray backticks: keeps the inline code intact
                return `> Tool \`${flat(e.name)}\` \`${flat(e.args)}\`${e.result !== null ? ` → ${flat(e.result)}` : ''}`;
            }
            const lat = e.kind === 'assistant' && e.metrics ? `\n\n*${latencyOf(e.metrics).map(l => `${l.label} ${l.text}`).join(' · ')}*` : '';
            return `**${labels[e.kind]}** (${timeOfDay(e.time)}): ${textOf(e)}${lat}`;
        }).join('\n\n');
    }

    /** The transcript as JSON, with the raw metrics. */
    function toJson(transcript) {
        return JSON.stringify(transcript.entries.map(e => e.kind === 'tool'
            ? { kind: 'tool', turnId: e.turnId, time: e.time, name: e.name, args: e.args, result: e.result }
            : { kind: e.kind, turnId: e.turnId, time: e.time, text: textOf(e), metrics: e.metrics || undefined }), null, 2);
    }

    return {
        describeState, clock, createTranscript, addTranscript, textOf, addTool, addMetrics, takePendingMetrics, formatMs, latencyOf,
        createNotices, pushNotice, dismissNotice, expireNotices, explainError, smoothLevel, timeOfDay, toText, toMarkdown, toJson,
        NOTICE_LIFETIME_MS
    };
})();
