/**
 * AudioDawInstruments: instrument tracks for the DAW. A polyphonic synth and a sampler (any clip, including a
 * generated one-shot, played across the keyboard), MIDI clips with sample-accurate playback and export, live
 * playing from the computer keyboard, on-screen keys or a Web MIDI device, Standard MIDI File import/export,
 * and the piano roll editor.
 * Notes are stored in seconds relative to their clip (the DAW's clips are time-based), quantized against the
 * project grid when edited. ctx-agnostic: the same voice builder runs live and in the OfflineAudioContext.
 * Reuses SwarmUI utilities: createDiv(), createSpan(), translate().
 */
const AudioDawInstruments = (() => {
    'use strict';

    // ===== SOUNDS =====

    /** Synth presets: two oscillators -> low-pass filter (with envelope) -> amp envelope. */
    const SYNTH_PRESETS = {
        'Soft Keys': { osc1: 'triangle', osc2: 'sine', ratio2: 2, mix2: 0.25, detune: 0, cutoff: 3200, resonance: 1, filterEnv: 0.6, attack: 0.004, decay: 0.7, sustain: 0.25, release: 0.45, gain: 0.55 },
        'Warm Pad': { osc1: 'sawtooth', osc2: 'sawtooth', ratio2: 1, mix2: 0.8, detune: 14, cutoff: 1100, resonance: 2, filterEnv: 0.3, attack: 0.5, decay: 1.2, sustain: 0.8, release: 1.4, gain: 0.35 },
        'Sub Bass': { osc1: 'sine', osc2: 'square', ratio2: 0.5, mix2: 0.18, detune: 0, cutoff: 700, resonance: 1, filterEnv: 0.2, attack: 0.004, decay: 0.25, sustain: 0.9, release: 0.12, gain: 0.7 },
        'Saw Lead': { osc1: 'sawtooth', osc2: 'sawtooth', ratio2: 1, mix2: 0.6, detune: 9, cutoff: 3800, resonance: 6, filterEnv: 0.5, attack: 0.01, decay: 0.3, sustain: 0.7, release: 0.2, gain: 0.35 },
        'Pluck': { osc1: 'square', osc2: 'sawtooth', ratio2: 1, mix2: 0.5, detune: 4, cutoff: 1600, resonance: 3, filterEnv: 1.6, attack: 0.002, decay: 0.28, sustain: 0, release: 0.18, gain: 0.45 },
        'Organ': { osc1: 'sine', osc2: 'sine', ratio2: 2, mix2: 0.55, detune: 0, cutoff: 6000, resonance: 0.5, filterEnv: 0, attack: 0.01, decay: 0.1, sustain: 1, release: 0.08, gain: 0.45 },
        'Bell': { osc1: 'sine', osc2: 'sine', ratio2: 2.76, mix2: 0.6, detune: 0, cutoff: 8000, resonance: 0.5, filterEnv: 0, attack: 0.002, decay: 1.6, sustain: 0, release: 1.2, gain: 0.45 }
    };

    const SYNTH_KNOBS = [
        { key: 'cutoff', label: 'Cutoff', min: 100, max: 12000, taper: 'log', fmt: v => v >= 1000 ? (v / 1000).toFixed(1) + 'k' : Math.round(v) + '' },
        { key: 'resonance', label: 'Res', min: 0.3, max: 15, fmt: v => v.toFixed(1) },
        { key: 'filterEnv', label: 'F.Env', min: 0, max: 3, fmt: v => v.toFixed(2) },
        { key: 'mix2', label: 'Osc 2', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' },
        { key: 'detune', label: 'Detune', min: 0, max: 50, fmt: v => Math.round(v) + 'c' },
        { key: 'attack', label: 'Attack', min: 0.001, max: 2, taper: 'log', fmt: v => Math.round(v * 1000) + 'ms' },
        { key: 'decay', label: 'Decay', min: 0.01, max: 3, taper: 'log', fmt: v => Math.round(v * 1000) + 'ms' },
        { key: 'sustain', label: 'Sustain', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' },
        { key: 'release', label: 'Release', min: 0.01, max: 4, taper: 'log', fmt: v => Math.round(v * 1000) + 'ms' },
        { key: 'gain', label: 'Level', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' }
    ];

    const WAVES = ['sine', 'triangle', 'square', 'sawtooth'];

    /** A fresh instrument for a track. type: 'synth' or 'sampler'. */
    function createInstrument(type = 'synth', preset = 'Soft Keys') {
        if (type === 'sampler') {
            return { type: 'sampler', name: translate('Sampler'), sampleKey: null, sampleName: '', rootNote: 60, attack: 0.002, release: 0.2, gain: 0.8, oneShot: false };
        }
        const p = SYNTH_PRESETS[preset] || SYNTH_PRESETS['Soft Keys'];
        return { type: 'synth', preset, ...JSON.parse(JSON.stringify(p)) };
    }

    function midiToFreq(m) {
        return 440 * Math.pow(2, (m - 69) / 12);
    }

    /**
     * Start one voice at ctx time `when`; it releases at `when + duration` (or on stop() when held live).
     * @param {Object} sample - AudioBuffer for the sampler, or null
     * @returns {{stop(at): void, nodes: AudioNode[]}}
     */
    function startVoice(ctx, dest, inst, pitch, velocity, when, duration = null, sample = null) {
        const vel = Math.max(0.05, Math.min(1, (velocity || 100) / 127));
        const amp = ctx.createGain();
        amp.gain.value = 0;
        amp.connect(dest);
        const nodes = [amp];
        const sources = [];
        let releaseTime;
        let envAt = () => 0; // the amp envelope's level at ctx time t, so a release starts from where it really is
        let attackEnd = when;
        if (inst.type === 'sampler') {
            if (!sample) return { stop: () => {}, nodes };
            const src = ctx.createBufferSource();
            src.buffer = sample;
            src.playbackRate.value = Math.pow(2, (pitch - (inst.rootNote || 60)) / 12);
            src.connect(amp);
            sources.push(src);
            nodes.push(src);
            const level = (inst.gain ?? 0.8) * vel;
            const atk = Math.max(0.001, inst.attack || 0.002);
            amp.gain.setValueAtTime(0, when);
            amp.gain.linearRampToValueAtTime(level, when + atk);
            attackEnd = when + atk;
            envAt = (t) => level * Math.max(0, Math.min(1, (t - when) / atk));
            releaseTime = Math.max(0.01, inst.release || 0.2);
            src.start(when);
        }
        else {
            const f = midiToFreq(pitch);
            const filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.Q.value = inst.resonance ?? 1;
            filter.connect(amp);
            nodes.push(filter);
            const mk = (type, freq, detune, level) => {
                const osc = ctx.createOscillator();
                osc.type = type;
                osc.frequency.value = freq;
                osc.detune.value = detune;
                const g = ctx.createGain();
                g.gain.value = level;
                osc.connect(g);
                g.connect(filter);
                nodes.push(osc, g);
                sources.push(osc);
                osc.start(when);
            };
            const mix2 = inst.mix2 ?? 0.5;
            mk(inst.osc1 || 'sawtooth', f, -(inst.detune || 0) / 2, 1 - mix2 * 0.5);
            if (mix2 > 0.001) mk(inst.osc2 || 'sawtooth', f * (inst.ratio2 || 1), (inst.detune || 0) / 2, mix2);
            // Filter: base cutoff, swept up by the envelope amount over the attack, settling with the decay
            const base = Math.min(ctx.sampleRate / 2.2, inst.cutoff || 3000);
            const peak = Math.min(ctx.sampleRate / 2.2, base * (1 + (inst.filterEnv || 0) * 3));
            const atk = Math.max(0.001, inst.attack || 0.005);
            const dec = Math.max(0.005, inst.decay || 0.3);
            filter.frequency.setValueAtTime(base, when);
            filter.frequency.linearRampToValueAtTime(peak, when + atk);
            filter.frequency.setTargetAtTime(base + (peak - base) * (inst.sustain ?? 0.5), when + atk, dec / 3);
            const level = (inst.gain ?? 0.5) * vel * 0.6;
            const hold = level * (inst.sustain ?? 0.5);
            amp.gain.setValueAtTime(0, when);
            amp.gain.linearRampToValueAtTime(level, when + atk);
            amp.gain.setTargetAtTime(hold, when + atk, dec / 3);
            attackEnd = when + atk;
            envAt = (t) => {
                const dt = t - when;
                if (dt <= 0) return 0;
                if (dt < atk) return level * dt / atk;
                return hold + (level - hold) * Math.exp(-(dt - atk) / (dec / 3));
            };
            releaseTime = Math.max(0.01, inst.release || 0.2);
        }
        let stopped = false;
        const stop = (at) => {
            if (stopped) return;
            stopped = true;
            const t = Math.max(at, when);
            try {
                // Canceling at t removes an attack ramp ending at or after t outright, which left the gain at 0 until
                // the note-off (a note no longer than the attack was silent). Re-ramp to the level the attack had
                // reached instead; cancelAndHoldAtTime would do this, but Firefox lacks it.
                amp.gain.cancelScheduledValues(t);
                if (t <= attackEnd) amp.gain.linearRampToValueAtTime(envAt(t), t);
                else amp.gain.setValueAtTime(envAt(t), t);
                amp.gain.setTargetAtTime(0, t, releaseTime / 4);
            } catch (_) {}
            for (const s of sources) {
                try { s.stop(t + releaseTime * 1.5 + 0.05); } catch (_) {}
            }
            sources[0] && (sources[0].onended = () => { for (const n of nodes) { try { n.disconnect(); } catch (_) {} } });
        };
        if (duration !== null) stop(when + Math.max(0.01, duration));
        return { stop, nodes, kill: () => { for (const s of sources) { try { s.stop(); } catch (_) {} } for (const n of nodes) { try { n.disconnect(); } catch (_) {} } } };
    }

    // ===== SCHEDULING =====

    /** The sample a sampler track plays, from the DAW's blob store (decoded lazily). */
    let sampleResolver = null;

    /** The DAW tells this module how to find a sampler's decoded buffer by blob key. */
    function setSampleResolver(fn) {
        sampleResolver = fn;
    }

    function trackSample(track) {
        const inst = track.instrument;
        if (!inst || inst.type !== 'sampler' || !inst.sampleKey || !sampleResolver) return null;
        return sampleResolver(inst.sampleKey);
    }

    /** Visible length of a MIDI clip in seconds. */
    function midiClipLength(clip) {
        return Math.max(0.05, clip.length || 0);
    }

    /**
     * Every note of a track that starts inside [from, to) on the timeline, with its absolute timeline start.
     * Layering matches audio clips: a later clip covers earlier ones, and covered notes do not sound.
     */
    function notesInWindow(track, from, to) {
        const out = [];
        const clips = track.midiClips || [];
        for (let i = 0; i < clips.length; i++) {
            const clip = clips[i];
            if (clip.muted) continue;
            const cs = clip.startTime;
            const ce = cs + midiClipLength(clip);
            if (ce <= from || cs >= to) continue;
            const covered = (t) => clips.some((c, j) => j > i && !c.muted && t >= c.startTime && t < c.startTime + midiClipLength(c));
            for (const n of clip.notes) {
                const t = cs + n.start;
                if (t < from || t >= to || n.start >= midiClipLength(clip)) continue;
                if (covered(t)) continue;
                const dur = Math.min(n.dur, midiClipLength(clip) - n.start);
                out.push({ key: `${clip.id}:${n.id}`, time: t, dur, pitch: n.pitch, vel: n.vel });
            }
        }
        return out;
    }

    /**
     * Note chase: notes that began before `t` and still sound at it, as notes starting at `t` with their remaining
     * length. Starting playback, a loop pass or a range render inside a held note plays the rest of it.
     */
    function notesSoundingAt(track, t) {
        const out = [];
        const clips = track.midiClips || [];
        for (let i = 0; i < clips.length; i++) {
            const clip = clips[i];
            if (clip.muted) continue;
            const cs = clip.startTime;
            const len = midiClipLength(clip);
            if (cs >= t || cs + len <= t) continue;
            if (clips.some((c, j) => j > i && !c.muted && t >= c.startTime && t < c.startTime + midiClipLength(c))) continue;
            for (const n of clip.notes) {
                if (n.start >= len) continue;
                const on = cs + n.start;
                const end = cs + n.start + Math.min(n.dur, len - n.start);
                if (on >= t - 0.005 || end <= t + 0.005) continue;
                out.push({ key: `${clip.id}:${n.id}`, time: t, dur: end - t, pitch: n.pitch, vel: n.vel, chased: true });
            }
        }
        return out;
    }

    /**
     * Offline export: schedule all of a track's notes in the render window.
     * @param {Object} origin - { t0Ctx, t0Timeline, windowEnd }
     */
    function scheduleTrack(ctx, dest, track, origin) {
        if (track.kind !== 'instrument' || !track.instrument) return;
        const sample = trackSample(track);
        const notes = [...notesSoundingAt(track, origin.t0Timeline), ...notesInWindow(track, origin.t0Timeline, origin.windowEnd)];
        for (const n of notes) {
            const when = origin.t0Ctx + (n.time - origin.t0Timeline);
            const dur = Math.min(n.dur, origin.windowEnd - n.time);
            startVoice(ctx, dest, track.instrument, n.pitch, n.vel, when, dur, sample);
        }
    }

    /**
     * Live transport: schedule notes falling in [timelineFrom, timelineTo) of one pass, skipping keys already
     * scheduled for that pass. Returns the voices started.
     */
    function scheduleWindow(ctx, dest, track, pass, timelineFrom, timelineTo, seen) {
        if (track.kind !== 'instrument' || !track.instrument) return [];
        const sample = trackSample(track);
        const voices = [];
        // Notes held across this pass's start chase in once per pass (the seen set keys carry the pass)
        const notes = [...notesSoundingAt(track, pass.baseTl), ...notesInWindow(track, timelineFrom, timelineTo)];
        for (const n of notes) {
            const id = `${pass.baseCtx.toFixed(4)}|${n.key}`;
            if (seen.has(id)) continue;
            seen.add(id);
            let when = pass.baseCtx + (n.time - pass.baseTl);
            let dur = pass.end < Infinity ? Math.min(n.dur, pass.end - n.time) : n.dur;
            if (n.chased && when < ctx.currentTime && ctx.currentTime - when < 0.5) {
                // The first tick came just after the pass began: join the held note now (later than that, a
                // re-check after the seen set was cleared, it stays skipped)
                dur -= ctx.currentTime - when;
                when = ctx.currentTime;
            }
            if (when < ctx.currentTime - 0.01 || dur <= 0.01) continue;
            voices.push(startVoice(ctx, dest, track.instrument, n.pitch, n.vel, when, Math.max(0.01, dur), sample));
        }
        return voices;
    }

    // ===== LIVE PLAYING: computer keyboard, on-screen keys, Web MIDI =====

    // Computer keyboard as a piano: home row = white keys, row above = black keys (the standard musical-typing
    // layout). Z / X shift the octave.
    const QWERTY = { a: 0, w: 1, s: 2, e: 3, d: 4, f: 5, t: 6, g: 7, y: 8, h: 9, u: 10, j: 11, k: 12, o: 13, l: 14, p: 15, ';': 16 };
    const live = {
        qwerty: false,      // computer-keyboard playing on/off
        octave: 4,          // C4 = MIDI 60 on the 'a' key
        held: new Map(),    // key -> midi
        midiAccess: null,
        handler: null       // { noteOn(pitch, vel), noteOff(pitch) } supplied by the DAW
    };

    function setLiveHandler(handler) {
        live.handler = handler;
    }

    function setQwerty(on) {
        live.qwerty = !!on;
        if (!live.qwerty) releaseAll();
    }

    function qwertyEnabled() {
        return live.qwerty;
    }

    function releaseAll() {
        for (const [, midi] of live.held) live.handler?.noteOff(midi);
        live.held.clear();
    }

    /** Keydown from the DAW. Returns true when the key played (or shifted) a note, so the DAW skips its shortcut. */
    function handleKeyDown(e) {
        if (!live.qwerty || e.ctrlKey || e.metaKey || e.altKey) return false;
        const k = e.key.toLowerCase();
        if (k === 'z' || k === 'x') {
            live.octave = Math.max(0, Math.min(8, live.octave + (k === 'z' ? -1 : 1)));
            e.preventDefault();
            return true;
        }
        if (!(k in QWERTY)) return false;
        e.preventDefault();
        if (e.repeat || live.held.has(k)) return true;
        const midi = (live.octave + 1) * 12 + QWERTY[k];
        live.held.set(k, midi);
        live.handler?.noteOn(midi, 100);
        return true;
    }

    function handleKeyUp(e) {
        const k = e.key.toLowerCase();
        if (!live.held.has(k)) return;
        const midi = live.held.get(k);
        live.held.delete(k);
        live.handler?.noteOff(midi);
    }

    /** Web MIDI needs a secure context (HTTPS or localhost) and browser support. */
    function midiPossible() {
        return !!(window.isSecureContext && navigator.requestMIDIAccess);
    }

    /** Connect every MIDI input (and any plugged in later) to the live handler. */
    async function connectMidi() {
        if (!midiPossible()) throw new Error(translate('MIDI needs HTTPS (or localhost) and a browser with Web MIDI'));
        if (!live.midiAccess) {
            live.midiAccess = await navigator.requestMIDIAccess();
            live.midiAccess.onstatechange = () => wireMidiInputs();
        }
        wireMidiInputs();
        return [...live.midiAccess.inputs.values()].map(i => i.name);
    }

    function wireMidiInputs() {
        for (const input of live.midiAccess.inputs.values()) {
            input.onmidimessage = (ev) => {
                const [status, note, velocity] = ev.data;
                const cmd = status & 0xf0;
                if (cmd === 0x90 && velocity > 0) live.handler?.noteOn(note, velocity);
                else if (cmd === 0x80 || (cmd === 0x90 && velocity === 0)) live.handler?.noteOff(note);
            };
        }
    }

    function midiInputNames() {
        return live.midiAccess ? [...live.midiAccess.inputs.values()].map(i => i.name) : [];
    }

    // ===== STANDARD MIDI FILES =====

    /** Parse a .mid file into tracks of notes in seconds (tempo map honored). */
    function parseMidiFile(arrayBuffer) {
        const data = new DataView(arrayBuffer);
        let pos = 0;
        const str = (n) => { let s = ''; for (let i = 0; i < n; i++) s += String.fromCharCode(data.getUint8(pos + i)); pos += n; return s; };
        const u32 = () => { const v = data.getUint32(pos); pos += 4; return v; };
        const u16 = () => { const v = data.getUint16(pos); pos += 2; return v; };
        if (str(4) !== 'MThd') throw new Error('Not a MIDI file');
        const headerLen = u32();
        u16(); // format
        const trackCount = u16();
        const division = u16();
        pos = 8 + headerLen;
        if (division & 0x8000) throw new Error('SMPTE-timed MIDI files are not supported');
        const events = []; // {tick, type, ...}
        for (let t = 0; t < trackCount && pos < data.byteLength; t++) {
            if (str(4) !== 'MTrk') break;
            const len = u32();
            const end = pos + len;
            let tick = 0;
            let running = 0;
            const vlq = () => { let v = 0, b; do { b = data.getUint8(pos++); v = (v << 7) | (b & 0x7f); } while (b & 0x80); return v; };
            while (pos < end) {
                tick += vlq();
                let status = data.getUint8(pos);
                if (status < 0x80) status = running;
                else pos++;
                if (status === 0xff) {
                    const type = data.getUint8(pos++);
                    const l = vlq();
                    if (type === 0x51 && l === 3) {
                        const us = (data.getUint8(pos) << 16) | (data.getUint8(pos + 1) << 8) | data.getUint8(pos + 2);
                        events.push({ tick, type: 'tempo', us });
                    }
                    pos += l;
                }
                else if (status === 0xf0 || status === 0xf7) {
                    pos += vlq();
                }
                else {
                    running = status;
                    const cmd = status & 0xf0;
                    const ch = status & 0x0f;
                    const d1 = data.getUint8(pos++);
                    const d2 = (cmd === 0xc0 || cmd === 0xd0) ? 0 : data.getUint8(pos++);
                    if (cmd === 0x90 || cmd === 0x80) events.push({ tick, type: cmd === 0x90 && d2 > 0 ? 'on' : 'off', pitch: d1, vel: d2, ch, track: t });
                }
            }
            pos = end;
        }
        events.sort((a, b) => a.tick - b.tick || (a.type === 'tempo' ? -1 : 0));
        // Ticks -> seconds through the tempo map
        let usPerBeat = 500000;
        let lastTick = 0;
        let lastSec = 0;
        let firstTempo = null;
        const open = new Map();
        const notes = [];
        for (const ev of events) {
            const sec = lastSec + (ev.tick - lastTick) / division * usPerBeat / 1e6;
            lastSec = sec;
            lastTick = ev.tick;
            if (ev.type === 'tempo') {
                usPerBeat = ev.us;
                if (firstTempo === null) firstTempo = ev.us;
                continue;
            }
            const k = `${ev.track}:${ev.ch}:${ev.pitch}`;
            if (ev.type === 'on') {
                if (!open.has(k)) open.set(k, []);
                open.get(k).push({ start: sec, vel: ev.vel });
            }
            else if (open.get(k)?.length) {
                const n = open.get(k).shift();
                notes.push({ pitch: ev.pitch, start: n.start, dur: Math.max(0.01, sec - n.start), vel: n.vel });
            }
        }
        notes.sort((a, b) => a.start - b.start);
        return { notes, bpm: firstTempo ? 60e6 / firstTempo : null };
    }

    /** Write notes (seconds) as a single-track SMF at the given tempo. */
    function writeMidiFile(notes, bpm = 120) {
        const ppq = 480;
        const secToTick = (s) => Math.round(s * bpm / 60 * ppq);
        const evs = [];
        for (const n of notes) {
            evs.push({ tick: secToTick(n.start), data: [0x90, n.pitch & 0x7f, Math.max(1, Math.min(127, Math.round(n.vel || 100)))] });
            evs.push({ tick: secToTick(n.start + n.dur), data: [0x80, n.pitch & 0x7f, 0] });
        }
        evs.sort((a, b) => a.tick - b.tick || (a.data[0] === 0x80 ? -1 : 1));
        const bytes = [];
        const vlq = (v) => { const out = [v & 0x7f]; while ((v >>= 7) > 0) out.unshift((v & 0x7f) | 0x80); bytes.push(...out); };
        const us = Math.round(60e6 / bpm);
        vlq(0);
        bytes.push(0xff, 0x51, 0x03, (us >> 16) & 0xff, (us >> 8) & 0xff, us & 0xff);
        let last = 0;
        for (const ev of evs) {
            vlq(ev.tick - last);
            last = ev.tick;
            bytes.push(...ev.data);
        }
        vlq(0);
        bytes.push(0xff, 0x2f, 0x00);
        const header = [0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, (ppq >> 8) & 0xff, ppq & 0xff];
        const trackHead = [0x4d, 0x54, 0x72, 0x6b, (bytes.length >>> 24) & 0xff, (bytes.length >>> 16) & 0xff, (bytes.length >>> 8) & 0xff, bytes.length & 0xff];
        return new Blob([new Uint8Array([...header, ...trackHead, ...bytes])], { type: 'audio/midi' });
    }

    // ===== MIDI CLIPS ON THE LANES =====

    const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

    function noteName(m) {
        return NOTE_NAMES[m % 12] + (Math.floor(m / 12) - 1);
    }

    /**
     * Draw a track's MIDI clips in its lane: a block per clip with its notes as a mini piano roll.
     * Drag the body to move (snapped), the right edge to change the length; click selects, double-click opens
     * the piano roll, right-click for the clip menu.
     * callbacks: { zoom, snapTime, selectedClipId, onSelect(clip), onOpen(clip), onContext(e, clip),
     *   onGestureStart(), onChanged() }
     */
    function renderMidiClips(track, lane, callbacks) {
        lane.querySelectorAll('.daw-midi-clip').forEach(el => el.remove());
        const zoom = callbacks.zoom;
        (track.midiClips || []).forEach((clip, idx) => {
            const el = createDiv(null, 'daw-clip daw-midi-clip' + (clip.id === callbacks.selectedClipId ? ' selected' : '') + (clip.muted ? ' clip-muted' : ''));
            el.dataset.clipId = clip.id;
            el.style.left = (clip.startTime * zoom) + 'px';
            el.style.width = Math.max(4, midiClipLength(clip) * zoom) + 'px';
            el.style.zIndex = String(2 + idx);
            el.style.backgroundColor = `color-mix(in srgb, ${clip.color || track.color} 28%, var(--background-soft))`;
            const label = createSpan(null, 'daw-clip-label');
            label.textContent = clip.name;
            el.appendChild(label);
            const notesBox = createDiv(null, 'daw-midi-notes');
            const pitches = clip.notes.map(n => n.pitch);
            const lo = Math.min(...pitches, 60) - 1;
            const hi = Math.max(...pitches, 72) + 1;
            for (const n of clip.notes) {
                if (n.start >= midiClipLength(clip)) continue;
                const b = createDiv(null, 'daw-midi-note-mini');
                b.style.left = (n.start * zoom) + 'px';
                b.style.width = Math.max(2, Math.min(n.dur, midiClipLength(clip) - n.start) * zoom - 1) + 'px';
                b.style.top = ((hi - n.pitch) / (hi - lo) * 100) + '%';
                b.style.background = track.color;
                notesBox.appendChild(b);
            }
            el.appendChild(notesBox);
            const trimR = createDiv(null, 'daw-clip-trim daw-clip-trim-right');
            trimR.title = translate('Drag to change the clip length');
            el.appendChild(trimR);
            el.addEventListener('click', (e) => {
                e.stopPropagation();
                callbacks.onSelect(clip);
            });
            el.addEventListener('dblclick', (e) => {
                e.stopPropagation();
                callbacks.onOpen(clip);
            });
            el.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                e.stopPropagation();
                callbacks.onContext(e, clip);
            });
            const drag = (target, mode) => target.addEventListener('pointerdown', (e) => {
                if (e.button !== 0 || callbacks.isRangeTool?.()) return;
                e.stopPropagation();
                target.setPointerCapture(e.pointerId);
                const x0 = e.clientX;
                const start0 = clip.startTime;
                const len0 = midiClipLength(clip);
                let moved = false;
                const onMove = (me) => {
                    if (!moved && Math.abs(me.clientX - x0) < 3) return;
                    if (!moved) { moved = true; callbacks.onGestureStart(); }
                    const d = (me.clientX - x0) / zoom;
                    if (mode === 'move') {
                        clip.startTime = Math.max(0, callbacks.snapTime(start0 + d));
                        el.style.left = (clip.startTime * zoom) + 'px';
                    }
                    else {
                        const end = callbacks.snapTime(start0 + len0 + d);
                        clip.length = Math.max(0.05, end - clip.startTime);
                        el.style.width = Math.max(4, clip.length * zoom) + 'px';
                    }
                };
                const onUp = (ue) => {
                    try { target.releasePointerCapture(ue.pointerId); } catch (_) {}
                    target.removeEventListener('pointermove', onMove);
                    target.removeEventListener('pointerup', onUp);
                    if (moved) callbacks.onChanged();
                };
                target.addEventListener('pointermove', onMove);
                target.addEventListener('pointerup', onUp);
            });
            drag(el, 'move');
            drag(trimR, 'length');
            lane.appendChild(el);
        });
    }

    // ===== PIANO ROLL =====

    /**
     * The piano roll for one MIDI clip: keys on the left, a time grid, notes as bars.
     * Click empty space to add a note one grid step long; drag a note to move it (time and pitch), its right
     * edge to resize; double-click or Delete removes; the velocity slider edits the selected notes.
     * callbacks: { gridStep(), snap(t), play(pitch, vel), onGestureStart(), onChanged(), bpm() }
     */
    function renderPianoRoll(container, clip, track, callbacks) {
        container.innerHTML = '';
        if (!clip) {
            const empty = createDiv(null, 'daw-stems-desc');
            empty.textContent = translate('Select a MIDI clip on an instrument track (double-click it) to edit its notes here. Record one with the keys, import a .mid file, or draw a new clip from the instrument track menu.');
            container.appendChild(empty);
            return;
        }
        const ROW = 12;
        const LOW = 24;   // C1
        const HIGH = 108; // C8
        const zoomX = callbacks.zoom || 120;
        const len = midiClipLength(clip);
        const selected = new Set();

        const bar = createDiv(null, 'daw-proll-toolbar');
        const title = createSpan(null, 'daw-fx-toolbar-title');
        title.textContent = `${clip.name} · ${track.name}`;
        bar.appendChild(title);
        const mkBtn = (text, tip, fn) => {
            const b = document.createElement('button');
            b.className = 'basic-button btn-sm';
            b.textContent = text;
            b.title = tip;
            b.addEventListener('click', fn);
            bar.appendChild(b);
            return b;
        };
        const commit = () => { callbacks.onChanged(); draw(); };
        mkBtn(translate('Quantize'), translate('Snap the selected notes (or all) to the grid'), () => {
            callbacks.onGestureStart();
            const g = callbacks.gridStep();
            for (const n of targetNotes()) {
                n.start = Math.max(0, Math.round(n.start / g) * g);
                n.dur = Math.max(g, Math.round(n.dur / g) * g);
            }
            commit();
        });
        for (const [label, d] of [['-12', -12], ['-1', -1], ['+1', 1], ['+12', 12]]) {
            mkBtn(label, translate('Transpose the selected notes (or all)'), () => {
                callbacks.onGestureStart();
                for (const n of targetNotes()) n.pitch = Math.max(0, Math.min(127, n.pitch + d));
                commit();
            });
        }
        const velLbl = createSpan(null, 'daw-fx-knob-label');
        velLbl.textContent = translate('Velocity');
        bar.appendChild(velLbl);
        const vel = document.createElement('input');
        vel.type = 'range';
        vel.min = '1';
        vel.max = '127';
        vel.value = '100';
        vel.className = 'daw-proll-vel';
        let velGesture = false;
        vel.addEventListener('input', () => {
            if (!velGesture) { velGesture = true; callbacks.onGestureStart(); }
            for (const n of targetNotes()) n.vel = parseInt(vel.value);
            draw();
        });
        vel.addEventListener('change', () => { velGesture = false; callbacks.onChanged(); });
        bar.appendChild(vel);
        mkBtn(translate('Delete'), translate('Delete the selected notes'), () => {
            if (!selected.size) return;
            callbacks.onGestureStart();
            clip.notes = clip.notes.filter(n => !selected.has(n.id));
            selected.clear();
            commit();
        });
        container.appendChild(bar);

        const targetNotes = () => selected.size ? clip.notes.filter(n => selected.has(n.id)) : clip.notes;

        const wrap = createDiv(null, 'daw-proll');
        const keys = createDiv(null, 'daw-proll-keys');
        const grid = createDiv(null, 'daw-proll-grid');
        wrap.appendChild(keys);
        wrap.appendChild(grid);
        container.appendChild(wrap);
        const height = (HIGH - LOW + 1) * ROW;
        keys.style.height = height + 'px';
        grid.style.height = height + 'px';
        const width = Math.max(len * zoomX + 80, 400);
        grid.style.width = width + 'px';
        for (let m = HIGH; m >= LOW; m--) {
            const k = createDiv(null, 'daw-proll-key' + ([1, 3, 6, 8, 10].includes(m % 12) ? ' black' : '') + (m % 12 === 0 ? ' c' : ''));
            k.style.height = ROW + 'px';
            k.textContent = m % 12 === 0 ? noteName(m) : '';
            k.addEventListener('pointerdown', (e) => { e.preventDefault(); callbacks.play(m, 100); });
            keys.appendChild(k);
        }
        // Rows and beat lines as backgrounds; the clip end as a line
        const g = callbacks.gridStep();
        const beat = 60 / callbacks.bpm();
        grid.style.backgroundImage = [
            `repeating-linear-gradient(90deg, color-mix(in srgb, var(--text-soft) 22%, transparent) 0 1px, transparent 1px ${beat * zoomX}px)`,
            g * zoomX >= 6 ? `repeating-linear-gradient(90deg, color-mix(in srgb, var(--text-soft) 9%, transparent) 0 1px, transparent 1px ${g * zoomX}px)` : '',
            `repeating-linear-gradient(0deg, transparent 0 ${ROW - 1}px, color-mix(in srgb, var(--text-soft) 10%, transparent) ${ROW - 1}px ${ROW}px)`
        ].filter(Boolean).join(', ');
        const endLine = createDiv(null, 'daw-proll-end');
        endLine.style.left = (len * zoomX) + 'px';
        grid.appendChild(endLine);

        const yOf = (m) => (HIGH - m) * ROW;
        const pitchAt = (y) => Math.max(LOW, Math.min(HIGH, HIGH - Math.floor(y / ROW)));
        let seq = clip.notes.reduce((n, x) => Math.max(n, x.id || 0), 0);

        const noteEls = new Map(); // note id -> element, rebuilt by draw()
        const place = (el, n) => {
            el.style.left = (n.start * zoomX) + 'px';
            el.style.top = yOf(n.pitch) + 'px';
            el.style.width = Math.max(4, n.dur * zoomX - 1) + 'px';
            el.classList.toggle('outside', n.start >= len);
        };
        const paintSelection = () => {
            for (const [id, el] of noteEls) el.classList.toggle('selected', selected.has(id));
        };

        function draw() {
            grid.querySelectorAll('.daw-proll-note').forEach(n => n.remove());
            noteEls.clear();
            for (const n of clip.notes) {
                const el = createDiv(null, 'daw-proll-note' + (selected.has(n.id) ? ' selected' : ''));
                place(el, n);
                el.style.height = (ROW - 1) + 'px';
                el.style.opacity = String(0.45 + 0.55 * (n.vel / 127));
                el.title = `${noteName(n.pitch)} · ${translate('velocity')} ${n.vel}`;
                const edge = createDiv(null, 'daw-proll-note-edge');
                el.appendChild(edge);
                grid.appendChild(el);
                noteEls.set(n.id, el);
                const startDrag = (e, mode) => {
                    if (e.button !== 0) return;
                    e.stopPropagation();
                    e.preventDefault();
                    if (!selected.has(n.id)) {
                        if (!e.shiftKey) selected.clear();
                        selected.add(n.id);
                        paintSelection();
                    }
                    vel.value = String(n.vel);
                    try { el.setPointerCapture(e.pointerId); } catch (_) {}
                    const x0 = e.clientX, y0 = e.clientY;
                    const moving = clip.notes.filter(x => selected.has(x.id)).map(x => ({ n: x, start: x.start, dur: x.dur, pitch: x.pitch }));
                    let moved = false;
                    // Notes move in place and redraw on release: rebuilding mid-gesture detaches the element holding the pointer
                    const onMove = (me) => {
                        const dx = (me.clientX - x0) / zoomX;
                        const dy = Math.round((y0 - me.clientY) / ROW);
                        if (!moved && Math.abs(me.clientX - x0) < 3 && Math.abs(me.clientY - y0) < 3) return;
                        if (!moved) { moved = true; callbacks.onGestureStart(); }
                        for (const m of moving) {
                            if (mode === 'move') {
                                m.n.start = Math.max(0, callbacks.snap(m.start + dx));
                                m.n.pitch = Math.max(LOW, Math.min(HIGH, m.pitch + dy));
                            }
                            else {
                                m.n.dur = Math.max(0.02, callbacks.snap(m.start + m.dur + dx) - m.start);
                            }
                            const mel = noteEls.get(m.n.id);
                            if (mel) place(mel, m.n);
                        }
                    };
                    const onUp = (ue) => {
                        try { el.releasePointerCapture(ue.pointerId); } catch (_) {}
                        window.removeEventListener('pointermove', onMove);
                        window.removeEventListener('pointerup', onUp);
                        window.removeEventListener('pointercancel', onUp);
                        if (moved) commit();
                    };
                    window.addEventListener('pointermove', onMove);
                    window.addEventListener('pointerup', onUp);
                    window.addEventListener('pointercancel', onUp);
                };
                el.addEventListener('pointerdown', (e) => startDrag(e, e.target === edge ? 'resize' : 'move'));
                el.addEventListener('dblclick', (e) => {
                    e.stopPropagation();
                    callbacks.onGestureStart();
                    clip.notes = clip.notes.filter(x => x.id !== n.id);
                    selected.delete(n.id);
                    commit();
                });
            }
        }

        grid.addEventListener('pointerdown', (e) => {
            if (e.button !== 0 || e.target !== grid) return;
            const rect = grid.getBoundingClientRect();
            const t = callbacks.snap((e.clientX - rect.left) / zoomX);
            const pitch = pitchAt(e.clientY - rect.top);
            if (t >= len) return;
            callbacks.onGestureStart();
            const note = { id: ++seq, pitch, start: Math.max(0, t), dur: Math.max(0.05, callbacks.gridStep()), vel: parseInt(vel.value) || 100 };
            clip.notes.push(note);
            selected.clear();
            selected.add(note.id);
            callbacks.play(pitch, note.vel);
            commit();
        });
        container.tabIndex = 0;
        container.onkeydown = (e) => {
            if ((e.key === 'Delete' || e.key === 'Backspace') && selected.size) {
                e.preventDefault();
                e.stopPropagation();
                callbacks.onGestureStart();
                clip.notes = clip.notes.filter(n => !selected.has(n.id));
                selected.clear();
                commit();
            }
        };
        draw();
        // Scroll the notes (or middle C) into view
        const focusPitch = clip.notes.length ? Math.max(...clip.notes.map(n => n.pitch)) + 4 : 72;
        wrap.scrollTop = Math.max(0, yOf(Math.min(HIGH, focusPitch)) - 20);
    }

    return {
        SYNTH_PRESETS, SYNTH_KNOBS, WAVES, createInstrument, startVoice, setSampleResolver, midiClipLength,
        notesInWindow, scheduleTrack, scheduleWindow, setLiveHandler, setQwerty, qwertyEnabled, releaseAll,
        handleKeyDown, handleKeyUp, midiPossible, connectMidi, midiInputNames, parseMidiFile, writeMidiFile,
        renderMidiClips, renderPianoRoll, noteName
    };
})();
