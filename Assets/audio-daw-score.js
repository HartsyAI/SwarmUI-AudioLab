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
    const EMPTY_HINT = 'No score loaded. Generate a song with YuE2 and press Load from clip, or paste a score below.';

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
    let synth = null, timing = null;
    let highlighted = [], lastSystemTop = null, lastTimingIndex = -1;

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
        els.play = miniButton('Play', 'Hear the plan in the browser before spending a render on it', playPlan);
        els.stop = miniButton('Stop', 'Stop the audition', stopPlan);
        els.stop.disabled = true;
        const chordsLabel = document.createElement('label');
        chordsLabel.className = 'daw-score-toggle';
        chordsLabel.title = 'Play the chord symbols as accompaniment';
        els.chords = document.createElement('input');
        els.chords.type = 'checkbox';
        els.chords.checked = true;
        chordsLabel.appendChild(els.chords);
        chordsLabel.appendChild(document.createTextNode(' Chords'));
        btns.appendChild(els.play);
        btns.appendChild(els.stop);
        btns.appendChild(chordsLabel);
        els.undo = miniButton('Undo', 'Undo the last score edit (Ctrl+Z while the score has focus)', undo);
        els.redo = miniButton('Redo', 'Redo (Ctrl+Y)', redo);
        btns.appendChild(els.undo);
        btns.appendChild(els.redo);
        btns.appendChild(miniButton('No chords', 'Strip every chord symbol — the melody-only form used for covers', stripAllChords));
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
        card.appendChild(els.sheet);
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
        els.key = labelledInput(hdrRow, 'Key', 'daw-generate-reftext', v => applyEdit(setHeaderField(current.abc, 'K', v)));
        els.meter = labelledInput(hdrRow, 'Meter', 'daw-generate-reftext', v => applyEdit(setHeaderField(current.abc, 'M', v)));
        els.tempo = labelledInput(hdrRow, 'Tempo', 'daw-generate-reftext', v => {
            const bpm = parseFloat(v);
            if (!isFinite(bpm) || bpm <= 0) { syncControls(); return; }
            applyEdit(setHeaderField(current.abc, 'Q', `1/4=${Math.round(bpm)}`));
        });
        els.unit = labelledInput(hdrRow, 'Unit', 'daw-generate-reftext', null);
        els.unit.readOnly = true;
        els.unit.title = 'Unit note length (L:). The grid every duration is measured in.';
        parent.appendChild(hdrRow);

        els.sections = createDiv(null, 'daw-fx-browser');
        parent.appendChild(els.sections);

        parent.appendChild(fieldLabel('Style'));
        els.style = document.createElement('textarea');
        els.style.className = 'daw-generate-text';
        els.style.rows = 2;
        els.style.placeholder = 'Genre, instruments, mood — what the recording should sound like.';
        parent.appendChild(els.style);

        parent.appendChild(fieldLabel('Lyrics'));
        els.lyrics = document.createElement('textarea');
        els.lyrics.className = 'daw-generate-text';
        els.lyrics.rows = 3;
        els.lyrics.placeholder = '[Verse]\nThe words to sing. Leave empty for an instrumental.';
        parent.appendChild(els.lyrics);

        // Numbered notation: how the YuE2 researchers dictated a melody correction ("1155665 / 4433221").
        parent.appendChild(fieldLabel('Melody by degree'));
        const numRow = createDiv(null, 'daw-stems-action-row');
        els.numbers = document.createElement('input');
        els.numbers.type = 'text';
        els.numbers.className = 'daw-generate-reftext';
        els.numbers.placeholder = '1155665 / 4433221';
        els.numbers.title = 'Scale degrees in the current key. Spaces or / start a new bar, 0 is a rest.';
        numRow.appendChild(els.numbers);
        els.numberOctave = document.createElement('select');
        els.numberOctave.className = 'daw-fx-select';
        for (const [v, l] of [['0', 'Low'], ['1', 'Mid'], ['2', 'High']]) {
            const o = document.createElement('option');
            o.value = v; o.textContent = l;
            els.numberOctave.appendChild(o);
        }
        els.numberOctave.value = '1';
        numRow.appendChild(els.numberOctave);
        button(numRow, 'Write bars', 'basic-button btn-sm', insertNumbered);
        parent.appendChild(numRow);

        parent.appendChild(fieldLabel('ABC score'));
        els.src = document.createElement('textarea');
        els.src.className = 'daw-generate-text daw-score-src';
        els.src.rows = 8;
        els.src.spellcheck = false;
        els.src.placeholder = EMPTY_HINT;
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

        buildLlmCard(parent);

        const help = createDiv(null, 'daw-stems-desc');
        help.textContent = 'Click a chord symbol to reharmonise, a note to edit it, or drag a note up and down '
            + 'to change its pitch. Play auditions the plan in the browser — instrument samples are fetched '
            + 'from the internet the first time and cached by it. The score is a plan the model performs, '
            + 'not a recording of it.';
        parent.appendChild(help);
    }

    function fieldLabel(text) {
        const l = createDiv(null, 'daw-stems-ctl-label');
        l.textContent = text;
        return l;
    }

    function labelledInput(row, label, cls, onCommit) {
        const wrap = createDiv(null, 'daw-score-field');
        const l = createSpan(null, 'daw-stems-ctl-label');
        l.textContent = label;
        const input = document.createElement('input');
        input.type = 'text';
        input.className = cls;
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
        sectionSpans(abc).forEach((s, i) => {
            const chip = createDiv(null, 'daw-fx-pick daw-score-chip');
            const name = createSpan(null, 'daw-fx-pick-name');
            name.textContent = s.label || '(unnamed)';
            chip.appendChild(name);
            chip.title = 'Rename, duplicate, reorder or delete this section';
            chip.addEventListener('click', (e) => openSectionMenu(e, i));
            els.sections.appendChild(chip);
        });

        const mode = abc.trim() ? modeForScore(abc) : null;
        els.modeNote.textContent = mode === null ? ''
            : mode === 'full'
                ? 'This score carries chord symbols, so it renders in Full planning mode.'
                : 'This score has no chord symbols, so it renders in Melody mode — the cover setting.';
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
            host.innerHTML = `<span class="daw-stems-clipinfo">${escapeHtml(EMPTY_HINT)}</span>`;
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
            if (visual) new ABCJS.TimingCallbacks(visual, { eventCallback: () => {} });
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
        if (cb.showMenu) cb.showMenu(ev, items);
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
        if (index > 0) items.push({ label: 'Move earlier', action: () => edit(withSections(current.abc, (lines, sp) => {
            const prev = sp[index - 1];
            const block = lines.slice(s.line, s.endLine + 1);
            lines.splice(s.line, block.length);
            lines.splice(prev.line, 0, ...block);
            return lines;
        })) });
        if (index < spans.length - 1) items.push({ label: 'Move later', action: () => edit(withSections(current.abc, (lines, sp) => {
            const next = sp[index + 1];
            const block = lines.slice(s.line, s.endLine + 1);
            const nextBlock = lines.slice(next.line, next.endLine + 1);
            lines.splice(s.line, block.length + nextBlock.length, ...nextBlock, ...block);
            return lines;
        })) });
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
     * Play the plan in the browser. abcjs comps the quoted chords automatically, so this is the fastest way to
     * hear whether a reharmonisation works — seconds, against minutes for a real render.
     */
    async function playPlan() {
        if (!visual || !ABCJS.synth.supportsAudio()) {
            notice('This browser cannot play audio here', 'yellow');
            return;
        }
        stopPlan();
        els.play.disabled = true;
        try {
            synth = new ABCJS.synth.CreateSynth();
            await synth.init({ visualObj: visual, options: { chordsOff: !els.chords.checked } });
            await synth.prime();
            timing = new ABCJS.TimingCallbacks(visual, {
                eventCallback: (ev) => { highlightTiming(ev); return ev ? undefined : 'continue'; }
            });
            synth.start();
            timing.start();
            els.stop.disabled = false;
        }
        catch (e) {
            console.error('[AudioDawScore] Audition failed:', e);
            notice('Could not play the plan — the instrument samples could not be fetched', 'yellow');
            stopPlan();
        }
        finally {
            els.play.disabled = false;
        }
    }

    function stopPlan() {
        try { timing?.stop(); } catch (_) {}
        try { synth?.stop(); } catch (_) {}
        timing = null; synth = null;
        clearHighlight();
        if (els.stop) els.stop.disabled = true;
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
        if (timing || !visual?.noteTimings?.length || !els.sheet?.isConnected) return;
        const clip = selectedClip?.clip;
        if (!clip || !current?.meta || clip.meta?.score?.abc !== current.abc) return;
        const rel = seconds - (clip.startTime || 0) + (clip.offset || 0);
        if (rel < 0) { clearHighlightOnly(); return; }
        const ms = rel * 1000;
        const timings = visual.noteTimings;
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
        if (!hasChords(current.abc)) { notice('This score has no chord symbols', 'yellow'); return; }
        edit(stripChords(current.abc));
        notice('Chords stripped — this score now renders in Melody mode', 'green');
    }

    // ===== actions =====

    function onSelection(sel) {
        selectedClip = sel || null;
        if (!els.clipInfo) return;
        const score = selectedClip?.clip?.meta?.score;
        els.load.disabled = !score;
        if (!selectedClip) {
            els.clipInfo.innerHTML = '<strong>No clip selected.</strong> Select a generated clip to load the score it came from.';
        }
        else if (!score) {
            els.clipInfo.innerHTML = `<strong>${escapeHtml(selectedClip.clip.name)}</strong> carries no score — only YuE2 generations plan one.`;
        }
        else {
            els.clipInfo.innerHTML = `<strong>${escapeHtml(selectedClip.clip.name)}</strong> has a planned score${score.truncated ? ' (cut short by the token budget)' : ''}.`;
        }
    }

    function loadFromSelectedClip() {
        const score = selectedClip?.clip?.meta?.score;
        if (!score) { notice('That clip has no score attached', 'yellow'); return; }
        loadScore(score.abc, score);
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

    async function renderScore() {
        if (!current?.abc.trim() || els.go.disabled) return;
        const model = cb.modelFor ? cb.modelFor('yue2_music') : null;
        if (!model) { notice('No YuE2 model is installed — install one from the Generate tab', 'yellow'); return; }

        const abc = current.abc;
        const mode = modeForScore(abc);
        const style = els.style.value.trim();
        const lyrics = els.lyrics.value.trim();
        const label = (style.slice(0, 24) || current.meta?.label || 'Score render');

        els.go.disabled = true;
        const busy = cb.busy ? cb.busy('Rendering the score…', 'score') : null;
        try {
            const { blob, metadata } = await cb.generate({
                model,
                prompt: lyrics,
                params: {
                    songscoreabc: abc,
                    scoreplanningmode: mode,
                    text2audiostyle: style
                },
                onProgress: (frac) => busy?.setProgress(frac)
            });
            await cb.addRenderedScore({
                blob, metadata, label,
                score: {
                    abc, style, lyrics, cot: mode, model,
                    engineId: 'yue2_music',
                    parent: current.meta?.clipId || selectedClip?.clip?.id || null,
                    source: 'rendered'
                }
            });
            notice('Score rendered into a new track', 'green');
        }
        catch (e) {
            console.error('[AudioDawScore] Render failed:', e);
            notice('Render failed: ' + e.message, 'red');
        }
        finally {
            busy?.done();
            els.go.disabled = false;
        }
    }

    return {
        render, onSelection, loadScore, undo, redo, syncTime, stopPlan, draftPlan,
        // exported for the DAW, for tests, and for later phases
        validate, hasChords, stripChords, modeForScore, prepareForEngraving, toOriginal,
        parseHeader, scanBody, countBars, chunkBlocks,
        splitElement, transposeToken, pitchIndex, pitchToken, tokenText,
        unitsPerBar, barBounds, barTokens, sectionSpans, numbersToAbc,
        _state: () => ({ abc: current?.abc || '', selection, undoDepth: history.undo.length })
    };
})();
