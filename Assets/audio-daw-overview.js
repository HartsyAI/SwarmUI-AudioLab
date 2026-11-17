/**
 * Audio Lab DAW: the whole-song overview strip.
 *
 * A thin canvas above the ruler that draws the whole arrangement at once: a row of mini clip blocks per track (in the
 * colors the lanes use), the sections as a colored band, the markers as ticks, the playhead, and a window that shows
 * which part of the song the lanes are scrolled to. Dragging the window scrolls the lanes; clicking elsewhere
 * recenters it; double-clicking seeks.
 *
 * The geometry (time <-> pixel, the window rectangle, the scroll a drag asks for, the rows to draw) is pure and
 * exported for testing, so it can be exercised from a browser console without the DOM. `create` is the only part that
 * touches the DOM. All times are seconds and all positions pixels, like the rest of the DAW.
 */
const AudioDawOverview = (() => {
    'use strict';

    /** The window is never drawn narrower than this, so a long song still leaves something to grab. */
    const MIN_WINDOW_PX = 8;

    /** Height of the section band and of the marker ticks, in pixels. */
    const BAND_H = 5;

    /**
     * How many seconds the strip spans: the content, or the visible part of the lanes if that reaches further (zoomed
     * out past the end), and never less than a second so an empty project still has a scale.
     */
    function span(contentDuration, viewEnd) {
        return Math.max(1, contentDuration || 0, viewEnd || 0);
    }

    /** Pixel for a time on a strip `width` wide covering `spanSeconds`. */
    function timeToX(t, spanSeconds, width) {
        return t / spanSeconds * width;
    }

    /** Time for a pixel, clamped to the strip. */
    function xToTime(x, spanSeconds, width) {
        if (!(width > 0)) return 0;
        return Math.max(0, Math.min(x, width)) / width * spanSeconds;
    }

    /**
     * The window rectangle: the part of the song the lanes show.
     * @param {{viewStart: number, viewEnd: number, span: number, width: number}} o - view in seconds
     * @returns {{x: number, w: number}}
     */
    function viewportRect({ viewStart, viewEnd, span: spanSeconds, width }) {
        let x = timeToX(Math.max(0, viewStart), spanSeconds, width);
        let w = timeToX(Math.max(0, viewEnd - Math.max(0, viewStart)), spanSeconds, width);
        w = Math.max(MIN_WINDOW_PX, Math.min(w, width));
        x = Math.max(0, Math.min(x, width - w));
        return { x, w };
    }

    /**
     * The lanes' scrollLeft that puts the window where the pointer is. `grab` is where inside the window the pointer
     * holds it (0 = left edge, 0.5 = middle), so a drag does not jump the window under the pointer.
     * @param {{x: number, width: number, span: number, zoom: number, viewW: number, grab?: number, scrollWidth?: number}} o
     */
    function targetScroll({ x, width, span: spanSeconds, zoom, viewW, grab = 0.5, scrollWidth }) {
        const viewSeconds = viewW / zoom;
        const t = xToTime(x, spanSeconds, width) - grab * viewSeconds;
        const raw = Math.max(0, t * zoom);
        if (!Number.isFinite(scrollWidth)) return raw;
        return Math.max(0, Math.min(raw, Math.max(0, scrollWidth - viewW)));
    }

    /** Where inside the window (0 to 1) a pointer at `x` holds it, or null when `x` is outside it. */
    function grabAt(x, rect) {
        if (x < rect.x || x > rect.x + rect.w) return null;
        return rect.w > 0 ? (x - rect.x) / rect.w : 0.5;
    }

    /**
     * The blocks to draw: one row per track (buses hold no clips and are left out) with a block per audio clip (its
     * visible part) and per MIDI clip, in the color the lane draws it in.
     * @param {object[]} tracks - tracks with `clips`, `midiClips`, `color` and an optional `displayColor`
     * @param {function(object): number} [midiLength] - the length of a MIDI clip in seconds
     * @returns {{rows: number, blocks: Array<{row: number, start: number, end: number, color: string, muted: boolean}>}}
     */
    function buildRows(tracks, midiLength = null) {
        const blocks = [];
        let row = 0;
        for (const t of tracks || []) {
            if (t.kind === 'bus') continue;
            const base = t.displayColor || t.color || '#888';
            for (const c of t.clips || []) {
                const len = (c.duration || 0) - (c.offset || 0) - (c.trimEnd || 0);
                if (len > 0) blocks.push({ row, start: c.startTime || 0, end: (c.startTime || 0) + len, color: c.color || base, muted: !!c.muted });
            }
            for (const m of t.midiClips || []) {
                const len = midiLength ? midiLength(m) : (m.length || 0);
                if (len > 0) blocks.push({ row, start: m.startTime || 0, end: (m.startTime || 0) + len, color: m.color || base, muted: !!m.muted });
            }
            row++;
        }
        return { rows: row, blocks };
    }

    /** Height of each track row and the gap between rows, for `rows` rows in `height` pixels below the section band. */
    function rowGeometry(rows, height) {
        const avail = Math.max(1, height - BAND_H - 2);
        if (rows <= 0) return { rowH: 0, gap: 0 };
        const gap = rows > 1 && avail / rows >= 4 ? 1 : 0;
        const rowH = Math.max(1, Math.min(8, Math.floor((avail - gap * (rows - 1)) / rows)));
        return { rowH, gap };
    }

    /**
     * Create the strip in a container.
     * @param {HTMLElement} container - the element to fill (its size is the strip's size)
     * @returns {{setData, setViewport, setPlayhead, redraw, on, destroy}}
     *   setData({tracks, sections, markers, contentDuration, midiLength}), setViewport({scrollLeft, viewW, zoom, scrollWidth}),
     *   setPlayhead(seconds), on('scroll', fn(scrollLeft)), on('seek', fn(seconds)).
     */
    function create(container) {
        const canvas = document.createElement('canvas');
        canvas.className = 'daw-overview-canvas';
        container.appendChild(canvas);
        const base = document.createElement('canvas'); // the blocks, redrawn only when the data or size changes
        const st = {
            data: { tracks: [], sections: [], markers: [], contentDuration: 0, midiLength: null },
            view: { scrollLeft: 0, viewW: 0, zoom: 100, scrollWidth: 0 },
            playhead: 0,
            drag: null,
            callbacks: {},
            baseDirty: true,
            baseSpan: 0              // the span the cached blocks were drawn for
        };
        const colors = () => {
            const cs = getComputedStyle(document.documentElement);
            const get = (name, fallback) => cs.getPropertyValue(name).trim() || fallback;
            return { bg: get('--background', '#1a1a1a'), soft: get('--background-soft', '#222'), text: get('--text', '#ddd'), emph: get('--emphasis', '#6af'), line: get('--shadow', '#444') };
        };
        const fire = (event, ...args) => { for (const cb of st.callbacks[event] || []) cb(...args); };
        const geometry = () => {
            const width = container.clientWidth;
            const viewStart = st.view.scrollLeft / st.view.zoom;
            const viewEnd = (st.view.scrollLeft + st.view.viewW) / st.view.zoom;
            const spanSeconds = span(st.data.contentDuration, viewEnd);
            return { width, spanSeconds, rect: viewportRect({ viewStart, viewEnd, span: spanSeconds, width }) };
        };

        function paintBase() {
            const dpr = window.devicePixelRatio || 1;
            const w = container.clientWidth;
            const h = container.clientHeight;
            if (!(w > 0 && h > 0)) return false;
            if (base.width !== Math.round(w * dpr) || base.height !== Math.round(h * dpr)) {
                base.width = Math.round(w * dpr);
                base.height = Math.round(h * dpr);
            }
            const ctx = base.getContext('2d');
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            const c = colors();
            ctx.clearRect(0, 0, w, h);
            ctx.fillStyle = c.soft;
            ctx.fillRect(0, 0, w, h);
            const { spanSeconds } = geometry();
            st.baseSpan = spanSeconds;
            // Sections: a colored band along the top
            for (const s of st.data.sections) {
                ctx.fillStyle = s.color || c.emph;
                const x = timeToX(s.start, spanSeconds, w);
                ctx.fillRect(x, 0, Math.max(1, timeToX(s.end, spanSeconds, w) - x - 1), BAND_H);
            }
            // Clip blocks: a row per track under the band
            const { rows, blocks } = buildRows(st.data.tracks, st.data.midiLength);
            const { rowH, gap } = rowGeometry(rows, h);
            for (const b of blocks) {
                ctx.globalAlpha = b.muted ? 0.35 : 0.9;
                ctx.fillStyle = b.color;
                const x = timeToX(b.start, spanSeconds, w);
                ctx.fillRect(x, BAND_H + 2 + b.row * (rowH + gap), Math.max(1, timeToX(b.end, spanSeconds, w) - x), rowH);
            }
            ctx.globalAlpha = 1;
            // Markers: ticks under the section band
            ctx.fillStyle = c.text;
            for (const m of st.data.markers) ctx.fillRect(Math.round(timeToX(m.time, spanSeconds, w)), BAND_H, 1, 3);
            st.baseDirty = false;
            return true;
        }

        function paint() {
            const dpr = window.devicePixelRatio || 1;
            const w = container.clientWidth;
            const h = container.clientHeight;
            if (!(w > 0 && h > 0)) return;
            // The blocks only move when the data, the size or the scale changes, not on every scroll
            if (st.baseDirty || st.baseSpan !== geometry().spanSeconds || base.width !== Math.round(w * dpr)) {
                if (!paintBase()) return;
            }
            if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
                canvas.width = Math.round(w * dpr);
                canvas.height = Math.round(h * dpr);
                canvas.style.width = w + 'px';
                canvas.style.height = h + 'px';
            }
            const ctx = canvas.getContext('2d');
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.drawImage(base, 0, 0);
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            const c = colors();
            const { width, spanSeconds, rect } = geometry();
            // The window: the part of the song the lanes show, with the rest dimmed
            ctx.fillStyle = 'rgba(0, 0, 0, 0.32)';
            ctx.fillRect(0, 0, rect.x, h);
            ctx.fillRect(rect.x + rect.w, 0, width - rect.x - rect.w, h);
            ctx.strokeStyle = c.emph;
            ctx.lineWidth = 1.5;
            ctx.strokeRect(rect.x + 0.75, 0.75, Math.max(1, rect.w - 1.5), h - 1.5);
            // The playhead
            const px = Math.round(timeToX(st.playhead, spanSeconds, width)) + 0.5;
            ctx.strokeStyle = '#ff5a5a';
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(px, 0);
            ctx.lineTo(px, h);
            ctx.stroke();
        }

        const localX = (e) => e.clientX - container.getBoundingClientRect().left;
        const scrollTo = (x, grab) => {
            const { width, spanSeconds } = geometry();
            fire('scroll', targetScroll({ x, width, span: spanSeconds, zoom: st.view.zoom, viewW: st.view.viewW, grab, scrollWidth: st.view.scrollWidth }));
        };
        canvas.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            e.preventDefault();
            canvas.setPointerCapture(e.pointerId);
            const x = localX(e);
            const grab = grabAt(x, geometry().rect);
            // Outside the window: recenter on the pointer, then keep dragging it from the middle
            st.drag = { grab: grab === null ? 0.5 : grab };
            scrollTo(x, st.drag.grab);
            const onMove = (me) => scrollTo(localX(me), st.drag.grab);
            const onUp = (ue) => {
                try { canvas.releasePointerCapture(ue.pointerId); } catch (_) {}
                canvas.removeEventListener('pointermove', onMove);
                canvas.removeEventListener('pointerup', onUp);
                canvas.removeEventListener('pointercancel', onUp);
                st.drag = null;
            };
            canvas.addEventListener('pointermove', onMove);
            canvas.addEventListener('pointerup', onUp);
            canvas.addEventListener('pointercancel', onUp);
        });
        canvas.addEventListener('dblclick', (e) => {
            const { width, spanSeconds } = geometry();
            fire('seek', xToTime(localX(e), spanSeconds, width));
        });
        canvas.title = translate('Whole song. Drag the frame to scroll, click to jump the view there, double-click to move the playhead');

        return {
            /** New content: tracks (with `displayColor` set), sections, markers, the content length, a MIDI length function. */
            setData(data) {
                st.data = { ...st.data, ...data };
                st.baseDirty = true;
                paint();
            },
            /** The lanes' scroll state: { scrollLeft, viewW, zoom, scrollWidth }. */
            setViewport(view) {
                st.view = { ...st.view, ...view };
                paint();
            },
            setPlayhead(seconds) {
                st.playhead = seconds;
                paint();
            },
            /** Re-measure and redraw (container resize, theme change). */
            redraw() {
                st.baseDirty = true;
                paint();
            },
            /** Subscribe: 'scroll' (scrollLeft the lanes should take) and 'seek' (seconds). */
            on(event, callback) {
                (st.callbacks[event] = st.callbacks[event] || []).push(callback);
            },
            destroy() {
                container.innerHTML = '';
                st.callbacks = {};
            }
        };
    }

    return { MIN_WINDOW_PX, span, timeToX, xToTime, viewportRect, targetScroll, grabAt, buildRows, rowGeometry, create };
})();
