/**
 * AudioDawScore — the Score tab: view, validate and edit the ABC score YuE2 plans before it renders audio.
 *
 * YuE2 has no audio input; the score is the only editable artifact it exposes, so "edit this song" is always
 * edit-the-score-and-re-render. Engraving is abcjs; everything semantic (validation, edits, render) works on
 * the original text, because abcjs never modifies the ABC — it reports what was clicked and the app rewrites.
 *
 * Reuses SwarmUI utilities: createDiv(), createSpan(), doNoticePopover(). Driven from audio-daw.js via a
 * callbacks object (see scoreCallbacks there) — the DAW IIFE exports only { open, close }.
 */
const AudioDawScore = (() => {
    'use strict';

    // YuE2's dialect, as the checkpoint emits it. Checkpoint facts, not preferences:
    // two voices under exactly these ids, both covering the same bars.
    const VOICE_IDS = ['Vocal', 'Ins'];
    // Unit counts one notehead can draw (largest first). Anything else must be written as tied parts.
    const ENGRAVABLE = [48, 32, 24, 16, 12, 8, 6, 4, 3, 2, 1];

    // What SheetSage2 consumes. A 3-minute stereo 44.1 kHz WAV is ~42 MB base64 and over the request body
    // limit; mono at this rate is ~8.6 MB and is exactly what the model hears anyway.
    const TRANSCRIBE_RATE = 24000;

    // Scale degrees for numbered-melody entry, as semitones above the tonic.
    const MAJOR_STEPS = [0, 2, 4, 5, 7, 9, 11];
    const LETTERS = 'CDEFGAB';

    let cb = null;          // callbacks from audio-daw.js
    let els = {};           // cached DOM
    let current = null;     // { abc, meta } — meta is the clip.meta.score record this came from, when any
    let prep = null;        // { text, reps } from the last engraving pass
    let selectedClip = null;
    let suppressSrcSync = false;
    let selection = null;   // { start, end, voice, chord, token } in ORIGINAL coordinates
    const history = { undo: [], redo: [] };
    let visual = null;      // the rendered tune; carries noteTimings for highlighting
    let synthCtl = null;
    // The plan's own timings. A warped audition replaces visual.noteTimings with tempo-scaled ones, and the
    // DAW playhead follows the plan rather than the audition.
    let planTimings = null;
    let highlighted = [], lastSystemTop = null, lastTimingIndex = -1;
    // null = abcjs's own remote host. Set once the local samples are all present.
    let soundFontUrl = null;
    let dragSection = null; // index of the section chip being dragged

    // Tagging a chip drag keeps it and the sheet's own file drop from answering each other.
    const SECTION_MIME = 'application/x-audiolab-section';
    const SCORE_FILE = /\.(abc|txt)$/i;
    const AUDIO_FILE = /\.(wav|mp3|flac|ogg|m4a)$/i;

    // ===== ABC reading =====

    /** Header fields up to and including K:, which ends the header. Body V: lines are not header voices. */
    function parseHeader(abc) {
        const h = { X: null, T: null, M: null, L: null, Q: null, K: null, voices: [] };
        for (const raw of abc.split('\n')) {
            const line = raw.trim();
            const m = /^([A-Za-z]):\s*(.*)$/.exec(line);
            if (!m) continue;
            const [, field, val] = m;
            if (field === 'V') { h.voices.push(val.trim().split(/\s+/)[0]); continue; }
            if (field in h) h[field] = val.trim();
            if (field === 'K') break;
        }
        return h;
    }

    function headerEndLine(abc) {
        const lines = abc.split('\n');
        for (let i = 0; i < lines.length; i++) if (/^K:/.test(lines[i].trim())) return i;
        return -1;
    }

    /** Replace a header field in place, or insert it just above K: when absent. */
    function setHeaderField(abc, field, value) {
        const lines = abc.split('\n');
        const kAt = headerEndLine(abc);
        for (let i = 0; i <= (kAt < 0 ? lines.length - 1 : kAt); i++) {
            if (new RegExp(`^${field}:`).test(lines[i].trim())) {
                lines[i] = `${field}:${value}`;
                return lines.join('\n');
            }
        }
        if (kAt >= 0) lines.splice(kAt, 0, `${field}:${value}`);
        return lines.join('\n');
    }

    function meterFraction(m) {
        const p = /^(\d+)\s*\/\s*(\d+)$/.exec(m || '');
        return p ? Number(p[1]) / Number(p[2]) : null;
    }

    /** Tempo from Q:1/4=93 (or a bare Q:93). */
    function parseTempo(q) {
        if (!q) return null;
        const m = /=\s*(\d+(?:\.\d+)?)/.exec(q) || /^(\d+(?:\.\d+)?)$/.exec(q.trim());
        return m ? Number(m[1]) : null;
    }

    /**
     * Bars on one music line. Barline runs count as one; Zn is n bars but closes with a single barline, so it
     * contributes n-1 extra. A leading barline opens rather than closes a bar.
     */
    function countBars(line) {
        const s = line
            .replace(/"[^"]*"/g, '')
            .replace(/!.*?!/g, '')
            .replace(/\{[^}]*\}/g, '')
            .replace(/\[[A-Za-z]:[^\]]*\]/g, '')
            .replace(/\[([^\]]*)\]/g, '$1');
        let bars = 0;
        for (const m of s.matchAll(/Z(\d*)/g)) bars += (m[1] ? parseInt(m[1], 10) : 1) - 1;
        for (const m of s.matchAll(/[|:]*\|[|:]*/g)) { if (m.index !== 0) bars++; }
        return bars;
    }

    /** Ordered {voice, bars, line} blocks plus the % section labels between them. */
    function scanBody(abc) {
        const lines = abc.split('\n');
        const blocks = [], sections = [];
        let inHeader = true, voice = null, bars = 0, startLine = -1;
        const flush = () => { if (voice !== null) blocks.push({ voice, bars, line: startLine }); voice = null; bars = 0; };
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i].trim();
            if (inHeader) { if (/^K:/.test(line)) inHeader = false; continue; }
            if (!line) continue;
            if (line.startsWith('%')) {
                sections.push({ label: line.replace(/^%+\s*/, '').trim(), line: i, blockIndex: blocks.length });
                continue;
            }
            const v = /^V:\s*(\S+)/.exec(line);
            if (v) { flush(); voice = v[1]; startLine = i; continue; }
            if (/^[A-Za-z]:/.test(line)) continue;
            bars += countBars(line);
        }
        flush();
        return { blocks, sections };
    }

    /** A chunk is the parallel run of voice blocks covering one span of bars; it ends when a voice repeats. */
    function chunkBlocks(blocks) {
        const chunks = [];
        let cur = [], seen = new Set();
        for (const b of blocks) {
            if (seen.has(b.voice)) { chunks.push(cur); cur = []; seen = new Set(); }
            cur.push(b); seen.add(b.voice);
        }
        if (cur.length) chunks.push(cur);
        return chunks;
    }

    /** Blocks as scanBody reads them, carrying the source lines their music sits on. */
    function voiceLineBlocks(abc) {
        const lines = abc.split('\n');
        const blocks = [];
        let inHeader = true, cur = null;
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i].trim();
            if (inHeader) { if (/^K:/.test(line)) inHeader = false; continue; }
            if (!line || line.startsWith('%')) continue;
            const v = /^V:\s*(\S+)/.exec(line);
            if (v) { cur = { voice: v[1], lines: [] }; blocks.push(cur); continue; }
            if (/^[A-Za-z]:/.test(line) || !cur) continue;
            cur.lines.push(i);
        }
        return blocks;
    }

    /** What countBars masks out, but length-preserving, so a barline's offset in the raw line stays valid. */
    function maskLine(line) {
        return line.replace(/"[^"]*"|![^!]*!|\{[^}]*\}|\[[A-Za-z]:[^\]]*\]/g, m => 'x'.repeat(m.length));
    }

    /** A line as its bars plus the barline closing each. A leading barline opens a bar, so it stays in the body. */
    function barSegments(line) {
        const masked = maskLine(line);
        const segs = [];
        let at = 0;
        for (const m of masked.matchAll(/[|:]*\|[|:]*/g)) {
            if (m.index === 0) continue;
            segs.push({ body: line.slice(at, m.index), bar: line.slice(m.index, m.index + m[0].length) });
            at = m.index + m[0].length;
        }
        return { segs, tail: line.slice(at) };
    }

    /**
     * Pad the source so a chunk's voices show the same bar in the same column, which makes a voice drifting
     * out of step visible before the validator says so. Spaces immediately before a barline can change neither
     * a duration nor a beam — the barline already breaks both — so the score still says exactly what it did.
     */
    function alignBars(abc) {
        const lines = abc.split('\n');
        for (const chunk of chunkBlocks(voiceLineBlocks(abc))) {
            if (chunk.length < 2) continue;
            const depth = Math.min(...chunk.map(b => b.lines.length));
            for (let j = 0; j < depth; j++) {
                const cut = chunk.map(b => barSegments(lines[b.lines[j]]));
                const count = cut[0].segs.length;
                // Lines the two voices do not divide into the same bars have no columns to share.
                if (!count || cut.some(c => c.segs.length !== count)) continue;
                for (const c of cut) for (const seg of c.segs) seg.body = seg.body.replace(/\s+$/, '');
                for (let k = 0; k < count; k++) {
                    const width = Math.max(...cut.map(c => c.segs[k].body.length));
                    for (const c of cut) c.segs[k].body = c.segs[k].body.padEnd(width);
                }
                chunk.forEach((b, v) => {
                    lines[b.lines[j]] = cut[v].segs.map(seg => seg.body + seg.bar).join('') + cut[v].tail;
                });
            }
        }
        return lines.join('\n');
    }

    /** Body character ranges holding music tokens — outside the header, comments, fields and quoted spans. */
    function safeRegions(abc) {
        const out = [];
        let pos = 0, inHeader = true;
        for (const line of abc.split('\n')) {
            const t = line.trim();
            if (inHeader) { if (/^K:/.test(t)) inHeader = false; pos += line.length + 1; continue; }
            if (t && !t.startsWith('%') && !/^[A-Za-z]:/.test(t)) {
                let i = 0, seg = 0;
                while (i < line.length) {
                    if (line[i] === '"') {
                        if (i > seg) out.push([pos + seg, pos + i]);
                        const j = line.indexOf('"', i + 1);
                        i = j < 0 ? line.length : j + 1; seg = i; continue;
                    }
                    if (line[i] === '[' && /^\[[A-Za-z]:/.test(line.slice(i, i + 3))) {
                        if (i > seg) out.push([pos + seg, pos + i]);
                        const j = line.indexOf(']', i);
                        i = j < 0 ? line.length : j + 1; seg = i; continue;
                    }
                    i++;
                }
                if (i > seg) out.push([pos + seg, pos + i]);
            }
            pos += line.length + 1;
        }
        return out;
    }

    /** A quoted span in the body is a chord symbol unless it opens with an annotation marker. */
    function chordSpans(abc) {
        const out = [];
        let pos = 0, inHeader = true;
        for (const line of abc.split('\n')) {
            const t = line.trim();
            if (inHeader) { if (/^K:/.test(t)) inHeader = false; pos += line.length + 1; continue; }
            if (t && !t.startsWith('%') && !/^[A-Za-z]:/.test(t)) {
                for (const m of line.matchAll(/"([^"]*)"/g)) {
                    if (m[1] && !/^[\^_<>@]/.test(m[1])) out.push({ start: pos + m.index, len: m[0].length, name: m[1] });
                }
            }
            pos += line.length + 1;
        }
        return out;
    }

    function hasChords(abc) { return chordSpans(abc).length > 0; }

    function stripChords(abc) {
        const spans = chordSpans(abc);
        let out = abc;
        for (let i = spans.length - 1; i >= 0; i--) {
            out = out.slice(0, spans[i].start) + out.slice(spans[i].start + spans[i].len);
        }
        return out;
    }

    /** The planning mode a score implies. Mismatching it is an out-of-distribution prompt, not a preference. */
    function modeForScore(abc) { return hasChords(abc) ? 'full' : 'melody'; }

    // ===== note tokens =====

    // accidental, letter, octave marks, duration, optional /n, tie
    const NOTE_TOKEN = /^([\^=_]{0,2})([A-Ga-gxzZ])([,']*)(\d*)(\/\d*)?(-?)/;

    /**
     * Split an element span into its chord symbol (abcjs includes it) and the note token that follows.
     * @returns {{chord: ?{start,end,name}, token: ?Object}} token carries absolute start/end plus its parts
     */
    function splitElement(abc, start, end) {
        let i = start, chord = null;
        while (i < end && abc[i] === '"') {
            const j = abc.indexOf('"', i + 1);
            if (j < 0 || j >= end) break;
            chord = { start: i, end: j + 1, name: abc.slice(i + 1, j) };
            i = j + 1;
        }
        const m = NOTE_TOKEN.exec(abc.slice(i, end));
        const token = m ? {
            start: i, end: i + m[0].length,
            accidental: m[1], letter: m[2], marks: m[3],
            count: m[4] ? parseInt(m[4], 10) : 1, hasCount: !!m[4],
            fraction: m[5] || '', tie: m[6] || '',
            isRest: /[xzZ]/.test(m[2])
        } : null;
        return { chord, token };
    }

    function tokenText(t, over = {}) {
        const p = { ...t, ...over };
        const count = p.count === 1 && !p.forceCount ? '' : String(p.count);
        return `${p.accidental}${p.letter}${p.marks}${count}${p.fraction}${p.tie}`;
    }

    /** Absolute diatonic index: C=0..B=6, lowercase adds 7, each mark shifts an octave. */
    function pitchIndex(letter, marks) {
        const upper = letter.toUpperCase();
        let n = LETTERS.indexOf(upper);
        if (n < 0) return null;
        if (letter !== upper) n += 7;
        for (const m of marks) n += m === "'" ? 7 : -7;
        return n;
    }

    function pitchToken(n) {
        let oct = Math.floor(n / 7);
        const letterIdx = ((n % 7) + 7) % 7;
        let letter = LETTERS[letterIdx];
        if (oct >= 1) { letter = letter.toLowerCase(); oct -= 1; }
        return { letter, marks: oct > 0 ? "'".repeat(oct) : ','.repeat(-oct) };
    }

    /** Move a note by whole staff steps. The key signature supplies the accidental, so an explicit one is
     *  dropped — it described the old pitch. */
    function transposeToken(t, steps) {
        const n = pitchIndex(t.letter, t.marks);
        if (n === null) return null;
        const moved = pitchToken(n + steps);
        return tokenText(t, { ...moved, accidental: '' });
    }

    // ===== edit primitives =====

    function replaceRange(abc, start, end, text) { return abc.slice(0, start) + text + abc.slice(end); }

    function pushHistory() {
        if (!current) return;
        history.undo.push(current.abc);
        if (history.undo.length > 100) history.undo.shift();
        history.redo.length = 0;
    }

    function undo() {
        if (!history.undo.length) return;
        history.redo.push(current.abc);
        current = { abc: history.undo.pop(), meta: current?.meta || null };
        selection = null;
        refresh();
    }

    function redo() {
        if (!history.redo.length) return;
        history.undo.push(current.abc);
        current = { abc: history.redo.pop(), meta: current?.meta || null };
        selection = null;
        refresh();
    }

    /** Apply one edit: snapshot, swap the text, revalidate, re-engrave. */
    function edit(newAbc) {
        if (typeof newAbc !== 'string' || newAbc === current?.abc) return;
        pushHistory();
        current = { abc: newAbc, meta: current?.meta || null };
        refresh();
    }

    // ===== engraving prep =====

    function decompose(units) {
        const parts = [];
        let left = units;
        while (left > 0) {
            const pick = ENGRAVABLE.find(u => u <= left);
            if (!pick) break;
            parts.push(pick); left -= pick;
        }
        return parts;
    }

    /**
     * Rewrite the score into something abcjs can engrave, recording every substitution so score coordinates
     * can be mapped back. Two fixes are needed:
     *   Zn  — abcjs counts a compressed multi-bar rest as ONE bar when wrapping, which desyncs the two staves.
     *   X10 — a 5/16 note needs two tied glyphs; any count outside ENGRAVABLE has the same problem.
     * The spelling change is for engraving only; the plan and its audio timeline keep the original.
     */
    function prepareForEngraving(abc) {
        const reps = [];
        for (const [from, to] of safeRegions(abc)) {
            const seg = abc.slice(from, to);
            for (const m of seg.matchAll(/Z(\d+)/g)) {
                const n = parseInt(m[1], 10);
                if (n > 1) reps.push({ start: from + m.index, oldLen: m[0].length, text: Array(n).fill('Z').join('|') });
            }
            for (const m of seg.matchAll(/([\^=_]{0,2}[A-Ga-gxz][,']*)(\d+)/g)) {
                const units = parseInt(m[2], 10);
                if (ENGRAVABLE.includes(units)) continue;
                const parts = decompose(units);
                if (parts.length < 2) continue;
                const isRest = /[xz]/.test(m[1]);
                reps.push({
                    start: from + m.index,
                    oldLen: m[0].length,
                    // Rests are never tied — consecutive rests say the same thing and stay legal ABC.
                    text: parts.map(p => `${m[1]}${p}`).join(isRest ? '' : '-')
                });
            }
        }
        reps.sort((a, b) => a.start - b.start);
        let text = abc;
        for (let i = reps.length - 1; i >= 0; i--) {
            text = text.slice(0, reps[i].start) + reps[i].text + text.slice(reps[i].start + reps[i].oldLen);
        }
        return { text, reps };
    }

    /** Map an offset in the engraved text back to the original. Every expansion shifts everything after it. */
    function toOriginal(reps, offset) {
        let delta = 0;
        for (const r of reps || []) {
            const start = r.start + delta;
            if (offset < start) break;
            if (offset < start + r.text.length) return r.start;
            delta += r.text.length - r.oldLen;
        }
        return offset - delta;
    }

    // ===== validation =====

    /**
     * Check the score against YuE2's dialect. Structural problems are errors and block rendering; bar sums are
     * warnings because the arithmetic has to survive ties, broken rhythm and tuplets before it can gate a render.
     */
    function validate(abc) {
        const out = [];
        const err = (message, line) => out.push({ severity: 'error', message, line });
        const warn = (message, line) => out.push({ severity: 'warning', message, line });
        if (!abc || !abc.trim()) { err('The score is empty.', 0); return out; }

        const h = parseHeader(abc);
        if (!h.M) err('The header has no M: (meter).', 0);
        if (!h.L) err('The header has no L: (unit note length).', 0);
        if (!h.K) err('The header has no K: (key).', 0);
        for (const id of VOICE_IDS) {
            if (!h.voices.includes(id)) err(`The header does not declare "V: ${id}". YuE2 reads exactly two voices, ${VOICE_IDS.join(' and ')}.`, 0);
        }
        for (const id of h.voices) {
            if (!VOICE_IDS.includes(id)) err(`Unknown voice "${id}" — YuE2 only understands ${VOICE_IDS.join(' and ')}.`, 0);
        }

        const { blocks } = scanBody(abc);
        if (!blocks.length) { err('There is no music after the header.', 0); return out; }
        for (const chunk of chunkBlocks(blocks)) {
            if (chunk.length < 2) {
                warn(`Only ${chunk[0].voice} is written here — both voices have to cover every bar (pad the other with Z).`, chunk[0].line);
                continue;
            }
            const counts = chunk.map(b => b.bars);
            if (counts.some(c => c !== counts[0])) {
                err(`The voices disagree on bar count here (${chunk.map(b => `${b.voice} ${b.bars}`).join(', ')}). Both must cover the same bars.`, chunk[0].line);
            }
        }
        checkInlineKeys(abc, out);
        checkBarDurations(abc, h, warn, err);
        return out;
    }

    /** Inline key changes have to land at the same bar in both voices, or the voices drift apart harmonically. */
    function checkInlineKeys(abc, out) {
        const perVoice = new Map();
        const lines = abc.split('\n');
        let inHeader = true, voice = null, barAt = 0;
        for (const raw of lines) {
            const line = raw.trim();
            if (inHeader) { if (/^K:/.test(line)) inHeader = false; continue; }
            if (!line || line.startsWith('%')) continue;
            const v = /^V:\s*(\S+)/.exec(line);
            if (v) { voice = v[1]; barAt = perVoice.get(voice)?.bars || 0; continue; }
            if (/^[A-Za-z]:/.test(line) || !voice) continue;
            const rec = perVoice.get(voice) || { bars: 0, keys: [] };
            let consumed = 0;
            for (const m of line.matchAll(/\[K:[^\]]*\]|[|:]*\|[|:]*/g)) {
                if (m[0].startsWith('[K:')) rec.keys.push(rec.bars + consumed);
                else if (m.index !== 0) consumed++;
            }
            rec.bars += countBars(line);
            perVoice.set(voice, rec);
        }
        const [a, b] = VOICE_IDS.map(id => perVoice.get(id)?.keys || []);
        if (a.length !== b.length || a.some((v, i) => v !== b[i])) {
            out.push({
                severity: 'warning',
                message: 'Inline key changes ([K:…]) are not at the same bars in both voices.',
                line: 0
            });
        }
    }

    /** Bar sums via abcjs's parsed durations — it already understands ties, dots and broken rhythm. */
    function checkBarDurations(abc, h, warn, err) {
        if (typeof ABCJS === 'undefined') return;
        let tune = null;
        try { tune = ABCJS.parseOnly(abc)[0]; }
        catch (e) { err(`The notation library could not parse this score: ${e.message}`, 0); return; }
        if (!tune) { err('The notation library produced no tune from this score.', 0); return; }
        for (const w of tune.warnings || []) warn(String(w).replace(/<[^>]*>/g, ''), 0);

        const barWhole = meterFraction(h.M);
        if (!barWhole) return;
        for (const line of tune.lines || []) {
            for (const staff of line.staff || []) {
                for (const voice of staff.voices || []) {
                    let sum = 0, skip = false;
                    for (const el of voice) {
                        if (el.el_type === 'bar') {
                            if (!skip && sum > 0 && Math.abs(sum - barWhole) > 1e-6) {
                                warn(`A bar holds ${(sum / barWhole * 100).toFixed(0)}% of ${h.M}.`, 0);
                            }
                            sum = 0; skip = false; continue;
                        }
                        if (el.rest && el.rest.type === 'multimeasure') { skip = true; continue; }
                        if (typeof el.duration === 'number') sum += el.duration;
                    }
                }
            }
        }
    }

    // ===== UI =====

    function render(container, callbacks) {
        cb = callbacks || {};
        els = {};
        container.innerHTML = '';

        const split = createDiv(null, 'daw-panel-split');
        const left = createDiv(null, 'daw-panel-col');
        const right = createDiv(null, 'daw-panel-col');
        split.appendChild(left);
        split.appendChild(right);
        container.appendChild(split);

        buildSheetCard(left);
        buildControls(right);
        loadScore('', null);
        return true;
    }

    function buildSheetCard(parent) {
        const card = createDiv(null, 'daw-fx-card');
        const head = createDiv(null, 'daw-fx-card-head');
        const title = createSpan(null, 'daw-fx-card-title');
        title.textContent = 'Score';
        head.appendChild(title);
        const btns = createDiv(null, 'daw-fx-card-btns');
        els.play = miniButton('Play', 'Hear the plan in the browser before spending a render on it', playPause);
        const chordsLabel = document.createElement('label');
        chordsLabel.className = 'daw-score-toggle';
        chordsLabel.title = 'Play the chord symbols as accompaniment';
        chordsLabel.htmlFor = 'daw_score_chords';
        els.chords = document.createElement('input');
        els.chords.type = 'checkbox';
        els.chords.id = 'daw_score_chords';
        els.chords.checked = true;
        els.chords.addEventListener('change', setTransportTune);
        chordsLabel.appendChild(els.chords);
        chordsLabel.appendChild(document.createTextNode(' Chords'));
        btns.appendChild(els.play);
        btns.appendChild(chordsLabel);
        els.undo = miniButton('Undo', 'Undo the last score edit (Ctrl+Z while the score has focus)', undo);
        els.redo = miniButton('Redo', 'Redo (Ctrl+Y)', redo);
        btns.appendChild(els.undo);
        btns.appendChild(els.redo);
        btns.appendChild(miniButton('No chords', 'Strip every chord symbol — the melody-only form used for covers', stripAllChords));
        btns.appendChild(miniButton('Align bars', 'Pad the source so both voices\u2019 barlines line up column-wise', applyAlignBars));
        btns.appendChild(miniButton('Copy', 'Copy the ABC score to the clipboard', () => {
            if (!current?.abc) return;
            navigator.clipboard?.writeText(current.abc)
                .then(() => notice('Score copied', 'green'))
                .catch(() => notice('Could not copy to the clipboard', 'yellow'));
        }));
        btns.appendChild(miniButton('Save', 'Download the ABC score as a .abc file', downloadScore));
        btns.appendChild(miniButton('MIDI', 'Download the plan as a MIDI file', exportMidi));
        head.appendChild(btns);
        card.appendChild(head);

        els.sheet = createDiv(null, 'daw-score-sheet');
        els.sheet.tabIndex = 0;   // so the staff can take keyboard focus without stealing the DAW's shortcuts
        els.sheet.addEventListener('keydown', onSheetKey);
        els.sheet.addEventListener('dblclick', playFromNote);
        setupSheetDrop(els.sheet);
        card.appendChild(els.sheet);
        els.transport = createDiv(null, 'daw-score-transport');
        card.appendChild(els.transport);
        parent.appendChild(card);
    }

    /** Scoped to the staff: the DAW's own shortcuts keep working everywhere else. */
    function onSheetKey(e) {
        const key = e.key;
        if ((e.ctrlKey || e.metaKey) && key.toLowerCase() === 'z') { e.preventDefault(); e.shiftKey ? redo() : undo(); return; }
        if ((e.ctrlKey || e.metaKey) && key.toLowerCase() === 'y') { e.preventDefault(); redo(); return; }
        if (!selection?.token) return;
        if (key === 'ArrowUp') { e.preventDefault(); applyTranspose(e.shiftKey ? 7 : 1); }
        else if (key === 'ArrowDown') { e.preventDefault(); applyTranspose(e.shiftKey ? -7 : -1); }
        else if (key === 'Delete' || key === 'Backspace') { e.preventDefault(); setRest(true); }
    }

    function buildControls(parent) {
        const header = createDiv(null, 'daw-stems-header');
        header.textContent = 'Plan';
        parent.appendChild(header);

        els.clipInfo = createDiv(null, 'daw-stems-clipinfo');
        parent.appendChild(els.clipInfo);

        // Header fields — each edit rewrites the ABC header, so the text stays the single source of truth.
        const hdrRow = createDiv(null, 'daw-stems-action-row');
        els.key = labelledInput(hdrRow, 'Key', 'daw-generate-reftext', v => applyEdit(setHeaderField(current.abc, 'K', v)), 'daw_score_key');
        els.meter = labelledInput(hdrRow, 'Meter', 'daw-generate-reftext', v => applyEdit(setHeaderField(current.abc, 'M', v)), 'daw_score_meter');
        els.tempo = labelledInput(hdrRow, 'Tempo', 'daw-generate-reftext', v => {
            const bpm = parseFloat(v);
            if (!isFinite(bpm) || bpm <= 0) { syncControls(); return; }
            applyEdit(setHeaderField(current.abc, 'Q', `1/4=${Math.round(bpm)}`));
        }, 'daw_score_tempo');
        els.unit = labelledInput(hdrRow, 'Unit', 'daw-generate-reftext', null, 'daw_score_unit');
        els.unit.readOnly = true;
        els.unit.title = 'Unit note length (L:). The grid every duration is measured in.';
        button(hdrRow, 'Apply to project', 'basic-button btn-sm', applyToProject)
            .title = 'Set the project tempo and time signature to what this score is written in';
        parent.appendChild(hdrRow);

        els.sections = createDiv(null, 'daw-fx-browser');
        parent.appendChild(els.sections);

        parent.appendChild(fieldLabel('Style', 'daw_score_style_label'));
        els.style = labelField(document.createElement('textarea'), 'daw_score_style_label');
        els.style.className = 'daw-generate-text';
        els.style.rows = 2;
        els.style.placeholder = 'Genre, instruments, mood — what the recording should sound like.';
        parent.appendChild(els.style);

        parent.appendChild(fieldLabel('Lyrics', 'daw_score_lyrics_label'));
        els.lyrics = labelField(document.createElement('textarea'), 'daw_score_lyrics_label');
        els.lyrics.className = 'daw-generate-text';
        els.lyrics.rows = 3;
        els.lyrics.placeholder = '[Verse]\nThe words to sing. Leave empty for an instrumental.';
        parent.appendChild(els.lyrics);

        // Numbered notation: how the YuE2 researchers dictated a melody correction ("1155665 / 4433221").
        parent.appendChild(fieldLabel('Melody by degree', 'daw_score_degree_label'));
        const numRow = createDiv(null, 'daw-stems-action-row');
        els.numbers = labelField(document.createElement('input'), 'daw_score_degree_label');
        els.numbers.type = 'text';
        els.numbers.className = 'daw-generate-reftext';
        els.numbers.placeholder = '1155665 / 4433221';
        els.numbers.title = 'Scale degrees in the current key. Spaces or / start a new bar, 0 is a rest.';
        numRow.appendChild(els.numbers);
        els.numberOctave = document.createElement('select');
        els.numberOctave.className = 'daw-fx-select';
        els.numberOctave.setAttribute('aria-label', 'Octave the degrees are written in');
        for (const [v, l] of [['0', 'Low'], ['1', 'Mid'], ['2', 'High']]) {
            const o = document.createElement('option');
            o.value = v; o.textContent = l;
            els.numberOctave.appendChild(o);
        }
        els.numberOctave.value = '1';
        numRow.appendChild(els.numberOctave);
        button(numRow, 'Write bars', 'basic-button btn-sm', insertNumbered);
        parent.appendChild(numRow);

        parent.appendChild(fieldLabel('ABC score', 'daw_score_src_label'));
        els.src = labelField(document.createElement('textarea'), 'daw_score_src_label');
        els.src.className = 'daw-generate-text daw-score-src';
        els.src.rows = 8;
        els.src.spellcheck = false;
        els.src.placeholder = 'Paste an ABC score here';
        // Typing is one history entry per editing session, not per keystroke — the textarea keeps its own
        // native undo for character-level work.
        let typingSnapshot = false;
        els.src.addEventListener('focus', () => { typingSnapshot = false; });
        els.src.addEventListener('input', () => {
            if (suppressSrcSync) return;
            if (!typingSnapshot) { pushHistory(); typingSnapshot = true; }
            current = { abc: els.src.value, meta: current?.meta || null };
            selection = null;
            scheduleRefresh();
        });
        parent.appendChild(els.src);

        els.issues = createDiv(null, 'daw-score-issues');
        els.issues.setAttribute('role', 'status');
        els.issues.setAttribute('aria-live', 'polite');
        parent.appendChild(els.issues);

        const actions = createDiv(null, 'daw-stems-action-row');
        els.load = button(actions, 'Load from clip', 'basic-button btn-sm', loadFromSelectedClip);
        els.draft = button(actions, 'Draft plan', 'basic-button btn-sm', draftPlan);
        els.draft.title = 'Ask the model for a score without rendering audio — seconds instead of minutes';
        const durWrap = createDiv(null, 'daw-score-field');
        const durLbl = createSpan(null, 'daw-stems-ctl-label');
        durLbl.textContent = 'Secs';
        els.duration = document.createElement('input');
        els.duration.type = 'number';
        els.duration.className = 'daw-generate-reftext';
        els.duration.value = '30';
        els.duration.min = '5';
        els.duration.max = '900';
        // The budget is computed against this, so a blank one would report the model's 6-minute default and
        // tell every short draft it has room to spare.
        els.duration.title = 'How long the song should be. The audio budget is measured against it.';
        durWrap.appendChild(durLbl);
        durWrap.appendChild(els.duration);
        actions.appendChild(durWrap);
        button(actions, 'Paste', 'basic-button btn-sm', async () => {
            try { applyEdit(await navigator.clipboard.readText()); }
            catch (_) { notice('Could not read the clipboard — paste into the ABC box instead', 'yellow'); }
        });
        els.go = button(actions, 'Render', 'basic-button btn-sm btn-primary daw-stems-go', renderScore);
        parent.appendChild(actions);

        els.modeNote = createDiv(null, 'daw-stems-desc');
        parent.appendChild(els.modeNote);

        els.budget = createDiv(null, 'daw-stems-clipinfo daw-score-budget');
        parent.appendChild(els.budget);

        buildTranscribeCard(parent);
        buildSoundfontRow(parent);
        buildVariantsCard(parent);
        buildLlmCard(parent);
        buildVersionsCard(parent);

        const help = createDiv(null, 'daw-stems-desc');
        help.textContent = 'Click a chord symbol to reharmonise, a note to edit it, or drag a note up and down '
            + 'to change its pitch. Play auditions the plan in the browser. The score is a plan the model '
            + 'performs, not a recording of it.';
        parent.appendChild(help);
    }

    function fieldLabel(text, id) {
        const l = createDiv(id || null, 'daw-stems-ctl-label');
        l.textContent = text;
        return l;
    }

    /** Name a field by the label div above it, without moving either in the DOM. */
    function labelField(field, labelId) {
        field.setAttribute('aria-labelledby', labelId);
        return field;
    }

    function labelledInput(row, label, cls, onCommit, id) {
        const wrap = createDiv(null, 'daw-score-field');
        const l = document.createElement('label');
        l.className = 'daw-stems-ctl-label';
        l.textContent = label;
        const input = document.createElement('input');
        input.type = 'text';
        input.className = cls;
        if (id) { input.id = id; l.htmlFor = id; }
        if (onCommit) {
            input.addEventListener('change', () => { if (current?.abc) onCommit(input.value.trim()); });
        }
        wrap.appendChild(l);
        wrap.appendChild(input);
        row.appendChild(wrap);
        return input;
    }

    function button(parent, text, cls, onClick) {
        const b = document.createElement('button');
        b.className = cls;
        b.textContent = text;
        b.addEventListener('click', onClick);
        parent.appendChild(b);
        return b;
    }

    function miniButton(text, title, onClick) {
        const b = document.createElement('button');
        b.className = 'daw-fx-mini-btn';
        b.textContent = text;
        b.title = title;
        // Starts with the visible text, so speaking the label still matches what a voice command would say.
        b.setAttribute('aria-label', `${text} — ${title}`);
        b.addEventListener('click', onClick);
        return b;
    }

    function notice(msg, kind) {
        if (typeof doNoticePopover === 'function') doNoticePopover(msg, `notice-pop-${kind}`);
    }


    // ===== state =====

    function loadScore(abc, meta) {
        history.undo.length = 0;
        history.redo.length = 0;
        selection = null;
        current = { abc: abc || '', meta: meta || null };
        if (meta) {
            if (typeof meta.style === 'string') els.style.value = meta.style;
            if (typeof meta.lyrics === 'string') els.lyrics.value = meta.lyrics;
            showBudget(meta.budgetSeconds ? { budget_seconds: meta.budgetSeconds } : null);
        }
        else showBudget(null);
        showTranscriptInfo(meta);
        refresh();
    }

    let refreshTimer = null;
    function scheduleRefresh() {
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(refresh, 250);
    }

    function applyEdit(abc) {
        if (typeof abc !== 'string' || !abc.trim()) return;
        edit(abc);
    }

    function refresh() {
        syncControls();
        renderSheet();
        const issues = current?.abc.trim() ? validate(current.abc) : [];
        showIssues(issues);
        if (els.undo) els.undo.disabled = !history.undo.length;
        if (els.redo) els.redo.disabled = !history.redo.length;
    }

    function syncControls() {
        const abc = current?.abc || '';
        suppressSrcSync = true;
        if (els.src.value !== abc) els.src.value = abc;
        suppressSrcSync = false;
        const h = parseHeader(abc);
        els.key.value = h.K || '';
        els.meter.value = h.M || '';
        els.unit.value = h.L || '';
        const bpm = parseTempo(h.Q);
        els.tempo.value = bpm === null ? '' : String(bpm);

        els.sections.innerHTML = '';
        // Only a score that came off a clip has somewhere to seek to; a drafted one just gets its menu.
        const seekable = sourceOrigin() !== null;
        sectionSpans(abc).forEach((s, i) => {
            const chip = createDiv(null, 'daw-fx-pick daw-score-chip');
            const name = createSpan(null, 'daw-fx-pick-name');
            name.textContent = s.label || '(unnamed)';
            chip.appendChild(name);
            chip.title = seekable
                ? 'Click to jump the playhead here · right-click to rename, duplicate, reorder or delete'
                : 'Rename, duplicate, reorder or delete this section';
            chip.addEventListener('click', (e) => seekToSection(e, s, i));
            chip.addEventListener('contextmenu', (e) => { e.preventDefault(); openSectionMenu(e, i); });
            setupChipDrag(chip, i);
            els.sections.appendChild(chip);
        });

        syncRenderingButtons();
        const mode = abc.trim() ? modeForScore(abc) : null;
        els.modeNote.textContent = mode === null ? ''
            : mode === 'full'
                ? 'This score carries chord symbols, so it renders in Full planning mode.'
                : 'This score has no chord symbols, so it renders in Melody mode — the cover setting.';
    }

    /** Dragging one chip onto another is the menu's Move earlier/later, without the counting. */
    function setupChipDrag(chip, index) {
        chip.draggable = true;
        chip.addEventListener('dragstart', (e) => {
            dragSection = index;
            // Firefox refuses to start a drag that carries nothing.
            e.dataTransfer.setData(SECTION_MIME, String(index));
            e.dataTransfer.effectAllowed = 'move';
        });
        chip.addEventListener('dragover', (e) => {
            if (dragSection === null || dragSection === index) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            chip.classList.add('daw-drop-target');
        });
        chip.addEventListener('dragleave', () => chip.classList.remove('daw-drop-target'));
        chip.addEventListener('drop', (e) => {
            e.preventDefault();
            chip.classList.remove('daw-drop-target');
            const from = dragSection;
            dragSection = null;
            if (from !== null && from !== index) moveSection(from, index);
        });
        chip.addEventListener('dragend', () => { dragSection = null; chip.classList.remove('daw-drop-target'); });
    }

    function showIssues(issues) {
        els.issues.innerHTML = '';
        for (const i of issues) {
            const row = createDiv(null, i.severity === 'error' ? 'daw-score-err' : 'daw-score-warn');
            row.textContent = i.message;
            if (i.line > 0) {
                row.title = `Line ${i.line + 1} — click to jump there`;
                row.addEventListener('click', () => selectLine(i.line));
            }
            els.issues.appendChild(row);
        }
        const blocked = issues.some(i => i.severity === 'error');
        els.go.disabled = blocked || !current?.abc.trim();
        els.go.title = blocked ? 'Fix the errors above first' : 'Render this score into a new clip';
    }

    function selectLine(lineIndex) {
        const lines = (current?.abc || '').split('\n');
        if (lineIndex < 0 || lineIndex >= lines.length) return;
        let start = 0;
        for (let i = 0; i < lineIndex; i++) start += lines[i].length + 1;
        els.src.focus();
        els.src.setSelectionRange(start, start + lines[lineIndex].length);
    }

    function renderSheet() {
        const host = els.sheet;
        if (!host) return;
        if (typeof ABCJS === 'undefined') {
            host.innerHTML = '<span class="daw-stems-clipinfo">The notation library did not load.</span>';
            return;
        }
        if (!current?.abc.trim()) {
            visual = null;
            planTimings = null;
            setTransportTune();
            renderStartScreen(host);
            return;
        }
        prep = prepareForEngraving(current.abc);
        const accent = getComputedStyle(document.body).getPropertyValue('--emphasis').trim() || '#7855e1';
        stopPlan();
        lastTimingIndex = -1;
        try {
            // Fixed staffwidth + re-engrave on resize rather than `responsive`, which scales the glyphs to fit.
            visual = ABCJS.renderAbc(host, prep.text, {
                add_classes: true,
                staffwidth: Math.max(320, host.clientWidth - 40),
                wrap: { preferredMeasuresPerLine: 4, minSpacing: 1.6, maxSpacing: 2.7 },
                selectionColor: accent,
                dragColor: accent,
                dragging: true,
                selectTypes: ['note'],
                clickListener: onScoreClick
            })[0];
            // Populates visual.noteTimings, which is what the DAW transport is followed against.
            if (visual) {
                new ABCJS.TimingCallbacks(visual, { eventCallback: () => {} });
                planTimings = visual.noteTimings;
            }
            markBadBars();
            setTransportTune();
            watchWidth(host);
        }
        catch (e) {
            console.error('[AudioDawScore] Engraving failed:', e);
            host.innerHTML = `<span class="daw-score-err">Could not draw this score: ${escapeHtml(e.message)}</span>`;
        }
    }

    /** staffwidth is a fixed pixel count, so the engraving has to be redrawn when the panel is resized. */
    let widthWatcher = null, lastWidth = 0;
    function watchWidth(host) {
        lastWidth = host.clientWidth;
        if (widthWatcher || typeof ResizeObserver === 'undefined') return;
        widthWatcher = new ResizeObserver(() => {
            const w = host.clientWidth;
            if (!w || Math.abs(w - lastWidth) < 24) return;
            lastWidth = w;
            renderSheet();
        });
        widthWatcher.observe(host);
    }

    // ===== first run =====

    /** An empty score offers what can be done about it, rather than a sentence sending the reader elsewhere. */
    function renderStartScreen(host) {
        host.innerHTML = '';
        const row = createDiv(null, 'daw-score-start');
        const clip = selectedClip?.clip;
        const score = clip?.meta?.score;
        const tr = cb.getTransport ? cb.getTransport() : {};
        const meter = Array.isArray(tr.timeSignature) ? tr.timeSignature.join('/') : '4/4';
        startAction(row, 'New blank score',
            `Eight empty bars in ${meter} at ${Math.round(tr.bpm || 120)} BPM. Click the notes in.`,
            null, () => {
                loadScore(blankScore(), { source: 'blank', label: 'New score' });
                notice('Blank score ready — click a note to edit it', 'green');
            });
        startAction(row, 'Load from the clip',
            score ? `Open the plan ${clip.name} was rendered from.` : '',
            score ? null : clip ? `${clip.name} carries no score — only YuE2 generations plan one.`
                : 'Select a generated clip on the timeline first.',
            loadFromSelectedClip);
        startAction(row, 'Transcribe a recording',
            clip?.blob ? `Read the melody, chords, key and tempo out of ${clip.name}.` : '',
            transcribing ? 'Reading a recording now…' : clip?.blob ? null
                : 'Select a clip with audio on the timeline first.',
            () => transcribeSelection('clip'));
        host.appendChild(row);
        const hint = createDiv(null, 'daw-stems-desc');
        hint.textContent = 'Or drop a clip, an .abc file or a recording onto this sheet.';
        host.appendChild(hint);
    }

    /** Disabled says why in the card itself: a title on a disabled button is unreadable in some browsers. */
    function startAction(parent, name, desc, blocked, onClick) {
        const b = document.createElement('button');
        b.className = 'daw-fx-pick';
        const n = createSpan(null, 'daw-fx-pick-name');
        n.textContent = name;
        const d = createSpan(null, 'daw-fx-pick-desc');
        d.textContent = blocked || desc;
        b.appendChild(n);
        b.appendChild(d);
        b.disabled = !!blocked;
        b.addEventListener('click', onClick);
        parent.appendChild(b);
        return b;
    }

    /** The start screen names the live selection, so it is redrawn whenever that can have moved. */
    function refreshStartScreen() {
        if (els.sheet && !current?.abc.trim()) renderStartScreen(els.sheet);
    }

    // ===== drops onto the sheet =====

    function setupSheetDrop(sheet) {
        const mine = (e) => !e.dataTransfer || !e.dataTransfer.types.includes(SECTION_MIME);
        sheet.addEventListener('dragover', (e) => {
            if (!mine(e)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            sheet.classList.add('daw-drop-target');
        });
        sheet.addEventListener('dragleave', (e) => {
            if (!sheet.contains(e.relatedTarget)) sheet.classList.remove('daw-drop-target');
        });
        sheet.addEventListener('drop', (e) => {
            if (!mine(e)) return;
            e.preventDefault();
            sheet.classList.remove('daw-drop-target');
            const file = e.dataTransfer.files?.[0];
            if (file) dropFile(file, { clientX: e.clientX, clientY: e.clientY });
            else notice('Drop a clip, an .abc file or a recording', 'yellow');
        });
    }

    async function dropFile(file, at) {
        if (SCORE_FILE.test(file.name)) {
            const text = await file.text();
            // The same gate the paste path relies on: no K: and abcjs has nothing to engrave.
            if (!/^\s*K:/m.test(text)) { notice(`${file.name} does not read as an ABC score`, 'yellow'); return; }
            loadScore(text, { source: 'file', label: file.name.replace(/\.[^.]+$/, '').slice(0, 32) });
            notice(`${file.name} loaded`, 'green');
            return;
        }
        if (!AUDIO_FILE.test(file.name)) { notice(`The score sheet has no use for ${file.name}`, 'yellow'); return; }
        if (!cb.importClip) return;
        let added = null;
        try { added = await cb.importClip(file); }
        catch (e) { notice(`Could not read ${file.name} as audio`, 'yellow'); return; }
        if (added?.clip) offerTranscribe(added.clip, at);
    }

    /** A recording that just arrived has one thing this tab can do with it, so it asks rather than assumes. */
    function offerTranscribe(clip, at) {
        if (cb.selectClip) cb.selectClip(clip.id);
        if (!cb.showMenu) return;
        cb.showMenu(at, [
            { label: `Transcribe ${clip.name}`, action: () => runTranscribe({ clip, stem: false }, 'full', 'clip') },
            { label: 'Leave it on the timeline', action: () => {} }
        ]);
    }

    /** The DAW moves a clip with a pointer gesture, not HTML5 dnd, so the sheet is offered the pointer instead. */
    function clipDragOver(x, y) {
        if (!els.sheet) return false;
        const r = els.sheet.getBoundingClientRect();
        const over = r.width > 0 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
        els.sheet.classList.toggle('daw-drop-target', over);
        return over;
    }

    function clipDropped(clip, x, y) {
        if (!clipDragOver(x, y)) return false;
        els.sheet.classList.remove('daw-drop-target');
        const score = clip?.meta?.score;
        if (score) {
            loadScore(score.abc, { ...score, clipId: clip.id });
            notice(`Score loaded from ${clip.name}`, 'green');
        }
        else if (clip?.blob) offerTranscribe(clip, { clientX: x, clientY: y });
        else notice(`${clip?.name || 'That clip'} has neither a score nor audio`, 'yellow');
        return true;
    }

    /**
     * All editing enters here. abcjs reports coordinates in the ENGRAVED text and never modifies the ABC, so
     * every gesture is mapped back to the original and applied as a text rewrite.
     */
    function onScoreClick(abcelem, tuneNumber, classes, analysis, drag, ev) {
        if (!prep || !current || abcelem?.startChar === undefined) return;
        const start = toOriginal(prep.reps, abcelem.startChar);
        const end = toOriginal(prep.reps, abcelem.endChar);
        const parts = splitElement(current.abc, start, end);
        selection = { start, end, voice: analysis?.voice ?? 0, ...parts };

        if (els.src) {
            els.src.setSelectionRange(start, Math.max(start + 1, end));
        }
        // A drag is a pitch change; abcjs reports how many staff steps, we rewrite the note.
        if (drag && drag.step) {
            applyTranspose(drag.step);
            return;
        }
        // Clicking the chord symbol edits the harmony, not the note it is attached to.
        if (analysis?.clickedName === 'chord' || (!parts.token && parts.chord)) {
            openChordMenu(ev, abcelem);
            return;
        }
        // The second click of a double-click means "play from here", which the sheet's dblclick handler takes.
        if (ev?.detail >= 2) return;
        if (parts.token) openNoteMenu(ev);
    }

    function applyTranspose(steps) {
        const t = selection?.token;
        if (!t || t.isRest) { notice('Rests have no pitch to move', 'yellow'); return; }
        const text = transposeToken(t, steps);
        if (text === null) return;
        edit(replaceRange(current.abc, t.start, t.end, text));
    }

    // ===== chords =====

    const CHORD_QUALITIES = ['', 'maj7', 'maj9', '6', '7', '9', '13', 'm', 'm7', 'm9', 'm11', 'sus4', 'dim', 'aug'];

    function openChordMenu(ev, abcelem) {
        const existing = selection?.chord?.name || abcelem?.chord?.[0]?.name || '';
        const rootMatch = /^([A-G][#b]?)(.*)$/.exec(existing);
        const root = rootMatch ? rootMatch[1] : 'C';
        const bass = /\/[A-G][#b]?$/.exec(existing)?.[0] || '';
        const items = CHORD_QUALITIES.map(q => ({
            label: `${root}${q}${bass}` + (`${root}${q}${bass}` === existing ? '  •' : ''),
            action: () => setChord(`${root}${q}${bass}`)
        }));
        items.push({ label: 'Custom…', action: () => {
            const v = prompt('Chord symbol:', existing);
            if (v !== null) setChord(v.trim());
        } });
        if (existing) items.push({ label: 'Remove chord', action: () => setChord('') });
        if (cb.showMenu) cb.showMenu(ev, items);
    }

    /** Write a chord onto the selected element, replacing or inserting the quoted span before its note. */
    function setChord(name) {
        if (!selection) return;
        const { chord, token } = selection;
        if (chord) {
            const text = name ? `"${name}"` : '';
            edit(replaceRange(current.abc, chord.start, chord.end, text));
        }
        else if (name && token) {
            edit(replaceRange(current.abc, token.start, token.start, `"${name}"`));
        }
    }

    // ===== notes =====

    function openNoteMenu(ev) {
        const t = selection?.token;
        if (!t) return;
        const items = [];
        if (!t.isRest) {
            items.push({ label: 'Up a step', action: () => applyTranspose(1) });
            items.push({ label: 'Down a step', action: () => applyTranspose(-1) });
            items.push({ label: 'Up an octave', action: () => applyTranspose(7) });
            items.push({ label: 'Down an octave', action: () => applyTranspose(-7) });
            items.push({ label: 'Sharp', action: () => setAccidental('^') });
            items.push({ label: 'Flat', action: () => setAccidental('_') });
            items.push({ label: 'Natural', action: () => setAccidental('=') });
            if (t.accidental) items.push({ label: 'Clear accidental', action: () => setAccidental('') });
            items.push({ label: t.tie ? 'Untie from next' : 'Tie to next', action: toggleTie });
            items.push({ label: 'Turn into a rest', action: () => setRest(true) });
        }
        else if (t.letter !== 'Z') {
            items.push({ label: 'Turn into a note', action: () => setRest(false) });
        }
        if (t.count >= 2) items.push({ label: 'Split in two', action: splitNote });
        items.push({ label: 'Merge with next', action: mergeWithNext });
        const per = unitsPerBar(parseHeader(current.abc));
        if (per) {
            for (const [label, frac] of [['Whole', 1], ['Half', 2], ['Quarter', 4], ['Eighth', 8], ['Sixteenth', 16]]) {
                const units = Math.round(per / frac);
                if (units >= 1 && units !== t.count) {
                    items.push({ label: `Length: ${label}`, action: () => setDuration(units) });
                }
            }
            if (t.count * 1.5 % 1 === 0) items.push({ label: 'Length: dotted', action: () => setDuration(t.count * 1.5) });
        }
        // Offset off the cursor so the note stays clickable under it — otherwise the menu eats the second
        // click of a double-click and the note never hears it.
        if (cb.showMenu) cb.showMenu({ clientX: (ev?.clientX || 0) + 12, clientY: (ev?.clientY || 0) + 12 }, items);
    }

    function setAccidental(acc) {
        const t = selection?.token;
        if (!t || t.isRest) return;
        edit(replaceRange(current.abc, t.start, t.end, tokenText(t, { accidental: acc })));
    }

    /** A tie only means anything between two notes, so it is written on the earlier one. */
    function toggleTie() {
        const t = selection?.token;
        if (!t) return;
        edit(replaceRange(current.abc, t.start, t.end, tokenText(t, { tie: t.tie ? '' : '-' })));
    }

    function setRest(toRest) {
        const t = selection?.token;
        if (!t) return;
        const letter = toRest ? 'z' : 'c';
        edit(replaceRange(current.abc, t.start, t.end,
            tokenText(t, { letter, marks: toRest ? '' : t.marks, accidental: '', tie: '' })));
    }

    /** Halve the note and repeat it, so the bar total is unchanged. */
    function splitNote() {
        const t = selection?.token;
        if (!t || t.count < 2) return;
        const a = Math.ceil(t.count / 2), b = t.count - a;
        const text = tokenText(t, { count: a, forceCount: true, tie: '' })
            + tokenText(t, { count: b, forceCount: true });
        edit(replaceRange(current.abc, t.start, t.end, text));
    }

    /** Absorb the following note or rest into this one. Total duration is unchanged, so the bar still balances. */
    function mergeWithNext() {
        const t = selection?.token;
        if (!t) return;
        const rest = current.abc.slice(t.end);
        const m = /^(\s*)((?:"[^"]*")?)([\^=_]{0,2}[A-Ga-gxz][,']*)(\d*)((?:\/\d*)?)(-?)/.exec(rest);
        if (!m) { notice('Nothing to merge into — the bar ends here', 'yellow'); return; }
        const nextCount = m[4] ? parseInt(m[4], 10) : 1;
        edit(replaceRange(current.abc, t.start, t.end + m[0].length,
            tokenText(t, { count: t.count + nextCount, forceCount: true, tie: m[6] || '' })));
    }

    // ===== durations =====

    /** Unit counts per bar, in L: units. 4/4 at L:1/16 is 16. */
    function unitsPerBar(h) {
        const m = meterFraction(h.M), l = meterFraction(h.L);
        return m && l ? Math.round(m / l) : null;
    }

    /** The text range of the bar containing pos, bounded by barlines and by its own line. */
    function barBounds(abc, pos) {
        const lineStart = abc.lastIndexOf('\n', pos - 1) + 1;
        let lineEnd = abc.indexOf('\n', pos);
        if (lineEnd < 0) lineEnd = abc.length;
        let start = lineStart, end = lineEnd;
        for (let i = pos - 1; i >= lineStart; i--) if (abc[i] === '|') { start = i + 1; break; }
        for (let i = pos; i < lineEnd; i++) if (abc[i] === '|') { end = i; break; }
        return { start, end };
    }

    /** Note and rest tokens inside a range, skipping quoted chord symbols. */
    function barTokens(abc, from, to) {
        const out = [];
        let i = from;
        while (i < to) {
            if (abc[i] === '"') { const j = abc.indexOf('"', i + 1); i = j < 0 || j >= to ? to : j + 1; continue; }
            const m = NOTE_TOKEN.exec(abc.slice(i, to));
            if (m && m[0]) {
                out.push({
                    start: i, end: i + m[0].length,
                    accidental: m[1], letter: m[2], marks: m[3],
                    count: m[4] ? parseInt(m[4], 10) : 1, hasCount: !!m[4],
                    fraction: m[5] || '', tie: m[6] || '', isRest: /[xzZ]/.test(m[2])
                });
                i += m[0].length;
                continue;
            }
            i++;
        }
        return out;
    }

    /**
     * Change a note's length and keep the bar full by taking the difference out of the bar's rests (or giving
     * it back to them). A bar that cannot absorb the change is left alone and said so — silently producing a
     * short bar is the one outcome worth refusing.
     */
    function setDuration(units) {
        const t = selection?.token;
        if (!t || !current) return;
        const h = parseHeader(current.abc);
        const per = unitsPerBar(h);
        if (!per) { notice('The header needs M: and L: before durations can be edited', 'yellow'); return; }
        const bar = barBounds(current.abc, t.start);
        const toks = barTokens(current.abc, bar.start, bar.end);
        const target = toks.find(x => x.start === t.start);
        if (!target) return;
        const total = toks.reduce((s, x) => s + x.count, 0);
        let excess = (total - target.count + units) - per;

        // A rest is about to follow, so a tie on the target would bind a note to a rest — not legal ABC.
        const keepTie = excess < 0 ? '' : target.tie;
        const edits = [{ start: target.start, end: target.end, text: tokenText(target, { count: units, forceCount: true, tie: keepTie }) }];
        if (excess > 0) {
            // Take it back from rests, nearest first.
            const rests = toks.filter(x => x.isRest && x.start !== target.start)
                .sort((a, b) => Math.abs(a.start - target.start) - Math.abs(b.start - target.start));
            for (const r of rests) {
                if (excess <= 0) break;
                const take = Math.min(r.count, excess);
                excess -= take;
                edits.push(r.count - take === 0
                    ? { start: r.start, end: r.end, text: '' }
                    : { start: r.start, end: r.end, text: tokenText(r, { count: r.count - take, forceCount: true }) });
            }
            if (excess > 0) { notice(`This bar has no rest to shorten — it would run ${excess} units over`, 'yellow'); return; }
        }
        else if (excess < 0) {
            edits.push({ start: target.end, end: target.end, text: `z${-excess}` });
        }
        let abc = current.abc;
        for (const e of edits.sort((a, b) => b.start - a.start)) abc = replaceRange(abc, e.start, e.end, e.text);
        edit(abc);
    }

    // ===== sections =====

    /** A section runs from its % comment to just before the next one. Moving whole sections keeps both voices
     *  in step by construction, which is what makes structural edits safe. */
    function sectionSpans(abc) {
        const lines = abc.split('\n');
        const marks = [];
        let inHeader = true;
        for (let i = 0; i < lines.length; i++) {
            const t = lines[i].trim();
            if (inHeader) { if (/^K:/.test(t)) inHeader = false; continue; }
            if (t.startsWith('%')) marks.push({ label: t.replace(/^%+\s*/, '').trim(), line: i });
        }
        return marks.map((m, i) => ({
            ...m,
            endLine: i + 1 < marks.length ? marks[i + 1].line - 1 : lines.length - 1
        }));
    }

    function withSections(abc, fn) {
        const lines = abc.split('\n');
        const spans = sectionSpans(abc);
        if (!spans.length) return null;
        const out = fn(lines, spans);
        return out === null ? null : out.join('\n');
    }

    /** Lift a whole section out and put it back at another chip's place. */
    function moveSection(from, to) {
        if (from === to) return;
        edit(withSections(current?.abc || '', (lines, sp) => {
            const s = sp[from], t = sp[to];
            const block = lines.splice(s.line, s.endLine - s.line + 1);
            // Moving later, the target's own lines have already shifted up by what was lifted out.
            lines.splice(to < from ? t.line : t.endLine + 1 - block.length, 0, ...block);
            return lines;
        }));
    }

    function openSectionMenu(ev, index) {
        const spans = sectionSpans(current?.abc || '');
        const s = spans[index];
        if (!s) return;
        const items = [
            { label: 'Jump to it', action: () => selectLine(s.line) },
            { label: 'Rename…', action: () => {
                const v = prompt('Section name:', s.label);
                if (v === null) return;
                edit(withSections(current.abc, (lines) => {
                    lines[s.line] = `% ${v.trim()}`;
                    return lines;
                }));
            } },
            { label: 'Duplicate', action: () => edit(withSections(current.abc, (lines) => {
                const block = lines.slice(s.line, s.endLine + 1);
                lines.splice(s.endLine + 1, 0, ...block);
                return lines;
            })) }
        ];
        if (index > 0) items.push({ label: 'Move earlier', action: () => moveSection(index, index - 1) });
        if (index < spans.length - 1) items.push({ label: 'Move later', action: () => moveSection(index, index + 1) });
        if (spans.length > 1) items.push({ label: 'Delete section', action: () => edit(withSections(current.abc, (lines) => {
            lines.splice(s.line, s.endLine - s.line + 1);
            return lines;
        })) });
        if (cb.showMenu) cb.showMenu(ev, items);
    }

    // ===== numbered melody =====

    /**
     * Turn scale degrees into bars of ABC, the way the YuE2 researchers corrected a melody by typing
     * "1155665 / 4433221". Groups split on / or whitespace, one group per bar, 0 is a rest.
     */
    function numbersToAbc(digits, h, octave) {
        const per = unitsPerBar(h);
        const tonic = /^([A-G])/.exec(h.K || 'C');
        if (!per || !tonic) return null;
        const base = LETTERS.indexOf(tonic[1]) + 7 * octave;
        const groups = digits.split(/[\s/|,]+/).map(g => g.replace(/[^0-7]/g, '')).filter(Boolean);
        if (!groups.length) return null;
        const bars = [];
        for (const group of groups) {
            const each = Math.floor(per / group.length);
            if (each < 1) return null;   // more degrees than the bar has units
            let used = 0;
            const notes = [...group].map(ch => {
                const d = parseInt(ch, 10);
                used += each;
                if (d === 0) return `z${each}`;
                const pt = pitchToken(base + (d - 1));
                return `${pt.letter}${pt.marks}${each}`;
            });
            if (used < per) notes.push(`z${per - used}`);
            bars.push(notes.join(''));
        }
        return bars.join('|');
    }

    function applyAlignBars() {
        if (!current?.abc.trim()) return;
        const aligned = alignBars(current.abc);
        if (aligned === current.abc) { notice('The barlines already line up', 'green'); return; }
        // abcjs quotes the offending column and a slice of the line back inside its own warnings, both of
        // which padding moves, so the round-trip checks what this transform could actually break instead:
        // that nothing but spaces was added, and that the verdict still has the same shape.
        const bare = (text) => text.replace(/ /g, '');
        const shape = (text) => validate(text).map(i => i.severity).join(',');
        if (bare(aligned) !== bare(current.abc) || shape(aligned) !== shape(current.abc)) {
            notice('Aligning would have changed what this score says — left as it was', 'yellow');
            return;
        }
        edit(aligned);
        notice('Barlines aligned', 'green');
    }

    function insertNumbered() {
        const raw = els.numbers.value.trim();
        if (!raw || !current?.abc.trim()) return;
        const h = parseHeader(current.abc);
        const octave = parseInt(els.numberOctave.value, 10) || 1;
        const bars = numbersToAbc(raw, h, octave);
        if (!bars) { notice('Could not read those degrees — use digits 1-7, 0 for a rest', 'yellow'); return; }
        if (!selection?.token) { notice('Select a bar on the staff first — the new bars replace it', 'yellow'); return; }
        const bar = barBounds(current.abc, selection.token.start);
        edit(replaceRange(current.abc, bar.start, bar.end, bars));
        notice(`Wrote ${bars.split('|').length} bar(s) from degrees`, 'green');
    }

    // ===== audition =====

    /**
     * abcjs's own SynthController, which is what carries pause, click-to-seek, loop and tempo warp. Its Play
     * button is left out (displayPlay: false) because it binds e.play at load time and swallows the rejection
     * a missing sample set produces, which is exactly the case the local/remote fallback below exists for.
     */
    function ensureTransport() {
        if (synthCtl || !els.transport) return synthCtl;
        if (typeof ABCJS === 'undefined' || !ABCJS.synth.supportsAudio()) {
            els.transport.textContent = 'This browser cannot play audio here.';
            els.transport.className = 'daw-stems-clipinfo';
            return null;
        }
        synthCtl = new ABCJS.synth.SynthController();
        synthCtl.load(els.transport, {
            onEvent: highlightTiming,
            onStart: syncPlayButton,
            onFinished: () => { clearHighlight(); syncPlayButton(); }
        }, { displayPlay: false, displayLoop: true, displayRestart: true, displayProgress: true, displayWarp: true });
        return synthCtl;
    }

    function synthOptions() {
        const opts = { chordsOff: !els.chords?.checked };
        if (soundFontUrl) opts.soundFontUrl = soundFontUrl;
        return opts;
    }

    /**
     * Point the transport at what is on the staff now. setTune keeps the previous tune's primed buffer and its
     * isLoading flag, so without this reset a re-engrave would audition the old score and one failed sample
     * fetch would wedge every later Play in the ready-poll. userAction is false: nothing is fetched until Play.
     */
    function setTransportTune() {
        // An empty tab has nothing to play, and building the control here would open an AudioContext before
        // anyone asked for sound.
        if (!visual && !synthCtl) return;
        const ctl = ensureTransport();
        if (!ctl) return;
        try { ctl.destroy(); } catch (_) {}
        ctl.isLoaded = false;
        ctl.isLoading = false;
        ctl.midiBuffer = null;
        if (!visual) { ctl.disable(true); syncPlayButton(); return; }
        ctl.setTune(visual, false, synthOptions()).catch(() => {});
        // go() is what fills the BPM readout, and that waits for the first Play — show the plan's own until then.
        try { ctl.control?.setTempo(Math.round(visual.getBeatsPerMeasure() / visual.millisecondsPerMeasure() * 60000)); }
        catch (_) {}
        syncPlayButton();
    }

    /**
     * Play and pause are one button, because play() is the toggle: it flips isStarted and pauses on the way
     * down. Calling pause() directly would stop the clocks but leave isStarted set, so the next press would
     * pause an audition that is already silent.
     */
    async function playPause() {
        const ctl = ensureTransport();
        if (!ctl || !visual) { notice('This browser cannot play audio here', 'yellow'); return; }
        els.play.disabled = true;
        try {
            await ctl.play();
        }
        catch (local) {
            // A local set that cannot be read is worth one retry against the host abcjs ships with,
            // rather than a dead Play button.
            if (soundFontUrl) {
                console.warn('[AudioDawScore] Local soundfont failed, falling back to the remote host', local);
                soundFontUrl = null;
                setTransportTune();
                try { await ctl.play(); }
                catch (remote) {
                    console.error('[AudioDawScore] Audition failed:', remote);
                    notice('Could not play the plan — the instrument samples could not be fetched', 'yellow');
                    setTransportTune();
                }
            }
            else {
                console.error('[AudioDawScore] Audition failed:', local);
                notice('Could not play the plan — the instrument samples could not be fetched', 'yellow');
                setTransportTune();
            }
        }
        finally {
            els.play.disabled = false;
            syncPlayButton();
        }
    }

    function syncPlayButton() {
        if (!els.play) return;
        const label = synthCtl?.isStarted ? 'Pause' : 'Play';
        els.play.textContent = label;
        // miniButton names the button once, but this one's label changes, so its name has to follow.
        els.play.setAttribute('aria-label', `${label} — ${els.play.title}`);
    }

    /**
     * Double-click a note to hear the score from there. Seeking by fraction rather than seconds survives a
     * tempo warp, which rewrites every timing but not their proportions.
     */
    async function playFromNote(e) {
        const node = e.target?.closest?.('.abcjs-note, .abcjs-rest');
        if (!node || !visual) return;
        e.preventDefault();
        document.querySelector('.daw-context-menu')?.remove();
        const ctl = ensureTransport();
        if (!ctl) return;
        if (!ctl.isStarted) await playPause();
        const timings = visual.noteTimings || [];
        const end = timings[timings.length - 1]?.milliseconds || 0;
        const hit = timings.find(t => (t.elements || []).flat()
            .some(n => n === node || node.contains?.(n) || n?.contains?.(node)));
        if (hit && end > 0) ctl.seek(hit.milliseconds / end);
    }

    /** Stop the audition. Kept as the DAW's way of silencing the tab; the transport's own Restart rewinds. */
    function stopPlan() {
        // pause() only stops the clocks — play() owns isStarted, so it has to be cleared alongside.
        try { synthCtl?.pause(); } catch (_) {}
        if (synthCtl) synthCtl.isStarted = false;
        clearHighlight();
        syncPlayButton();
    }

    /**
     * Mark the bars whose durations do not add up on the staff itself. Measured off the engraved tune rather
     * than the source, so what is marked and what is drawn can never be two different texts.
     */
    function markBadBars() {
        if (!visual || !prep) return;
        const barWhole = meterFraction(parseHeader(prep.text).M);
        if (!barWhole) return;
        for (const line of visual.lines || []) {
            for (const staff of line.staff || []) {
                for (const voice of staff.voices || []) {
                    let sum = 0, skip = false, run = [];
                    for (const el of voice) {
                        if (el.el_type === 'bar') {
                            if (!skip && sum > 0 && Math.abs(sum - barWhole) > 1e-6) {
                                markBar(run.concat(el), `This bar holds ${(sum / barWhole * 100).toFixed(0)}% of a full bar.`);
                            }
                            sum = 0; skip = false; run = [];
                            continue;
                        }
                        if (el.rest && el.rest.type === 'multimeasure') { skip = true; continue; }
                        if (typeof el.duration === 'number') sum += el.duration;
                        run.push(el);
                    }
                }
            }
        }
    }

    function markBar(elements, why) {
        let titled = false;
        for (const el of elements) {
            for (const node of el.abselem?.elemset || []) {
                if (!node?.classList) continue;
                node.classList.add('daw-score-badbar');
                if (titled) continue;
                const t = document.createElementNS('http://www.w3.org/2000/svg', 'title');
                t.textContent = why;
                node.appendChild(t);
                titled = true;
            }
        }
    }

    function clearHighlight() {
        for (const el of highlighted) el.classList?.remove('playing-note');
        highlighted = [];
        lastSystemTop = null;
    }

    /** Highlight what is sounding, and follow the music one staff system at a time rather than one note. */
    function highlightTiming(ev) {
        clearHighlightOnly();
        if (!ev) return;
        highlighted = (ev.elements || []).flat().filter(Boolean);
        for (const el of highlighted) el.classList?.add('playing-note');
        if (ev.top !== lastSystemTop) {
            lastSystemTop = ev.top;
            highlighted[0]?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
        }
    }

    function clearHighlightOnly() {
        for (const el of highlighted) el.classList?.remove('playing-note');
        highlighted = [];
    }

    /**
     * Follow the DAW transport. The score carries the tempo it was PLANNED at, which the render only
     * approximates, so this tracks the plan rather than claiming sample accuracy.
     */
    function syncTime(seconds) {
        // The audition owns the highlight while it runs; two transports fighting over it helps nobody.
        if (synthCtl?.isStarted || !planTimings?.length || !els.sheet?.isConnected) return;
        const clip = selectedClip?.clip;
        if (!clip || !current?.meta || clip.meta?.score?.abc !== current.abc) return;
        const rel = seconds - (clip.startTime || 0) + (clip.offset || 0);
        if (rel < 0) { clearHighlightOnly(); return; }
        const ms = rel * 1000;
        const timings = planTimings;
        let lo = 0, hi = timings.length - 1, found = -1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (timings[mid].milliseconds <= ms) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) { clearHighlightOnly(); return; }
        if (found === lastTimingIndex) return;
        lastTimingIndex = found;
        highlightTiming(timings[found]);
    }

    function exportMidi() {
        if (!current?.abc.trim()) return;
        try {
            // One entry per tune in the ABC; ours always holds exactly one.
            const data = ABCJS.synth.getMidiFile(current.abc, { midiOutputType: 'binary' })[0];
            if (!data) { notice('This score produced no MIDI', 'yellow'); return; }
            const blob = new Blob([data], { type: 'audio/midi' });
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = `${(current.meta?.label || 'score').replace(/[^\w-]+/g, '_')}.mid`;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(a.href), 1000);
        }
        catch (e) {
            console.error('[AudioDawScore] MIDI export failed:', e);
            notice('Could not build a MIDI file from this score', 'red');
        }
    }

    // ===== LLM editing (through the LLMAssistant extension) =====

    const LLM_PRESETS = [
        ['Reharmonise — jazz', 'Reharmonise with extended jazz voicings: major and minor ninths, dominant thirteenths, and a few tasteful substitutions.'],
        ['Reharmonise — modern', 'Reharmonise with modern harmony: chromatic bass movement, brief tonicisations and a tritone substitution or two.'],
        ['Reharmonise — simpler', 'Simplify the harmony to plain triads and sevenths that a small band could play.'],
        ['Add a bridge', 'Add an eight-bar bridge before the final chorus that departs from the home key and returns to it.'],
        ['Make the Ins answer the vocal', 'Where the vocal rests, give the instrumental voice a short answering phrase drawn from the vocal melody.'],
        ['Lift the last chorus', 'Raise the energy of the final chorus: a fuller instrumental line and a more emphatic harmony.']
    ];
    const LLM_INVARIANTS = [
        ['exact', 'Keep every pitch and rhythm exactly'],
        ['pitch', 'Keep the pitches, rhythm may move'],
        ['contour', 'Keep the melodic shape only'],
        ['free', 'Adapt the melody within reason']
    ];

    function buildLlmCard(parent) {
        const card = createDiv(null, 'daw-fx-card');
        const head = createDiv(null, 'daw-fx-card-head');
        const title = createSpan(null, 'daw-fx-card-title');
        title.textContent = 'Edit with an LLM';
        head.appendChild(title);
        card.appendChild(head);
        els.llmBody = createDiv(null, 'daw-score-llm');
        card.appendChild(els.llmBody);
        parent.appendChild(card);
        renderLlmUnavailable('Checking…');
        probeLlm();
    }

    function renderLlmUnavailable(message, link) {
        els.llmBody.innerHTML = '';
        const note = createDiv(null, 'daw-stems-desc');
        note.textContent = message;
        els.llmBody.appendChild(note);
        if (link) {
            const a = document.createElement('a');
            a.href = link;
            a.target = '_blank';
            a.rel = 'noopener';
            a.textContent = 'SwarmUI-LLMAssistant on GitHub';
            a.className = 'daw-stems-desc';
            els.llmBody.appendChild(a);
        }
    }

    const LLM_MISSING = 'Swarm does not have native LLM support. Install the LLMAssistant extension to edit '
        + 'scores with an LLM.';

    async function probeLlm() {
        let available = false;
        try {
            const r = await AudioLabAPI.callAPI('AudioLabScoreCapabilities', {});
            available = !!r?.llm_available;
            showSoundfont(r);
        }
        catch (_) { available = false; }
        if (!available) {
            renderLlmUnavailable(LLM_MISSING, 'https://github.com/HartsyAI/SwarmUI-LLMAssistant');
            return;
        }
        // The endpoint refuses an empty model, so the list has to be in hand before the card is usable.
        let models = [];
        try {
            models = await new Promise((resolve) => genericRequest('LLMAssistantGetModels', {},
                (d) => resolve(d?.models || []), 0, () => resolve([])));
        }
        catch (_) { models = []; }
        if (!models.length) {
            renderLlmUnavailable('LLMAssistant is installed but has no LLM model available. Add a .gguf to '
                + 'Models/llm, or configure a remote provider in its settings.');
            return;
        }
        buildLlmControls(models);
    }

    function buildLlmControls(models) {
        const body = els.llmBody;
        body.innerHTML = '';

        const presetRow = createDiv(null, 'daw-fx-browser');
        for (const [label, text] of LLM_PRESETS) {
            const chip = createDiv(null, 'daw-fx-pick daw-score-chip');
            const n = createSpan(null, 'daw-fx-pick-name');
            n.textContent = label;
            chip.appendChild(n);
            chip.addEventListener('click', () => { els.llmPrompt.value = text; els.llmPrompt.focus(); });
            presetRow.appendChild(chip);
        }
        body.appendChild(presetRow);

        els.llmPrompt = document.createElement('textarea');
        els.llmPrompt.className = 'daw-generate-text';
        els.llmPrompt.rows = 2;
        els.llmPrompt.placeholder = 'What should change? Pick a preset above or describe it.';
        body.appendChild(els.llmPrompt);

        const row = createDiv(null, 'daw-stems-action-row');
        const scopeWrap = createDiv(null, 'daw-score-field');
        const scopeLbl = createSpan(null, 'daw-stems-ctl-label');
        scopeLbl.textContent = 'Scope';
        els.llmScope = document.createElement('select');
        els.llmScope.className = 'daw-fx-select';
        for (const [v, l] of [['whole', 'Whole score'], ['chords', 'Chords only'], ['section', 'One section']]) {
            const o = document.createElement('option'); o.value = v; o.textContent = l;
            els.llmScope.appendChild(o);
        }
        scopeWrap.appendChild(scopeLbl); scopeWrap.appendChild(els.llmScope);
        row.appendChild(scopeWrap);

        const keepWrap = createDiv(null, 'daw-score-field');
        const keepLbl = createSpan(null, 'daw-stems-ctl-label');
        keepLbl.textContent = 'Keep';
        els.llmKeep = document.createElement('select');
        els.llmKeep.className = 'daw-fx-select';
        for (const [v, l] of LLM_INVARIANTS) {
            const o = document.createElement('option'); o.value = v; o.textContent = l;
            els.llmKeep.appendChild(o);
        }
        els.llmKeep.value = 'exact';
        keepWrap.appendChild(keepLbl); keepWrap.appendChild(els.llmKeep);
        row.appendChild(keepWrap);

        const modelWrap = createDiv(null, 'daw-score-field');
        const modelLbl = createSpan(null, 'daw-stems-ctl-label');
        modelLbl.textContent = 'Model';
        els.llmModel = document.createElement('select');
        els.llmModel.className = 'daw-fx-select';
        for (const m of models) {
            const o = document.createElement('option');
            o.value = m.id;
            o.textContent = m.name || m.title || m.id;
            els.llmModel.appendChild(o);
        }
        modelWrap.appendChild(modelLbl); modelWrap.appendChild(els.llmModel);
        row.appendChild(modelWrap);

        els.llmRun = button(row, 'Rewrite', 'basic-button btn-sm btn-primary daw-stems-go', runLlmEdit);
        body.appendChild(row);

        els.llmStatus = createDiv(null, 'daw-stems-desc daw-score-llm-status');
        body.appendChild(els.llmStatus);
    }

    /** The dialect rules the model has to honour. No double braces anywhere — LLMAssistant substitutes those. */
    function llmInstruction(keep, scope) {
        const keepText = {
            exact: 'Do not change any pitch or rhythm in either voice.',
            pitch: 'Keep every pitch; rhythm may be adjusted.',
            contour: 'Keep the melodic shape; individual pitches and rhythms may change.',
            free: 'You may adapt the melody, but it must stay recognisable.'
        }[keep] || '';
        const scopeText = {
            whole: 'You may edit the whole score.',
            chords: 'Change ONLY the quoted chord symbols. Every note must stay byte-identical.',
            section: 'Confine the edit to the section the user names; leave the rest untouched.'
        }[scope] || '';
        return [
            'You edit ABC music scores for the YuE2 music model. Reply with the complete edited score and nothing else, inside one ```abc fence.',
            '',
            'The dialect is strict:',
            '- Exactly two voices, with the ids Vocal and Ins. Keep the V: header lines exactly as given.',
            '- The body alternates V: Vocal and V: Ins blocks. Within each such pair BOTH voices must contain the same number of bars. Zn is an n-bar rest used to pad the silent voice.',
            '- Every bar must hold exactly the number of L: units the M: meter calls for.',
            '- Chord symbols are quoted strings attached to the Vocal voice, and may change mid-bar.',
            '- Section names are % comment lines. Keep them meaningful.',
            '- Do not add w: lyric lines. Lyrics are supplied separately.',
            '',
            scopeText,
            keepText
        ].filter(Boolean).join('\n');
    }

    async function runLlmEdit() {
        if (!current?.abc.trim()) return;
        const ask = els.llmPrompt.value.trim();
        if (!ask) { notice('Say what should change first', 'yellow'); return; }
        els.llmRun.disabled = true;
        els.llmStatus.textContent = 'Asking the model…';
        const instruction = llmInstruction(els.llmKeep.value, els.llmScope.value);
        try {
            let reply = await askLlm(instruction, buildLlmInput(ask));
            let candidate = extractAbc(reply);
            let issues = candidate ? validate(candidate).filter(i => i.severity === 'error') : [{ message: 'No ```abc block came back.' }];
            if (issues.length) {
                // One correction pass: hand back exactly what failed rather than guessing.
                els.llmStatus.textContent = 'The first attempt was not valid; asking again with the errors…';
                reply = await askLlm(instruction, buildLlmInput(ask)
                    + '\n\nA previous attempt was rejected for these reasons. Fix them:\n'
                    + issues.map(i => `- ${i.message}`).join('\n')
                    + (candidate ? `\n\nThat attempt was:\n${candidate}` : ''));
                candidate = extractAbc(reply);
                issues = candidate ? validate(candidate).filter(i => i.severity === 'error') : [{ message: 'No ```abc block came back.' }];
            }
            if (!candidate || issues.length) {
                els.llmStatus.textContent = 'The model could not produce a valid score: '
                    + issues.map(i => i.message).join(' ');
                return;
            }
            offerLlmResult(candidate);
        }
        catch (e) {
            els.llmStatus.textContent = llmErrorText(e);
        }
        finally {
            els.llmRun.disabled = false;
        }
    }

    function buildLlmInput(ask) {
        const parts = [`Requested change: ${ask}`];
        if (els.style.value.trim()) parts.push(`Style prompt: ${els.style.value.trim()}`);
        if (els.lyrics.value.trim()) parts.push(`Lyrics:\n${els.lyrics.value.trim()}`);
        parts.push(`Score:\n${current.abc}`);
        return parts.join('\n\n');
    }

    function askLlm(instructionText, sampleInput) {
        return new Promise((resolve, reject) => {
            genericRequest('LLMAssistantTestInstruction', { instructionText, sampleInput, model: els.llmModel.value },
                (data) => {
                    if (data?.success === false) reject(new Error(data.error || 'The LLM refused the request'));
                    else resolve(data?.response || '');
                },
                0,
                (err) => reject(new Error(String(err || 'The LLM request failed'))));
        });
    }

    /** Three different reasons this can fail, and they need three different answers. */
    function llmErrorText(e) {
        const msg = String(e?.message || e);
        if (/bad_route|Unknown API route/i.test(msg)) return LLM_MISSING;
        if (/bad_permissions|lack permissions/i.test(msg)) {
            return 'Your account does not have the llm_chat permission, which LLMAssistant requires.';
        }
        return msg;
    }

    function extractAbc(reply) {
        if (!reply) return null;
        const fenced = /```(?:abc)?\s*\n([\s\S]*?)```/i.exec(reply);
        const text = (fenced ? fenced[1] : reply).trim();
        return /(^|\n)K:/.test(text) ? text : null;
    }

    /** Never overwrite silently: show what changed and let the user take it or leave it. */
    function offerLlmResult(candidate) {
        const d = diffScores(current.abc, candidate);
        els.llmStatus.innerHTML = '';
        const summary = createDiv(null, 'daw-stems-clipinfo');
        summary.innerHTML = `<strong>${d.chords} chord${d.chords === 1 ? '' : 's'}</strong> and `
            + `<strong>${d.bars} bar${d.bars === 1 ? '' : 's'}</strong> changed`
            + (d.headers.length ? `, plus ${escapeHtml(d.headers.join(', '))}` : '')
            + `. ${d.notesTouched ? 'Notes were edited.' : 'No note was moved.'}`;
        els.llmStatus.appendChild(summary);
        const row = createDiv(null, 'daw-stems-action-row');
        button(row, 'Apply', 'basic-button btn-sm btn-primary', () => {
            edit(candidate);
            els.llmStatus.textContent = 'Applied. Undo puts the previous score back.';
        });
        button(row, 'Discard', 'basic-button btn-sm', () => { els.llmStatus.textContent = 'Discarded.'; });
        els.llmStatus.appendChild(row);
    }

    /** A chord-level diff, because chord-only rewrites are the common case and the one worth confirming. */
    function diffScores(a, b) {
        const chordsA = chordSpans(a).map(c => c.name);
        const chordsB = chordSpans(b).map(c => c.name);
        let chords = Math.abs(chordsA.length - chordsB.length);
        for (let i = 0; i < Math.min(chordsA.length, chordsB.length); i++) if (chordsA[i] !== chordsB[i]) chords++;
        // Normalise incidental whitespace: a reply is trimmed on arrival, and a lost trailing newline must not
        // read as "notes were edited" on an edit that only touched chords.
        const stripped = (s) => s.split('\n').map(l => l.replace(/"[^"]*"/g, '').trimEnd()).join('\n').trim();
        const barsA = stripped(a).split('|'), barsB = stripped(b).split('|');
        let bars = Math.abs(barsA.length - barsB.length);
        for (let i = 0; i < Math.min(barsA.length, barsB.length); i++) if (barsA[i] !== barsB[i]) bars++;
        const ha = parseHeader(a), hb = parseHeader(b);
        const headers = ['M', 'L', 'K', 'Q'].filter(k => ha[k] !== hb[k]).map(k => `${k}:`);
        return { chords, bars, headers, notesTouched: stripped(a) !== stripped(b) };
    }

    // ===== the instrument contract =====

    const SHARP_ORDER = 'FCGDAEB', FLAT_ORDER = 'BEADGCF';
    const MAJOR_SHARPS = { C: 0, G: 1, D: 2, A: 3, E: 4, B: 5, 'F#': 6, 'C#': 7, F: -1, Bb: -2, Eb: -3, Ab: -4, Db: -5, Gb: -6, Cb: -7 };
    const SHARP_SPELL = [['C', 0], ['C', 1], ['D', 0], ['D', 1], ['E', 0], ['F', 0], ['F', 1], ['G', 0], ['G', 1], ['A', 0], ['A', 1], ['B', 0]];
    const FLAT_SPELL = [['C', 0], ['D', -1], ['D', 0], ['E', -1], ['E', 0], ['F', 0], ['G', -1], ['G', 0], ['A', -1], ['A', 0], ['B', -1], ['B', 0]];

    /** What the key signature already says about each letter. A minor key carries its relative major's. */
    function keyAccidentals(k) {
        const m = /^([A-G][#b]?)\s*(.*)$/.exec((k || 'C').trim());
        let sharps = MAJOR_SHARPS[m ? m[1] : 'C'] ?? 0;
        if (m && /^(m|min)/i.test(m[2]) && !/^maj/i.test(m[2])) sharps -= 3;
        const accidentals = { C: 0, D: 0, E: 0, F: 0, G: 0, A: 0, B: 0 };
        const order = sharps >= 0 ? SHARP_ORDER : FLAT_ORDER;
        for (let i = 0; i < Math.abs(sharps); i++) accidentals[order[i]] = sharps >= 0 ? 1 : -1;
        return { key: m ? m[1] : 'C', mode: m ? m[2] : '', sharps, accidentals };
    }

    /**
     * A MIDI number as an ABC token, spelled against the key. This is the whole reason instruments cannot
     * write ABC themselves: in K:D a bare F already means F sharp, so MIDI 65 has to be written =F.
     */
    function midiToAbc(midi, key) {
        const pc = ((Math.round(midi) % 12) + 12) % 12;
        const [letter, alt] = (key.sharps < 0 ? FLAT_SPELL : SHARP_SPELL)[pc];
        const oct = Math.floor(Math.round(midi) / 12) - 1;
        const want = key.accidentals[letter] || 0;
        const mark = alt === want ? '' : alt === 1 ? '^' : alt === -1 ? '_' : '=';
        return oct >= 5
            ? `${mark}${letter.toLowerCase()}${"'".repeat(oct - 5)}`
            : `${mark}${letter}${','.repeat(Math.max(0, 4 - oct))}`;
    }

    /** The key an instrument should quantize pitch against. */
    function getKey() {
        return keyAccidentals(parseHeader(current?.abc || '').K);
    }

    /** The grid an instrument should quantize time against. */
    function getGrid() {
        const h = parseHeader(current?.abc || '');
        const l = meterFraction(h.L) || 1 / 32;
        const bpm = parseTempo(h.Q) || (cb.getTransport ? cb.getTransport().bpm : 0) || 120;
        return {
            unitLength: h.L || '1/32',
            unitsPerBar: unitsPerBar(h) || Math.round((meterFraction(h.M) || 1) / l),
            meter: h.M || '4/4',
            bpm,
            secondsPerUnit: (60 / bpm) * l * 4
        };
    }

    /** Bar spans per voice in body order. A Zn span stands for n bars behind one piece of text. */
    function voiceBars(abc) {
        const out = [];
        let inHeader = true, voice = null, pos = 0;
        for (const line of abc.split('\n')) {
            const lineStart = pos;
            pos += line.length + 1;
            const t = line.trim();
            if (inHeader) { if (/^K:/.test(t)) inHeader = false; continue; }
            if (!t || t.startsWith('%')) continue;
            const v = /^V:\s*(\S+)/.exec(t);
            if (v) { voice = v[1]; continue; }
            if (/^[A-Za-z]:/.test(t) || voice === null) continue;
            const lineEnd = lineStart + line.length;
            let barStart = lineStart;
            const close = (end) => {
                const text = abc.slice(barStart, end);
                if (text.trim()) {
                    const z = /^\s*Z(\d*)\s*$/.exec(text);
                    out.push({ voice, start: barStart, end, bars: z ? (z[1] ? parseInt(z[1], 10) : 1) : 1 });
                }
                barStart = end + 1;
            };
            for (let i = lineStart; i < lineEnd; i++) {
                if (abc[i] === '"') { const j = abc.indexOf('"', i + 1); i = j < 0 || j >= lineEnd ? lineEnd : j; continue; }
                if (abc[i] === '|') close(i);
            }
            if (barStart < lineEnd) close(lineEnd);
        }
        return out;
    }

    /** A two-voice score in the transport's own meter and tempo, so an instrument has somewhere to play into. */
    function blankScore() {
        const tr = cb.getTransport ? cb.getTransport() : {};
        const meter = Array.isArray(tr.timeSignature) ? `${tr.timeSignature[0]}/${tr.timeSignature[1]}` : '4/4';
        const bpm = Math.round(tr.bpm || 120);
        const per = unitsPerBar({ M: meter, L: '1/32' }) || 32;
        const bars = Array(8).fill(`z${per}`).join('|') + '|';
        return ['X:1', 'T:', `M:${meter}`, 'L:1/32', `Q:1/4=${bpm}`,
            'V: Vocal clef=treble name="Vocal Melody" snm="Vocal"',
            'V: Ins clef=treble name="Ins Melody" snm="Inst."',
            'K:C', '% part 1', 'V: Vocal', bars, 'V: Ins', bars, ''].join('\n');
    }

    /**
     * Write notes into one voice at a bar position. The public entry point for instruments.
     *
     * Every bar it touches is rewritten whole — leading rest, the notes clipped to the bar, trailing rest —
     * so the bar sum is right by construction rather than by arithmetic afterwards, and a note crossing a
     * barline is tied. The validator still has the last word: if the result has an error the score is left
     * exactly as it was and the issues come back.
     *
     * @param {Object} req {voice, barIndex, offsetUnits, notes:[{midi, units}]}; midi null or absent = a rest.
     * @returns {{ok: boolean, issues?: Array, bars?: [number, number]}}
     */
    function insertNotes({ voice = 'Vocal', barIndex = 0, offsetUnits = 0, notes = [] } = {}) {
        const fail = (message) => ({ ok: false, issues: [{ severity: 'error', message }] });
        const wanted = (Array.isArray(notes) ? notes : [])
            .map(n => ({ midi: n.midi ?? null, units: Math.max(0, Math.round(n.units) || 0) }))
            .filter(n => n.units > 0);
        if (!wanted.length) return fail('No notes to write.');
        if (!current?.abc.trim()) loadScore(blankScore(), { source: 'instrument', label: 'New score' });
        const h = parseHeader(current.abc);
        const per = unitsPerBar(h);
        if (!per) return fail('The score needs M: and L: before notes can be written.');
        const key = keyAccidentals(h.K);

        const total = wanted.reduce((s, n) => s + n.units, 0);
        const startAbs = Math.max(0, Math.round(barIndex)) * per + Math.max(0, Math.round(offsetUnits));
        const endAbs = startAbs + total;
        const firstBar = Math.floor(startAbs / per), lastBar = Math.ceil(endAbs / per) - 1;

        let abc = current.abc;
        // A Zn hides n bars behind one span, so anything written into it has to be opened up first.
        for (let guard = 0; guard < 64; guard++) {
            let at = 0, hit = null;
            for (const b of voiceBars(abc).filter(x => x.voice === voice)) {
                if (b.bars > 1 && at <= lastBar && at + b.bars > firstBar) { hit = b; break; }
                at += b.bars;
            }
            if (!hit) break;
            abc = replaceRange(abc, hit.start, hit.end, Array(hit.bars).fill(`z${per}`).join('|'));
        }

        const spans = [];
        let at = 0;
        for (const b of voiceBars(abc).filter(x => x.voice === voice)) { spans[at] = b; at += b.bars; }
        if (lastBar >= at) {
            return fail(`Voice ${voice} has ${at} bar${at === 1 ? '' : 's'}; this take needs ${lastBar + 1}. `
                + 'Insert bars first — adding them here would desync the two voices.');
        }

        const edits = [];
        let noteAt = 0, consumed = 0, posAbs = startAbs;
        for (let bar = firstBar; bar <= lastBar; bar++) {
            const span = spans[bar];
            if (!span) return fail(`Voice ${voice} has no bar ${bar + 1} of its own to write into.`);
            const pieces = [];
            let filled = Math.max(0, Math.min(per, startAbs - bar * per));
            if (filled > 0) pieces.push(`z${filled}`);
            while (filled < per && posAbs < endAbs && noteAt < wanted.length) {
                const n = wanted[noteAt];
                const take = Math.min(n.units - consumed, per - filled);
                const rest = n.midi === null;
                const crosses = take < n.units - consumed;
                pieces.push(`${rest ? 'z' : midiToAbc(n.midi, key)}${take}${!rest && crosses ? '-' : ''}`);
                filled += take; posAbs += take; consumed += take;
                if (consumed >= n.units) { noteAt++; consumed = 0; }
            }
            if (filled < per) pieces.push(`z${per - filled}`);
            // The harmony belongs to the bar, not to the note that happened to carry it.
            const chord = /^\s*("[^"]*")/.exec(abc.slice(span.start, span.end));
            edits.push({ start: span.start, end: span.end, text: (chord ? chord[1] : '') + pieces.join('') });
        }
        for (const e of edits.sort((a, b) => b.start - a.start)) abc = replaceRange(abc, e.start, e.end, e.text);

        const errors = validate(abc).filter(i => i.severity === 'error');
        if (errors.length) return { ok: false, issues: errors };
        edit(abc);
        return { ok: true, bars: [firstBar, lastBar] };
    }

    /** Which bar of the loaded score a timeline position lands on, measured the way syncTime measures it. */
    function barAtTime(seconds) {
        const g = getGrid();
        const perBar = g.secondsPerUnit * g.unitsPerBar;
        if (!(perBar > 0)) return 0;
        const origin = sourceOrigin();
        return Math.max(0, Math.floor((seconds - (origin ?? 0)) / perBar));
    }

    // ===== offline audition =====

    // Derived from this file's own URL: core serves extension files under the extension CLASS name, not
    // the folder name, so a written-out path is one rename away from 404ing every sample.
    const SOUNDFONT_URL = (document.querySelector('script[src*="audio-daw-score.js"]')?.src || '')
        .replace(/audio-daw-score\.js.*$/, 'soundfont/');

    function buildSoundfontRow(parent) {
        const row = createDiv(null, 'daw-stems-action-row');
        els.soundfont = createDiv(null, 'daw-stems-clipinfo');
        row.appendChild(els.soundfont);
        els.soundfontGo = button(row, 'Download samples', 'basic-button btn-sm', fetchSoundfont);
        els.soundfontGo.title = 'Fetch the piano samples once so auditioning works without the internet';
        parent.appendChild(row);
    }

    /** Only a complete set goes local: a missing note would 404 mid-audition instead of playing. */
    function showSoundfont(caps) {
        if (!els.soundfont) return;
        const have = Number(caps?.soundfont_notes ?? 0);
        const total = Number(caps?.soundfont_total ?? 0);
        const complete = total > 0 && have >= total;
        soundFontUrl = complete ? SOUNDFONT_URL : null;
        els.soundfont.textContent = complete
            ? `Instrument samples are installed (${have} notes) — audition works offline.`
            : `Instrument samples come from the internet on first play (${have} of ${total || '88'} installed).`;
        els.soundfontGo.disabled = complete;
        return complete;
    }

    async function fetchSoundfont() {
        els.soundfontGo.disabled = true;
        const busy = cb.busy ? cb.busy('Fetching instrument samples…', 'score') : null;
        // The server answers once, at the end; the capabilities count is what makes the wait legible.
        const poll = setInterval(async () => {
            try {
                const caps = await AudioLabAPI.callAPI('AudioLabScoreCapabilities', {});
                const have = Number(caps?.soundfont_notes ?? 0), total = Number(caps?.soundfont_total ?? 88);
                els.soundfont.textContent = `Fetching instrument samples — ${have} of ${total}…`;
                busy?.setProgress(have / total);
            }
            catch (_) {}
        }, 900);
        try {
            const r = await AudioLabAPI.callAPI('AudioLabFetchSoundfont', {});
            clearInterval(poll);
            const complete = showSoundfont({ soundfont_notes: r?.installed, soundfont_total: r?.total });
            notice(complete
                ? 'Instrument samples installed — auditioning no longer needs the internet'
                : `Only ${r?.installed ?? 0} of ${r?.total ?? 88} samples arrived — audition still uses the remote host`,
                complete ? 'green' : 'yellow');
        }
        catch (e) {
            clearInterval(poll);
            console.error('[AudioDawScore] Soundfont download failed:', e);
            notice('Could not download the samples: ' + e.message, 'yellow');
            els.soundfontGo.disabled = false;
        }
        finally {
            busy?.done();
        }
    }

    // ===== versions =====

    // Two picks at a time: the diff is between exactly two scores, and so is an A/B.
    let picks = [];

    function buildVariantsCard(parent) {
        const card = createDiv(null, 'daw-fx-card');
        const head = createDiv(null, 'daw-fx-card-head');
        const title = createSpan(null, 'daw-fx-card-title');
        title.textContent = 'Variants';
        head.appendChild(title);
        card.appendChild(head);
        els.variantStyles = document.createElement('textarea');
        els.variantStyles.className = 'daw-generate-text';
        els.variantStyles.rows = 3;
        els.variantStyles.placeholder = 'One style per line — each renders this same score into its own track.\n'
            + 'Leave empty to vary only the seed.';
        card.appendChild(els.variantStyles);
        const row = createDiv(null, 'daw-stems-action-row');
        const wrap = createDiv(null, 'daw-score-field');
        const lbl = createSpan(null, 'daw-stems-ctl-label');
        lbl.textContent = 'Seeds';
        els.variantCount = document.createElement('select');
        els.variantCount.className = 'daw-fx-select';
        for (const n of [2, 3, 4]) {
            const o = document.createElement('option');
            o.value = String(n); o.textContent = '\u00D7' + n;
            els.variantCount.appendChild(o);
        }
        els.variantCount.value = '3';
        els.variantCount.title = 'How many takes to render when no style lines are given';
        wrap.appendChild(lbl);
        wrap.appendChild(els.variantCount);
        row.appendChild(wrap);
        els.variantGo = button(row, 'Render variants', 'basic-button btn-sm daw-stems-go', renderVariants);
        card.appendChild(row);
        parent.appendChild(card);
    }

    function buildVersionsCard(parent) {
        const card = createDiv(null, 'daw-fx-card');
        const head = createDiv(null, 'daw-fx-card-head');
        const title = createSpan(null, 'daw-fx-card-title');
        title.textContent = 'Versions';
        head.appendChild(title);
        const btns = createDiv(null, 'daw-fx-card-btns');
        btns.appendChild(miniButton('Unsolo', 'Hear every track again', () => { cb.soloOnly?.(null); }));
        head.appendChild(btns);
        card.appendChild(head);
        els.versions = createDiv(null, 'daw-fx-browser');
        card.appendChild(els.versions);
        els.versionDiff = createDiv(null, 'daw-stems-clipinfo');
        card.appendChild(els.versionDiff);
        els.versionActions = createDiv(null, 'daw-stems-action-row');
        card.appendChild(els.versionActions);
        parent.appendChild(card);
    }

    /** Depth-first over the parent links. A version whose parent is gone is a root of its own. */
    function versionTree(entries) {
        const byId = new Map(entries.map(e => [e.clip.id, e]));
        const children = new Map();
        const roots = [];
        for (const e of entries) {
            const parent = e.clip.meta.score.parent;
            if (parent && byId.has(parent) && parent !== e.clip.id) {
                if (!children.has(parent)) children.set(parent, []);
                children.get(parent).push(e);
            }
            else roots.push(e);
        }
        const out = [];
        const walk = (e, depth) => {
            out.push({ entry: e, depth });
            for (const c of children.get(e.clip.id) || []) walk(c, depth + 1);
        };
        for (const r of roots) walk(r, 0);
        return out;
    }

    function renderVersions() {
        if (!els.versions) return;
        const entries = cb.listScoreClips ? cb.listScoreClips() : [];
        const live = new Set(entries.map(e => e.clip.id));
        picks = picks.filter(id => live.has(id));
        els.versions.innerHTML = '';
        if (!entries.length) {
            const empty = createDiv(null, 'daw-stems-clipinfo');
            empty.textContent = 'No scored clips yet. Render a score and every take lands here as a version.';
            els.versions.appendChild(empty);
        }
        for (const { entry, depth } of versionTree(entries)) {
            const { clip, track } = entry;
            const score = clip.meta.score;
            const pick = createDiv(null, 'daw-fx-pick daw-score-ver' + (picks.includes(clip.id) ? ' selected' : ''));
            pick.style.marginLeft = `${depth * 0.9}rem`;
            const name = createSpan(null, 'daw-fx-pick-name');
            name.textContent = score.label || clip.name || 'Version';
            pick.appendChild(name);
            const meta = createDiv(null, 'daw-clip-card-meta');
            const bits = [score.source || 'rendered', track.name];
            if (score.seed !== null && score.seed !== undefined) bits.push(`seed ${score.seed}`);
            if (score.style) bits.push(score.style.slice(0, 40));
            meta.textContent = bits.join(' · ');
            pick.appendChild(meta);
            pick.title = 'Click to pick for comparison — two picks show the diff and let you A/B them';
            pick.addEventListener('click', () => togglePick(clip.id));
            els.versions.appendChild(pick);
        }
        renderVersionCompare(entries);
    }

    function togglePick(clipId) {
        const at = picks.indexOf(clipId);
        if (at >= 0) picks.splice(at, 1);
        else {
            picks.push(clipId);
            if (picks.length > 2) picks.shift();
        }
        cb.selectClip?.(clipId);
        renderVersions();
    }

    function renderVersionCompare(entries) {
        els.versionDiff.textContent = '';
        els.versionActions.innerHTML = '';
        const chosen = picks.map(id => entries.find(e => e.clip.id === id)).filter(Boolean);
        if (chosen.length !== 2) {
            if (chosen.length === 1) {
                button(els.versionActions, 'Load', 'basic-button btn-sm', () => loadVersion(chosen[0]));
                button(els.versionActions, 'Solo', 'basic-button btn-sm', () => cb.soloOnly?.(chosen[0].track.id));
            }
            return;
        }
        const [a, b] = chosen;
        const d = diffScores(a.clip.meta.score.abc, b.clip.meta.score.abc);
        const parts = [];
        parts.push(d.chords ? `${d.chords} chord symbol${d.chords === 1 ? '' : 's'} differ` : 'same chord symbols');
        parts.push(d.bars ? `${d.bars} bar${d.bars === 1 ? '' : 's'} added or removed` : 'same bar count');
        parts.push(d.notesTouched ? 'the notes moved' : 'every note is identical');
        if (d.headers.length) parts.push(`${d.headers.join(' ')} changed`);
        els.versionDiff.innerHTML = `<strong>${escapeHtml(a.clip.meta.score.label || a.clip.name)}</strong> vs `
            + `<strong>${escapeHtml(b.clip.meta.score.label || b.clip.name)}</strong>: ${escapeHtml(parts.join(' · '))}.`;
        button(els.versionActions, 'Solo A', 'basic-button btn-sm', () => cb.soloOnly?.(a.track.id));
        button(els.versionActions, 'Solo B', 'basic-button btn-sm', () => cb.soloOnly?.(b.track.id));
        button(els.versionActions, 'Load A', 'basic-button btn-sm', () => loadVersion(a));
        button(els.versionActions, 'Load B', 'basic-button btn-sm', () => loadVersion(b));
    }

    function loadVersion(entry) {
        loadScore(entry.clip.meta.score.abc, { ...entry.clip.meta.score, clipId: entry.clip.id });
        notice('Score loaded', 'green');
    }

    // ===== drafting =====

    /** Seconds as m:ss, for a budget a musician reads rather than counts. */
    function clockTime(seconds) {
        const total = Math.max(0, Math.round(seconds));
        return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
    }

    function showBudget(plan) {
        if (!els.budget) return;
        if (!plan) { els.budget.textContent = ''; return; }
        const asked = parseFloat(els.duration.value) || 0;
        const room = Number(plan.budget_seconds ?? plan.budgetSeconds ?? 0);
        const tokens = Number(plan.score_tokens ?? 0);
        let text = `Room for <strong>${clockTime(room)}</strong> of audio`;
        if (tokens > 0) text += ` · ${tokens.toLocaleString()} score tokens`;
        els.budget.innerHTML = text;
        // Below what was asked for means the prompt and score ate the context, which is otherwise
        // indistinguishable from the model simply choosing to end early.
        if (asked > 0 && room > 0 && room < asked - 0.5) {
            els.budget.innerHTML = text
                + ` — less than the ${clockTime(asked)} asked for; shorten the lyrics or the score.`;
        }
    }

    /**
     * Ask the model for a score and nothing else. It writes one before it renders anything, so this is the
     * same first pass a full generation runs — seconds against minutes, and the result is editable.
     */
    async function draftPlan() {
        const style = els.style.value.trim();
        const lyrics = els.lyrics.value.trim();
        if (!style && !lyrics) { notice('Give it a style or some lyrics to plan from', 'yellow'); return; }
        els.draft.disabled = true;
        const busy = cb.busy ? cb.busy('Planning a score…', 'score') : null;
        try {
            const plan = await AudioLabAPI.callAPI('AudioLabPlanScore', {
                provider_id: 'yue2_music',
                style, lyrics,
                duration: Math.max(5, parseFloat(els.duration.value) || 30)
            });
            if (!plan?.abc || !plan.abc.trim()) {
                notice('The model planned no score — check that Score Planning Mode is not off', 'yellow');
                return;
            }
            loadScore(plan.abc, {
                style, lyrics,
                cot: modeForScore(plan.abc),
                source: 'drafted',
                label: style.slice(0, 24) || 'Draft',
                budgetSeconds: plan.budget_seconds
            });
            showBudget(plan);
            notice(plan.truncated
                ? 'Score drafted, but it hit its token ceiling'
                : 'Score drafted', plan.truncated ? 'yellow' : 'green');
        }
        catch (e) {
            console.error('[AudioDawScore] Draft failed:', e);
            notice('Could not plan a score: ' + e.message, 'red');
        }
        finally {
            busy?.done();
            els.draft.disabled = false;
        }
    }

    function stripAllChords() {
        if (!current?.abc.trim()) return;
        // A transcription's melody rendering is the model's own, and stripping quoted text does not reproduce
        // it: a bar carrying a chord cannot fold back into a multi-bar rest, and rests are spelled differently.
        if (current.meta?.melodyAbc && onCanonical()) { showRendering('melody'); return; }
        if (!hasChords(current.abc)) { notice('This score has no chord symbols', 'yellow'); return; }
        edit(stripChords(current.abc));
        notice('Chords stripped — this score now renders in Melody mode', 'green');
    }

    // ===== transcription =====

    // A transcription lands as its own root: both renderings in hand, pinned to the clip they were read off.
    let transcribing = false;

    function buildTranscribeCard(parent) {
        const card = createDiv(null, 'daw-fx-card');
        const head = createDiv(null, 'daw-fx-card-head');
        const title = createSpan(null, 'daw-fx-card-title');
        title.textContent = 'From a recording';
        head.appendChild(title);
        const btns = createDiv(null, 'daw-fx-card-btns');
        els.melodyView = miniButton('Melody', 'Show the melody-only rendering — what a cover is rendered from', () => showRendering('melody'));
        els.fullView = miniButton('Full', 'Show the rendering with chord symbols', () => showRendering('full'));
        btns.appendChild(els.melodyView);
        btns.appendChild(els.fullView);
        head.appendChild(btns);
        card.appendChild(head);

        const row = createDiv(null, 'daw-stems-action-row');
        els.transcribe = button(row, 'Transcribe clip', 'basic-button btn-sm', () => transcribeSelection('clip'));
        els.transcribeStem = button(row, 'Transcribe vocal stem', 'basic-button btn-sm', () => transcribeSelection('stem'));
        els.cover = button(row, 'Cover this clip', 'basic-button btn-sm btn-primary', coverClip);
        const freeLabel = document.createElement('label');
        freeLabel.className = 'daw-score-toggle';
        freeLabel.htmlFor = 'daw_score_unload';
        freeLabel.title = 'Releases every resident audio model, not only SheetSage2 — the engine has no per-model unload';
        els.unloadAfter = document.createElement('input');
        els.unloadAfter.type = 'checkbox';
        els.unloadAfter.id = 'daw_score_unload';
        freeLabel.appendChild(els.unloadAfter);
        freeLabel.appendChild(document.createTextNode(' Free audio models after'));
        row.appendChild(freeLabel);
        card.appendChild(row);

        els.transcribeInfo = createDiv(null, 'daw-stems-clipinfo');
        card.appendChild(els.transcribeInfo);

        const desc = createDiv(null, 'daw-stems-desc');
        desc.textContent = 'SheetSage2 reads the score behind a recording — melody, chords, key, meter and tempo. '
            + 'It comes back in two renderings from one listen: Melody is what a cover is rendered from, Full '
            + 'keeps the harmony. Cover transcribes the clip and renders its melody in the style above. '
            + 'It is about 1.4 GB and stays resident afterwards unless Free audio models after is ticked, '
            + 'which releases every loaded audio model rather than just this one. '
            + 'Weights are CC BY-NC 4.0 — non-commercial use only.';
        card.appendChild(desc);
        parent.appendChild(card);
    }

    /** Switch between the two canonical renderings. Neither is derived from the other: the melody form folds
     *  idle bars into multi-bar rests and spells rests differently, so deleting quoted text cannot produce it. */
    function showRendering(which) {
        const meta = current?.meta;
        const next = which === 'melody' ? meta?.melodyAbc : meta?.fullAbc;
        if (!next) { notice('Transcribe a recording first — both renderings come from the model', 'yellow'); return; }
        if (next === current.abc) { syncRenderingButtons(); return; }
        const edited = !onCanonical();
        edit(next);
        if (edited) notice('Switched renderings — your edits are one undo away', 'yellow');
    }

    /** True while the editor still holds one of the model's own renderings, untouched. */
    function onCanonical() {
        const meta = current?.meta;
        return !!meta && (current.abc === meta.fullAbc || current.abc === meta.melodyAbc);
    }

    function syncRenderingButtons() {
        const meta = current?.meta;
        const both = !!(meta?.melodyAbc && meta?.fullAbc);
        for (const [btn, key] of [[els.melodyView, 'melodyAbc'], [els.fullView, 'fullAbc']]) {
            if (!btn) continue;
            btn.disabled = !both;
            const on = both && meta[key] === current.abc;
            btn.classList.toggle('active', on);
            btn.setAttribute('aria-pressed', on ? 'true' : 'false');
        }
    }

    function showTranscriptInfo(meta) {
        if (!els.transcribeInfo) return;
        if (!meta?.fullAbc) { els.transcribeInfo.textContent = ''; return; }
        const bits = [`${clockTime(meta.duration || 0)} transcribed`];
        if (meta.windowCount > 1) bits.push(`${meta.windowCount} windows stitched`);
        if (meta.sourceName) bits.push(escapeHtml(meta.sourceName));
        els.transcribeInfo.innerHTML = bits.join(' · ') + (meta.truncated
            ? ' — <strong>cut short</strong>: the decoder hit its token ceiling, so the score stops before the '
                + 'audio does. Transcribe the rest as a second section.'
            : '');
    }

    /** What a transcribe button acts on: the selected clip, or the vocal stem the Stems tab made from it. */
    function transcribeTarget(which) {
        const clip = selectedClip?.clip;
        if (!clip?.blob) return null;
        if (which !== 'stem') return { clip, stem: false };
        const found = cb.findVocalStem ? cb.findVocalStem(clip.id) : null;
        return found?.clip?.blob ? { clip: found.clip, stem: true, of: clip.name } : null;
    }

    function syncTranscribeButtons() {
        const clip = selectedClip?.clip;
        const stem = clip?.blob && cb.findVocalStem ? cb.findVocalStem(clip.id) : null;
        if (els.transcribe) {
            els.transcribe.disabled = transcribing || !clip?.blob;
            els.transcribe.title = clip?.blob
                ? `Read the score off ${clip.name}` : 'Select a clip to read the score off';
        }
        if (els.transcribeStem) {
            els.transcribeStem.disabled = transcribing || !stem?.clip?.blob;
            els.transcribeStem.title = stem?.clip?.blob
                ? `Transcribe the vocal stem of ${clip.name} — a cleaner melody than the mix`
                : 'Separate the clip in the Stems tab first, then the vocal alone can be transcribed';
        }
        if (els.cover) {
            els.cover.disabled = transcribing || !clip?.blob;
            els.cover.title = clip?.blob
                ? 'Transcribe this clip and render its melody in the style above'
                : 'Select a clip to cover';
        }
        refreshStartScreen();
    }

    /** Mono 24 kHz 16-bit, which is what the model consumes. decodeAudioData resamples to the context's rate,
     *  so a clip already at 24 kHz passes through untouched rather than being resampled twice. */
    async function toModelWav(blob) {
        if (!cb.encodeWav) throw new Error('The DAW did not hand over an audio encoder');
        const ctx = new OfflineAudioContext(1, 1, TRANSCRIBE_RATE);
        const decoded = await ctx.decodeAudioData(await blob.arrayBuffer());
        let mono = decoded;
        if (decoded.numberOfChannels > 1) {
            mono = ctx.createBuffer(1, decoded.length, decoded.sampleRate);
            const dst = mono.getChannelData(0);
            for (let ch = 0; ch < decoded.numberOfChannels; ch++) {
                const src = decoded.getChannelData(ch);
                for (let i = 0; i < dst.length; i++) dst[i] += src[i] / decoded.numberOfChannels;
            }
        }
        return cb.encodeWav(mono);
    }

    /**
     * Read the score off a recording. One decode returns both renderings, so the Melody/Full toggle costs
     * nothing after this; the decode itself is roughly as long as the audio.
     */
    async function runTranscribe(target, show, which) {
        if (!target) {
            notice(which === 'stem' && selectedClip?.clip?.blob
                ? 'No vocal stem for this clip yet — separate it in the Stems tab first'
                : 'Select a clip to transcribe', 'yellow');
            return null;
        }
        transcribing = true;
        syncTranscribeButtons();
        const busy = cb.busy ? cb.busy(`Transcribing ${target.clip.name}…`, 'score') : null;
        try {
            const wav = await toModelWav(target.clip.blob);
            const result = await AudioLabAPI.callAPI('AudioLabTranscribeScore', {
                audio_data: await AudioLabCore.readAsBase64(wav),
                unload_after: !!els.unloadAfter?.checked
            });
            if (!result?.success) throw new Error(result?.error || 'The transcription came back empty');
            const meta = {
                fullAbc: result.full_abc,
                melodyAbc: result.melody_abc,
                duration: result.duration,
                windowCount: result.window_count,
                truncated: result.truncated,
                clipId: target.clip.id,
                // A transcription that refuses leaves the previous score in place, so the score alone cannot
                // say whether it is the one just asked for.
                at: Date.now(),
                stem: !!target.stem,
                sourceName: target.stem ? `vocal stem of ${target.of}` : target.clip.name,
                source: 'transcribed',
                parent: null,
                label: (target.stem ? `Vocal of ${target.of}` : target.clip.name).slice(0, 32),
                // Keep what is typed above: a transcription says nothing about style or words.
                style: els.style.value, lyrics: els.lyrics.value
            };
            loadScore(show === 'melody' ? meta.melodyAbc : meta.fullAbc, meta);
            notice(result.truncated ? 'Transcribed, but the score stops short of the audio' : 'Score transcribed',
                result.truncated ? 'yellow' : 'green');
            return meta;
        }
        catch (e) {
            console.error('[AudioDawScore] Transcription failed:', e);
            // The overlap-budget refusal names the limit and what to do about it, so it is shown rather
            // than summarised — it means "transcribe a shorter section", not "something broke".
            const message = String(e?.message || e).replace(/^API AudioLabTranscribeScore:\s*/, '');
            if (els.transcribeInfo) els.transcribeInfo.textContent = message;
            notice(message, 'red');
            return null;
        }
        finally {
            busy?.done();
            transcribing = false;
            syncTranscribeButtons();
        }
    }

    function transcribeSelection(which) {
        return runTranscribe(transcribeTarget(which), 'full', which);
    }

    /** A cover is the transcribed melody in a new style. Render derives the mode from the chord content, so
     *  the melody rendering takes the melody path without being told. */
    async function coverClip() {
        const style = els.style.value.trim();
        if (!style) { notice('Describe the style to cover it in first', 'yellow'); return; }
        const target = transcribeTarget('clip');
        if (!target) { notice('Select a clip to cover', 'yellow'); return; }
        const meta = await runTranscribe(target, 'melody');
        if (!meta) return;
        if (validate(current.abc).some(i => i.severity === 'error')) {
            notice('The transcribed score did not validate — fix it before rendering', 'yellow');
            return;
        }
        return runRenders([{ style, label: `Cover: ${style.slice(0, 18)}` }]);
    }

    // ===== analysis =====

    /** Tempo and meter the score is written in, handed to the project's own grid. */
    function applyToProject() {
        const h = parseHeader(current?.abc || '');
        const bpm = parseTempo(h.Q);
        const m = /^(\d+)\s*\/\s*(\d+)$/.exec(h.M || '');
        const meter = m ? [parseInt(m[1], 10), parseInt(m[2], 10)] : null;
        if (!bpm && !meter) { notice('This score says nothing about tempo or meter', 'yellow'); return; }
        if (!cb.setTransport) return;
        cb.setTransport({ bpm: bpm || 0, timeSignature: meter });
        notice(`Project set to ${[bpm ? `${Math.round(bpm)} BPM` : null, meter?.join('/')].filter(Boolean).join(' · ')}`, 'green');
    }

    /** Timeline time the score's first bar sits at — the clip it was read off, played from its own start. */
    function sourceOrigin() {
        const id = current?.meta?.clipId;
        const found = id && cb.findClip ? cb.findClip(id) : null;
        const clip = found?.clip
            || (selectedClip?.clip?.meta?.score?.abc === current?.abc ? selectedClip.clip : null);
        return clip ? (clip.startTime || 0) - (clip.offset || 0) : null;
    }

    /** Bar the first voice is on when a section starts — what a section chip seeks to. */
    function sectionBar(abc, line) {
        const lines = abc.split('\n');
        let inHeader = true, voice = null, bars = 0;
        for (let i = 0; i < Math.min(line, lines.length); i++) {
            const t = lines[i].trim();
            if (inHeader) { if (/^K:/.test(t)) inHeader = false; continue; }
            const v = /^V:\s*(\S+)/.exec(t);
            if (v) { voice = v[1]; continue; }
            if (!t || t.startsWith('%')) continue;
            if (voice === VOICE_IDS[0]) bars += countBars(lines[i]);
        }
        return bars;
    }

    function seekToSection(ev, span, index) {
        const origin = sourceOrigin();
        if (origin === null || !cb.seek) { openSectionMenu(ev, index); return; }
        const g = getGrid();
        const perBar = g.secondsPerUnit * g.unitsPerBar;
        if (!(perBar > 0)) { openSectionMenu(ev, index); return; }
        cb.seek(Math.max(0, origin + sectionBar(current.abc, span.line) * perBar));
    }

    // ===== actions =====

    function onSelection(sel) {
        selectedClip = sel || null;
        if (!els.clipInfo) return;
        // Called from every updateBottomPanel, so adds, deletes and undo all redraw the tree for free.
        renderVersions();
        const score = selectedClip?.clip?.meta?.score;
        els.load.disabled = !score;
        syncTranscribeButtons();
        if (!selectedClip) {
            els.clipInfo.innerHTML = '<strong>No clip selected.</strong> Select a generated clip to load the score it came from.';
        }
        else if (!score) {
            els.clipInfo.innerHTML = `<strong>${escapeHtml(selectedClip.clip.name)}</strong> carries no score — only YuE2 generations plan one.`;
        }
        else {
            els.clipInfo.innerHTML = `<strong>${escapeHtml(selectedClip.clip.name)}</strong> has a planned score${score.truncated ? ' (cut short by the token budget)' : ''}.`;
            // What the context granted the take that produced this clip, so a draft's promise can be read back
            // against the render that used it.
            if (score.budgetSeconds) showBudget({ budget_seconds: score.budgetSeconds });
        }
    }

    function loadFromSelectedClip() {
        const score = selectedClip?.clip?.meta?.score;
        if (!score) { notice('That clip has no score attached', 'yellow'); return; }
        // Pin the clip this came from: without it a render parents itself to whatever is selected at the time,
        // so comparing two versions before rendering would rewrite the tree's edges.
        loadScore(score.abc, { ...score, clipId: selectedClip.clip.id });
        notice('Score loaded', 'green');
    }

    function downloadScore() {
        if (!current?.abc) return;
        const blob = new Blob([current.abc], { type: 'text/plain' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `${(current.meta?.label || 'score').replace(/[^\w-]+/g, '_')}.abc`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    }

    /**
     * Render one score under n sets of words. Every job sends the SAME ABC, so the takes differ only in what
     * was asked around it — which is the only way an A/B between them says anything about the score.
     */
    async function runRenders(jobs) {
        if (!current?.abc.trim() || els.go.disabled) return;
        const model = cb.modelFor ? cb.modelFor('yue2_music') : null;
        if (!model) { notice('No YuE2 model is installed — install one from the Generate tab', 'yellow'); return; }

        const abc = current.abc;
        const mode = modeForScore(abc);
        const lyrics = els.lyrics.value.trim();
        // Only the clip a score was LOADED from is its parent. Falling back to the selection would parent a
        // drafted score under whatever was clicked last — including a click in the Versions panel.
        const parent = current.meta?.clipId ?? null;

        els.go.disabled = true;
        els.variantGo.disabled = true;
        const busy = cb.busy
            ? cb.busy(jobs.length > 1 ? `Rendering ${jobs.length} variants…` : 'Rendering the score…', 'score')
            : null;
        let finished = 0;
        try {
            const takes = await Promise.all(jobs.map(job => cb.generate({
                model,
                prompt: lyrics,
                params: {
                    songscoreabc: abc,
                    scoreplanningmode: mode,
                    text2audiostyle: job.style,
                    ...(job.seed === undefined ? {} : { seed: job.seed })
                },
                onProgress: (frac) => { if (jobs.length === 1) busy?.setProgress(frac); }
            })
                .then(take => ({ ...take, job }))
                .catch(e => { console.error('[AudioDawScore] Render failed:', e); return { error: e, job }; })
                .then(r => { finished++; if (jobs.length > 1) busy?.setProgress(finished / jobs.length); return r; })));

            // Landed after every take is in hand, in the order asked for, as one undo step.
            let added = 0;
            for (const take of takes) {
                if (take.error) continue;
                await cb.addRenderedScore({
                    blob: take.blob, metadata: take.metadata, label: take.job.label,
                    snapshot: added === 0,
                    score: {
                        abc, style: take.job.style, lyrics, cot: mode, model,
                        engineId: 'yue2_music',
                        seed: take.job.seed ?? null,
                        parent, source: 'rendered'
                    }
                });
                added++;
            }
            const failed = takes.length - added;
            if (!added) notice('Render failed: ' + (takes[0]?.error?.message || 'no audio came back'), 'red');
            else notice(failed
                ? `${added} of ${takes.length} rendered — ${failed} failed`
                : added > 1 ? `${added} variants rendered into their own tracks` : 'Score rendered into a new track',
                failed ? 'yellow' : 'green');
        }
        finally {
            busy?.done();
            els.go.disabled = false;
            els.variantGo.disabled = false;
        }
    }

    function renderScore() {
        const style = els.style.value.trim();
        return runRenders([{ style, label: style.slice(0, 24) || current.meta?.label || 'Score render' }]);
    }

    /** One score against n style lines, or against n seeds when no lines are given. */
    function renderVariants() {
        if (!current?.abc.trim()) { notice('Load or draft a score first', 'yellow'); return; }
        const seed = () => Math.floor(Math.random() * 1e9);
        const lines = els.variantStyles.value.split('\n').map(l => l.trim()).filter(Boolean);
        const baseStyle = els.style.value.trim();
        const jobs = lines.length
            ? lines.map(style => ({ style, seed: seed(), label: style.slice(0, 24) }))
            : Array.from({ length: parseInt(els.variantCount.value, 10) || 3 }, (_, i) => ({
                style: baseStyle, seed: seed(), label: `${baseStyle.slice(0, 18) || 'Take'} ${i + 1}`
            }));
        if (jobs.length === 1 && !lines.length) { notice('Ask for at least two takes, or give a style per line', 'yellow'); return; }
        return runRenders(jobs);
    }

    return {
        render, onSelection, loadScore, undo, redo, syncTime, stopPlan, draftPlan,
        transcribeSelection, coverClip, showRendering, applyToProject,
        // exported for the DAW, for tests, and for later phases
        // the instrument contract: write into the score, and the grid and key to quantize against
        insertNotes, getKey, getGrid, barAtTime,
        // the DAW hands a dragged clip to the sheet, since its clip drag is pointer-based
        clipDragOver, clipDropped, moveSection,
        validate, hasChords, stripChords, modeForScore, prepareForEngraving, toOriginal,
        alignBars, barSegments, voiceLineBlocks,
        parseHeader, scanBody, countBars, chunkBlocks,
        splitElement, transposeToken, pitchIndex, pitchToken, tokenText,
        unitsPerBar, barBounds, barTokens, sectionSpans, numbersToAbc,
        keyAccidentals, midiToAbc, voiceBars, blankScore,
        _state: () => ({ abc: current?.abc || '', selection, undoDepth: history.undo.length,
            meta: current?.meta || null, transcribing }),
        _soundfontUrl: () => soundFontUrl,
        _transport: () => synthCtl
    };
})();
