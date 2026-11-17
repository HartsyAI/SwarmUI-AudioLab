/**
 * Audio Lab DAW: arrangement sections.
 *
 * A section is a named, colored span of the song (Intro, Verse, Drop...). This module is pure logic over plain
 * arrays: no DOM and no DAW state, so the ruler row, the lane bands, the menus and any later feature (section-aware
 * naming, an overview strip) share one set of rules, and the rules can be exercised from a browser console:
 * `AudioDawSections.constrain([...], id, start, end, 'move')`.
 *
 * Sections never overlap. A list kept through `normalize` is sorted by start, every section is at least MIN_LENGTH
 * long, and each one ends at or before the next one starts. Times are seconds, as everywhere in the DAW.
 *
 * Section shape: { id, start, end, name, color }
 */
const AudioDawSections = (() => {
    'use strict';

    /** The shortest a section may be, in seconds. */
    const MIN_LENGTH = 0.1;

    /** Colors handed out in turn to new sections, light enough to read dark text on and distinct side by side. */
    const PALETTE = [
        '#f4a261', '#f2c94c', '#b5d63d', '#5fcf80', '#2ec4b6',
        '#4cc9f0', '#4895ef', '#9d7bf0', '#d97ae0', '#ef6f91'
    ];

    let seq = 0;

    /** A new unique id. */
    function newId() {
        return `section-${Date.now().toString(36)}-${++seq}`;
    }

    /** The palette color for the nth section (wraps). */
    function colorFor(index) {
        const n = Number.isFinite(index) ? Math.floor(index) : 0;
        return PALETTE[((n % PALETTE.length) + PALETTE.length) % PALETTE.length];
    }

    /** A copy of the list sorted by start. */
    function sorted(list) {
        return (list || []).slice().sort((a, b) => a.start - b.start);
    }

    /** Whether a value is a usable section: finite times, start >= 0, at least MIN_LENGTH long. */
    function isValid(s) {
        return !!s && Number.isFinite(s.start) && Number.isFinite(s.end) && s.start >= 0 && s.end - s.start >= MIN_LENGTH - 1e-9;
    }

    /**
     * Make a list well-formed: drop invalid sections, sort, and trim any section that runs into the next one (a
     * section swallowed whole by its predecessor is dropped). Returns new objects; the input is not changed.
     */
    function normalize(list) {
        const out = [];
        for (const s of sorted((list || []).filter(isValid))) {
            const prev = out[out.length - 1];
            const copy = { ...s };
            if (prev && prev.end > copy.start) {
                // Overlap: the earlier section yields, unless that would leave it too short, then the later one yields
                if (copy.start - prev.start >= MIN_LENGTH) prev.end = copy.start;
                else if (copy.end - prev.end >= MIN_LENGTH) copy.start = prev.end;
                else continue;
            }
            out.push(copy);
        }
        return out;
    }

    /** Plain copies carrying exactly the persisted fields. */
    function serialize(list) {
        return (list || []).map(s => ({ id: s.id, start: s.start, end: s.end, name: s.name, color: s.color || null }));
    }

    /**
     * Read sections back from a saved project or an undo snapshot, tolerating a missing list or bad entries.
     * Missing ids are minted, missing names get a numbered default, missing colors the palette color for their place.
     */
    function restore(raw) {
        if (!Array.isArray(raw)) return [];
        const seen = new Set();
        const cleaned = [];
        for (const r of raw) {
            if (!r || typeof r !== 'object') continue;
            const start = Number(r.start);
            const end = Number(r.end);
            let id = typeof r.id === 'string' && r.id ? r.id : newId();
            if (seen.has(id)) id = newId();
            seen.add(id);
            cleaned.push({ id, start, end, name: typeof r.name === 'string' ? r.name : '', color: typeof r.color === 'string' && r.color ? r.color : null });
        }
        return normalize(cleaned).map((s, i) => ({
            ...s,
            name: s.name.trim() || `Section ${i + 1}`,
            color: s.color || colorFor(i)
        }));
    }

    /** The section containing time t (start inclusive, end exclusive), or null. */
    function at(list, t) {
        for (const s of list || []) {
            if (t >= s.start && t < s.end) return s;
        }
        return null;
    }

    /** The section starting after t (dir > 0) or the one that started before t (dir < 0), or null. */
    function neighbor(list, t, dir, eps = 1e-3) {
        const ordered = sorted(list);
        if (dir > 0) return ordered.find(s => s.start > t + eps) || null;
        return [...ordered].reverse().find(s => s.start < t - eps) || null;
    }

    /** The nearest section ends around a span: the previous one's end and the next one's start (the free gap). */
    function bounds(list, id, start, end) {
        const others = sorted((list || []).filter(s => s.id !== id));
        let lo = 0;
        let hi = Infinity;
        for (const s of others) {
            const mid = (start + end) / 2;
            if (s.end <= mid) lo = Math.max(lo, s.end);
            else if (s.start >= mid) hi = Math.min(hi, s.start);
        }
        return { lo, hi };
    }

    /**
     * Where a section may go when it is dragged. Returns the span to use: unchanged if the request does not fit.
     * @param {Array} list - the current sections
     * @param {string} id - the section being edited
     * @param {number} start - requested start
     * @param {number} end - requested end
     * @param {'move'|'start'|'end'} mode - move keeps the length; start and end resize from that edge
     * @returns {{start: number, end: number}}
     */
    function constrain(list, id, start, end, mode) {
        const cur = (list || []).find(s => s.id === id);
        if (!cur) return { start, end };
        // Neighbors are judged around the section's current span, so dragging stops at them instead of leaping over
        const { lo, hi } = bounds(list, id, cur.start, cur.end);
        const len = cur.end - cur.start;
        if (mode === 'move') {
            if (hi - lo < len) return { start: cur.start, end: cur.end };
            const s = Math.max(lo, Math.min(start, hi - len));
            return { start: s, end: s + len };
        }
        if (mode === 'start') {
            const s = Math.max(lo, Math.min(start, cur.end - MIN_LENGTH));
            return { start: s, end: cur.end };
        }
        const e = Math.min(hi, Math.max(end, cur.start + MIN_LENGTH));
        return { start: cur.start, end: e };
    }

    /**
     * The room for a new section that starts at t: it runs `length` seconds, shortened to stop at the next section.
     * Null when t is inside an existing section or the gap is shorter than MIN_LENGTH.
     */
    function fit(list, t, length) {
        if (!Number.isFinite(t) || t < 0 || at(list, t)) return null;
        const next = sorted(list).find(s => s.start > t);
        const end = Math.min(t + Math.max(length, MIN_LENGTH), next ? next.start : Infinity);
        return end - t >= MIN_LENGTH - 1e-9 ? { start: t, end } : null;
    }

    /** A name like "Section 3" that no section uses yet. */
    function uniqueName(list, base = 'Section') {
        const used = new Set((list || []).map(s => s.name));
        for (let i = (list || []).length + 1; ; i++) {
            const name = `${base} ${i}`;
            if (!used.has(name)) return name;
        }
    }

    /** Add a section; returns the new list (normalized) and the section created. */
    function add(list, { start, end, name, color }) {
        const ids = new Set((list || []).map(s => s.id));
        let id = newId();
        while (ids.has(id)) id = newId();
        const made = {
            id, start, end,
            name: (name && name.trim()) || uniqueName(list),
            color: color || colorFor((list || []).length)
        };
        return { list: normalize([...(list || []), made]), section: made };
    }

    /**
     * Sections from markers: each marker opens a section that runs to the next marker, and the last runs to endTime.
     * A marker's name and color carry over. Markers too close together to make a section are skipped.
     * @param {Array<{time: number, name: string, color?: string}>} markers
     * @param {number} endTime - where the last section ends (the end of the content)
     */
    function fromMarkers(markers, endTime) {
        const ms = (markers || []).filter(m => Number.isFinite(m.time) && m.time >= 0).slice().sort((a, b) => a.time - b.time);
        const out = [];
        for (let i = 0; i < ms.length; i++) {
            const start = ms[i].time;
            const end = i + 1 < ms.length ? ms[i + 1].time : endTime;
            if (!(end - start >= MIN_LENGTH)) continue;
            out.push({
                id: newId(), start, end,
                name: (ms[i].name || '').trim() || `Section ${out.length + 1}`,
                color: ms[i].color || colorFor(out.length)
            });
        }
        return out;
    }

    return { MIN_LENGTH, PALETTE, newId, colorFor, sorted, isValid, normalize, serialize, restore, at, neighbor, constrain, fit, uniqueName, add, fromMarkers };
})();
