/**
 * Audio Lab DAW: lyrics and word timing.
 *
 * A clip carries its lyrics as `clip.meta.lyrics = { text, words: [{ w, s, e, line }], source, model }`. `text` is what the
 * user sees and edits (section tags like `[Verse 1]` allowed); `words` are the timed words, with `s` and `e` in seconds of the
 * clip's SOURCE audio and `line` the index of the line (of `linesOf(text)`) a word belongs to. Source time means a clip that is
 * moved, trimmed or split keeps valid word times: a word sits on the timeline at `clip.startTime + (s - clip.offset)`.
 *
 * This module is pure logic over plain objects: no DOM, no DAW state, no audio, no network. The shell and the lyrics view ask
 * it to build, map, search and convert lyrics; it can be exercised from a browser console: `AudioDawLyrics.linesOf('[Verse]\na b')`.
 */
const AudioDawLyrics = (() => {
    'use strict';

    /** A line that is only a section tag, like "[Chorus]". */
    const TAG_LINE = /^\s*\[[^\]]*\]\s*$/;

    /** A pause between transcribed words, in seconds, that starts a new line. */
    const LINE_PAUSE = 0.8;

    /** Most words on one line when a transcript is broken into lines by length. */
    const LINE_WORDS = 8;

    /** Longest a word is stretched when timing is only estimated, in seconds. */
    const MAX_ESTIMATED_WORD = 1.2;

    /** Round to milliseconds, so stored times stay short and stable. */
    function ms(t) {
        return Math.round(t * 1000) / 1000;
    }

    /** The lines of a lyric text with tag lines and empty lines removed, each trimmed. */
    function linesOf(text) {
        return String(text || '').split(/\r?\n/).filter(l => l.trim() && !TAG_LINE.test(l)).map(l => l.trim());
    }

    /** The words of a lyric text, split on whitespace (what the aligner sees). */
    function wordsOf(text) {
        return linesOf(text).join(' ').split(/\s+/).filter(Boolean);
    }

    /** A word with its punctuation and case removed, for comparing texts. */
    function core(word) {
        return String(word).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
    }

    /** A new lyrics object. `words` are `{ w, s, e, line }` in source seconds. */
    function create({ text = '', words = [], source = 'manual', model = '' } = {}) {
        return { text: String(text), words: words.map(w => ({ ...w })), source, model };
    }

    /** A deep copy. */
    function copy(lyrics) {
        return lyrics ? create(lyrics) : null;
    }

    /** Whether any word has a time (the lyrics can drive the strip). */
    function isTimed(lyrics) {
        return !!lyrics && lyrics.words.length > 0;
    }

    /**
     * Give each word the index of the line it sits on. A word is looked up in the text after the previous one, so a repeated word
     * ("la la la") lands where it was written; a word that cannot be found keeps the previous word's line.
     * @param {Array<{w: string}>} words - in order
     * @param {string[]} lines
     */
    function assignLines(words, lines) {
        const text = lines.join('\n');
        const starts = [];
        let at = 0;
        for (const l of lines) {
            starts.push(at);
            at += l.length + 1;
        }
        let pointer = 0;
        let last = 0;
        for (const word of words) {
            const found = text.indexOf(word.w, pointer);
            if (found >= 0) {
                let line = 0;
                while (line + 1 < starts.length && starts[line + 1] <= found) line++;
                last = line;
                pointer = found + word.w.length;
            }
            word.line = last;
        }
        return words;
    }

    /**
     * Lyrics from an alignment of known text: the API's `[{ word, start, end }]` in seconds from the start of the audio that was
     * sent. `offset` is where that audio began in the clip's source, so times become source seconds.
     */
    function fromAlignment(text, apiWords, { offset = 0, source = 'aligned', model = '' } = {}) {
        const words = [];
        for (const x of apiWords || []) {
            const w = String(x.word || '').trim();
            if (!w || !Number.isFinite(x.start) || !Number.isFinite(x.end)) continue;
            words.push({ w, s: ms(x.start + offset), e: ms(Math.max(x.end, x.start) + offset), line: 0 });
        }
        assignLines(words, linesOf(text));
        return create({ text, words, source, model });
    }

    /**
     * Lyrics from a transcript that has no line breaks: a new line starts after a pause or after `LINE_WORDS` words.
     * The text is rebuilt from the lines.
     */
    function fromTranscript(apiWords, { offset = 0, source = 'stt', model = '' } = {}) {
        const words = [];
        let line = 0;
        let inLine = 0;
        for (const x of apiWords || []) {
            const w = String(x.word || '').trim();
            if (!w || !Number.isFinite(x.start) || !Number.isFinite(x.end)) continue;
            const prev = words[words.length - 1];
            const gap = prev ? x.start + offset - prev.e : 0;
            if (prev && (gap > LINE_PAUSE || inLine >= LINE_WORDS)) {
                line++;
                inLine = 0;
            }
            words.push({ w, s: ms(x.start + offset), e: ms(Math.max(x.end, x.start) + offset), line });
            inLine++;
        }
        const lines = [];
        for (const word of words) (lines[word.line] ||= []).push(word.w);
        return create({ text: lines.map(l => l.join(' ')).join('\n'), words, source, model });
    }

    /**
     * Words timed by estimate: each line of `text` is given an equal share of `[start, end]` (or the time until the next
     * line when `lineStarts` has it), and its words split that share by length. Used when no aligner is available and for
     * lyric files that time only the lines. Times are in the same seconds as `start` and `end`.
     * @param {string} text
     * @param {number} start
     * @param {number} end
     * @param {number[]} [lineStarts] - start time of each line (same order as `linesOf(text)`), when known
     */
    function estimate(text, start, end, lineStarts = null) {
        const lines = linesOf(text);
        const words = [];
        if (!lines.length || !(end > start)) return words;
        for (let i = 0; i < lines.length; i++) {
            const lineStart = lineStarts?.[i] ?? start + (end - start) * i / lines.length;
            const lineEnd = lineStarts?.[i + 1] ?? (lineStarts ? end : start + (end - start) * (i + 1) / lines.length);
            const parts = lines[i].split(/\s+/).filter(Boolean);
            const weight = parts.reduce((n, p) => n + p.length + 1, 0);
            const span = Math.max(0.01, Math.min(lineEnd - lineStart, parts.length * MAX_ESTIMATED_WORD));
            let at = lineStart;
            for (const p of parts) {
                const len = span * (p.length + 1) / weight;
                words.push({ w: p, s: ms(at), e: ms(at + len), line: i });
                at += len;
            }
        }
        return words;
    }

    /** Replace the text, keeping the word times when only spacing, case, punctuation or tags changed; otherwise they are dropped. */
    function setText(lyrics, text) {
        const next = create({ ...lyrics, text });
        const was = lyrics.words.map(w => core(w.w));
        const now = wordsOf(text).map(core);
        const same = was.length === now.length && was.every((c, i) => c === now[i]);
        if (!same) {
            next.words = [];
            next.source = lyrics.source === 'generated' ? 'generated' : 'manual';
            return next;
        }
        const parts = wordsOf(text);
        next.words = lyrics.words.map((w, i) => ({ ...w, w: parts[i] }));
        assignLines(next.words, linesOf(text));
        return next;
    }

    /** Move every word by `dt` seconds (never before 0). */
    function shift(lyrics, dt) {
        const first = lyrics.words.reduce((m, w) => Math.min(m, w.s), Infinity);
        const d = Number.isFinite(first) ? Math.max(dt, -first) : 0;
        return create({ ...lyrics, words: lyrics.words.map(w => ({ ...w, s: ms(w.s + d), e: ms(w.e + d) })) });
    }

    /**
     * The lyrics for audio that starts `dt` seconds away in the source: every time moves by `dt`, words that end before the
     * new start are dropped and one that straddles it is cut to start at 0. A stem cut from a clip's visible part starts at
     * 0, so its lyrics are `rebase(lyrics, -clip.offset)`.
     */
    function rebase(lyrics, dt) {
        const words = [];
        for (const w of lyrics.words) {
            const e = ms(w.e + dt);
            if (e <= 0) continue;
            words.push({ ...w, s: Math.max(0, ms(w.s + dt)), e });
        }
        return create({ ...lyrics, words });
    }

    /** Change one word's start and/or end, keeping start <= end and the word out of its neighbours. */
    function setWordTime(lyrics, index, { s, e } = {}) {
        const words = lyrics.words.map(w => ({ ...w }));
        const w = words[index];
        if (!w) return lyrics;
        const prev = words[index - 1];
        const next = words[index + 1];
        const lo = prev ? prev.s : 0;
        const hi = next ? next.e : Infinity;
        if (Number.isFinite(s)) w.s = ms(Math.min(Math.max(s, lo), w.e));
        if (Number.isFinite(e)) w.e = ms(Math.max(Math.min(e, hi), w.s));
        return create({ ...lyrics, words });
    }

    /**
     * The clip's timed words as they sit on the timeline: each `{ w, start, end, line, index }`, for the words that overlap the
     * clip's visible window. `index` is the word's place in `clip.meta.lyrics.words`.
     */
    function placed(clip) {
        const lyrics = clip?.meta?.lyrics;
        if (!isTimed(lyrics)) return [];
        const offset = clip.offset || 0;
        const visible = (clip.duration || 0) - offset - (clip.trimEnd || 0);
        const out = [];
        lyrics.words.forEach((word, index) => {
            if (word.e <= offset || word.s >= offset + visible) return;
            const start = clip.startTime + (word.s - offset);
            out.push({ w: word.w, start, end: clip.startTime + (word.e - offset), line: word.line, index });
        });
        return out;
    }

    /**
     * The whole project's lyrics laid out for the strip. `clips` is a list of clips; muted clips are skipped. The result is
     * `{ words, lines }`: `words` sorted by start (each with `lineKey` and `clipId`), and `lines` sorted by start, each
     * `{ key, clipId, start, end, first, last }` with `first` and `last` indexing `words`.
     */
    function buildTimeline(clips) {
        const words = [];
        for (const clip of clips || []) {
            if (clip.muted) continue;
            for (const p of placed(clip)) {
                words.push({ ...p, clipId: clip.id, lineKey: `${clip.id}:${p.line}` });
            }
        }
        words.sort((a, b) => a.start - b.start || a.end - b.end);
        const lines = [];
        const byKey = new Map();
        words.forEach((word, i) => {
            let line = byKey.get(word.lineKey);
            if (!line) {
                line = { key: word.lineKey, clipId: word.clipId, start: word.start, end: word.end, first: i, last: i };
                byKey.set(word.lineKey, line);
                lines.push(line);
            }
            line.start = Math.min(line.start, word.start);
            line.end = Math.max(line.end, word.end);
            line.last = i;
        });
        lines.sort((a, b) => a.start - b.start);
        return { words, lines };
    }

    /**
     * Which lines the strip shows at `time`: the line being sung (held for `hold` seconds after its last word) and the one after.
     * @returns {{current: number, next: number}} indexes into `timeline.lines`, -1 for none
     */
    function lineAt(timeline, time, hold = 1.5) {
        const lines = timeline.lines;
        let lo = 0;
        let hi = lines.length - 1;
        let at = -1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (lines[mid].start <= time) {
                at = mid;
                lo = mid + 1;
            }
            else {
                hi = mid - 1;
            }
        }
        if (at >= 0 && time <= lines[at].end + hold) {
            return { current: at, next: at + 1 < lines.length ? at + 1 : -1 };
        }
        return { current: -1, next: at + 1 < lines.length ? at + 1 : -1 };
    }

    /** How much of each word of a line is sung at `time`, 0 to 1, in the line's word order. */
    function fillOf(timeline, line, time) {
        const out = [];
        for (let i = line.first; i <= line.last; i++) {
            const word = timeline.words[i];
            if (word.lineKey !== line.key) continue;
            const span = word.end - word.start;
            out.push(span > 0 ? Math.min(1, Math.max(0, (time - word.start) / span)) : (time >= word.start ? 1 : 0));
        }
        return out;
    }

    /** The words of a line, in order. */
    function wordsOfLine(timeline, line) {
        const out = [];
        for (let i = line.first; i <= line.last; i++) {
            if (timeline.words[i].lineKey === line.key) out.push(timeline.words[i]);
        }
        return out;
    }

    /** "mm:ss.xx" for LRC, from seconds. */
    function stamp(t, wordLevel = false) {
        const total = Math.max(0, t);
        const m = Math.floor(total / 60);
        const s = total - m * 60;
        const text = `${String(m).padStart(2, '0')}:${s.toFixed(2).padStart(5, '0')}`;
        return wordLevel ? `<${text}>` : `[${text}]`;
    }

    /** The timeline as an LRC file: a `[mm:ss.xx]` line per lyric line, with `<mm:ss.xx>` before each word when `words` is set. */
    function toLrc(timeline, { words = false } = {}) {
        return timeline.lines.map(line => {
            const parts = wordsOfLine(timeline, line);
            const body = words ? parts.map(p => `${stamp(p.start, true)}${p.w}`).join(' ') : parts.map(p => p.w).join(' ');
            return `${stamp(line.start)}${body}`;
        }).join('\n');
    }

    /** "hh:mm:ss,mmm" for SRT. */
    function srtStamp(t) {
        const total = Math.max(0, t);
        const h = Math.floor(total / 3600);
        const m = Math.floor((total % 3600) / 60);
        const s = Math.floor(total % 60);
        const milli = Math.round((total - Math.floor(total)) * 1000);
        const carry = milli === 1000;
        return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(carry ? s + 1 : s).padStart(2, '0')},${String(carry ? 0 : milli).padStart(3, '0')}`;
    }

    /** The timeline as an SRT file, one cue per lyric line. */
    function toSrt(timeline) {
        return timeline.lines.map((line, i) => {
            const text = wordsOfLine(timeline, line).map(p => p.w).join(' ');
            return `${i + 1}\n${srtStamp(line.start)} --> ${srtStamp(line.end)}\n${text}\n`;
        }).join('\n');
    }

    /**
     * Read an LRC file into `[{ time, text }]` sorted by time. Metadata tags (`[ar:...]`) are skipped, a line with several
     * timestamps becomes several entries, and word-level `<mm:ss.xx>` marks are removed from the text.
     */
    function parseLrc(text) {
        const out = [];
        for (const raw of String(text || '').split(/\r?\n/)) {
            const stamps = [];
            let rest = raw;
            for (;;) {
                const m = rest.match(/^\s*\[(\d+):(\d{1,2}(?:[.:]\d{1,3})?)\]/);
                if (!m) break;
                stamps.push(parseInt(m[1], 10) * 60 + parseFloat(m[2].replace(':', '.')));
                rest = rest.slice(m[0].length);
            }
            const body = rest.replace(/<\d+:\d{1,2}(?:[.:]\d{1,3})?>/g, '').trim();
            if (!stamps.length || !body) continue;
            for (const time of stamps) out.push({ time, text: body });
        }
        return out.sort((a, b) => a.time - b.time);
    }

    /**
     * Lyrics from an LRC file, timed by estimate: each line runs to the next line's start, the last one for `lastLine` seconds.
     * `toSource` turns a timeline second into a source second (the clip's start and offset).
     */
    function fromLrc(lrc, { toSource = (t) => t, lastLine = 4 } = {}) {
        const entries = parseLrc(lrc);
        if (!entries.length) return null;
        const text = entries.map(e => e.text).join('\n');
        const starts = entries.map(e => toSource(e.time));
        const end = starts[starts.length - 1] + lastLine;
        const words = estimate(text, starts[0], end, starts);
        return create({ text, words, source: 'lrc' });
    }

    /** Plain copy carrying exactly the persisted fields, tolerating anything a saved project might hold. */
    function restore(raw) {
        if (!raw || typeof raw !== 'object') return null;
        const words = [];
        for (const x of Array.isArray(raw.words) ? raw.words : []) {
            if (!x || typeof x.w !== 'string' || !x.w) continue;
            const s = Number(x.s);
            const e = Number(x.e);
            if (!Number.isFinite(s) || !Number.isFinite(e)) continue;
            words.push({ w: x.w, s: Math.max(0, s), e: Math.max(s, e), line: Number.isInteger(x.line) && x.line >= 0 ? x.line : 0 });
        }
        return create({
            text: typeof raw.text === 'string' ? raw.text : words.map(w => w.w).join(' '),
            words,
            source: typeof raw.source === 'string' ? raw.source : 'manual',
            model: typeof raw.model === 'string' ? raw.model : ''
        });
    }

    return {
        linesOf, wordsOf, create, copy, isTimed, assignLines, fromAlignment, fromTranscript, estimate, setText, shift,
        setWordTime, rebase, placed, buildTimeline, lineAt, fillOf, wordsOfLine, toLrc, toSrt, parseLrc, fromLrc, restore
    };
})();
