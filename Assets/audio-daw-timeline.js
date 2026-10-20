/**
 * AudioDawTimeline: the stacked rulers above the arrangement, the way a pro DAW shows them:
 * Bars|Beats, Min:Secs and a Markers row, plus the loop/punch range, the selection and the playhead.
 * Only the visible slice is drawn (one viewport-wide canvas that follows the scroll), so a long session at a
 * high zoom never exceeds the browser's canvas size limit.
 * Reuses SwarmUI utilities: createDiv().
 */
const AudioDawTimeline = (() => {
    'use strict';

    const ROW_BARS = 18;
    const ROW_TIME = 16;
    const ROW_MARKERS = 18;
    const HEIGHT = ROW_BARS + ROW_TIME + ROW_MARKERS;

    /**
     * Create a timeline ruler instance.
     * @param {HTMLElement} container - DOM element to mount the ruler into (scrolled in step with the lanes)
     * @param {Object} opts - { zoom (px/s), totalDuration (s), mode ('time'|'beats'), bpm, timeSig }
     * @returns {Object} Timeline API
     */
    function create(container, opts = {}) {
        const state = {
            container,
            zoom: opts.zoom || 100,
            height: HEIGHT,
            totalDuration: opts.totalDuration || 60,
            scrollLeft: 0,
            loopEnabled: false,
            punch: false,
            loopStart: 0,
            loopEnd: 0,
            selection: null,          // {start, end}
            markers: [],              // [{id, time, name, color}]
            playheadTime: 0,
            mode: opts.mode || 'time', // which row the grid follows; both rows always draw
            bpm: opts.bpm || 120,
            timeSig: opts.timeSig || [4, 4],
            snapper: null,
            callbacks: {}
        };
        container.style.height = HEIGHT + 'px';

        // Spacer gives the ruler the arrangement's full scroll width; the canvas only covers the viewport.
        const spacer = createDiv(null, 'daw-ruler-spacer');
        container.appendChild(spacer);
        const canvas = document.createElement('canvas');
        canvas.className = 'daw-ruler-canvas';
        canvas.style.position = 'absolute';
        canvas.style.top = '0';
        canvas.style.height = HEIGHT + 'px';
        container.appendChild(canvas);
        state.canvas = canvas;
        state.spacer = spacer;
        state.ctx = canvas.getContext('2d');

        const playheadMarker = createDiv(null, 'daw-ruler-playhead');
        container.appendChild(playheadMarker);
        state.playheadMarker = playheadMarker;

        const markerLayer = createDiv(null, 'daw-ruler-markers');
        markerLayer.style.top = (ROW_BARS + ROW_TIME) + 'px';
        markerLayer.style.height = ROW_MARKERS + 'px';
        container.appendChild(markerLayer);
        state.markerLayer = markerLayer;

        // Scrub on the Bars and Min:Secs rows; the marker row adds and edits markers instead.
        const timeAt = (clientX) => {
            const rect = container.getBoundingClientRect();
            return Math.max(0, Math.min((clientX - rect.left + container.scrollLeft) / state.zoom, state.totalDuration));
        };
        state.timeAt = timeAt;
        canvas.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            const rect = container.getBoundingClientRect();
            if (e.clientY - rect.top >= ROW_BARS + ROW_TIME) return; // marker row: handled by markerLayer
            e.preventDefault();
            canvas.setPointerCapture(e.pointerId);
            fire(state, 'scrub', timeAt(e.clientX));
            const onMove = (me) => fire(state, 'scrub', timeAt(me.clientX));
            const onUp = (ue) => {
                try { canvas.releasePointerCapture(ue.pointerId); } catch (_) {}
                canvas.removeEventListener('pointermove', onMove);
                canvas.removeEventListener('pointerup', onUp);
                fire(state, 'seek', timeAt(ue.clientX));
            };
            canvas.addEventListener('pointermove', onMove);
            canvas.addEventListener('pointerup', onUp);
        });
        markerLayer.addEventListener('dblclick', (e) => {
            if (e.target !== markerLayer) return;
            let t = timeAt(e.clientX);
            if (state.snapper) t = state.snapper(t);
            fire(state, 'markerAdd', t);
        });
        markerLayer.title = translate('Markers: double-click to add, drag to move, click to jump, right-click for options');

        // Loop/punch region handles
        const loopStartHandle = createDiv(null, 'daw-loop-handle daw-loop-start');
        loopStartHandle.title = translate('Drag to set loop start');
        container.appendChild(loopStartHandle);
        const loopEndHandle = createDiv(null, 'daw-loop-handle daw-loop-end');
        loopEndHandle.title = translate('Drag to set loop end');
        container.appendChild(loopEndHandle);
        state.loopStartHandle = loopStartHandle;
        state.loopEndHandle = loopEndHandle;
        setupLoopDrag(state, loopStartHandle, 'start');
        setupLoopDrag(state, loopEndHandle, 'end');

        layout(state);
        return buildAPI(state);
    }

    /** Size the spacer to the arrangement, park the canvas over the viewport, and redraw everything. */
    function layout(state) {
        const width = Math.max(state.totalDuration * state.zoom, state.container.clientWidth);
        state.spacer.style.width = width + 'px';
        state.spacer.style.height = HEIGHT + 'px';
        const viewW = Math.max(1, state.container.clientWidth);
        const dpr = window.devicePixelRatio || 1;
        if (state.canvas.width !== Math.round(viewW * dpr) || state.canvas.height !== Math.round(HEIGHT * dpr)) {
            state.canvas.width = Math.round(viewW * dpr);
            state.canvas.height = Math.round(HEIGHT * dpr);
        }
        state.canvas.style.width = viewW + 'px';
        state.canvas.style.left = state.scrollLeft + 'px';
        updateLoopHandles(state);
        renderMarkers(state);
        draw(state);
        positionPlayhead(state);
    }

    function draw(state) {
        const { ctx, zoom } = state;
        const dpr = window.devicePixelRatio || 1;
        const viewW = state.canvas.width / dpr;
        const x0 = state.scrollLeft;
        const t0 = x0 / zoom;
        const t1 = (x0 + viewW) / zoom;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        const cs = getComputedStyle(document.documentElement);
        const textColor = cs.getPropertyValue('--text-soft').trim() || '#999';
        const strongText = cs.getPropertyValue('--text').trim() || '#ddd';
        const lineColor = cs.getPropertyValue('--shadow').trim() || '#444';
        const emphColor = cs.getPropertyValue('--emphasis').trim() || '#6af';
        const bgColor = cs.getPropertyValue('--background').trim() || '#1a1a1a';
        const softBg = cs.getPropertyValue('--background-soft').trim() || '#222';
        const redColor = cs.getPropertyValue('--red').trim() || '#e05252';

        ctx.clearRect(0, 0, viewW, HEIGHT);
        ctx.fillStyle = bgColor;
        ctx.fillRect(0, 0, viewW, ROW_BARS + ROW_TIME);
        ctx.fillStyle = softBg;
        ctx.fillRect(0, ROW_BARS + ROW_TIME, viewW, ROW_MARKERS);
        const X = (t) => Math.round(t * zoom - x0) + 0.5;

        ctx.font = '10px var(--font-monospace, monospace)';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';

        // Row 1: Bars|Beats
        const secPerBeat = 60 / state.bpm;
        const perBar = state.timeSig[0] || 4;
        const secPerBar = secPerBeat * perBar;
        let labelEvery = 1;
        while (secPerBar * zoom * labelEvery < 44) labelEvery *= 2;
        ctx.strokeStyle = lineColor;
        ctx.lineWidth = 1;
        if (secPerBeat * zoom >= 6) {
            const firstBeat = Math.floor(t0 / secPerBeat);
            for (let b = firstBeat; b * secPerBeat <= t1; b++) {
                if (b % perBar === 0) continue;
                const x = X(b * secPerBeat);
                ctx.beginPath();
                ctx.moveTo(x, ROW_BARS - 4);
                ctx.lineTo(x, ROW_BARS);
                ctx.stroke();
            }
        }
        const firstBar = Math.floor(t0 / secPerBar);
        for (let bar = firstBar; bar * secPerBar <= t1; bar++) {
            const x = X(bar * secPerBar);
            ctx.strokeStyle = textColor;
            ctx.beginPath();
            ctx.moveTo(x, bar % labelEvery === 0 ? 2 : ROW_BARS - 7);
            ctx.lineTo(x, ROW_BARS);
            ctx.stroke();
            if (bar % labelEvery === 0) {
                ctx.fillStyle = state.mode === 'beats' ? strongText : textColor;
                ctx.fillText(String(bar + 1), x + 3, ROW_BARS / 2);
            }
        }

        // Row 2: Min:Secs
        const tick = getTickInterval(zoom);
        ctx.strokeStyle = lineColor;
        for (let i = Math.floor(t0 / tick.sub); i * tick.sub <= t1; i++) {
            const x = X(i * tick.sub);
            ctx.beginPath();
            ctx.moveTo(x, ROW_BARS + ROW_TIME - 4);
            ctx.lineTo(x, ROW_BARS + ROW_TIME);
            ctx.stroke();
        }
        for (let i = Math.floor(t0 / tick.major); i * tick.major <= t1; i++) {
            const t = i * tick.major;
            const x = X(t);
            ctx.strokeStyle = textColor;
            ctx.beginPath();
            ctx.moveTo(x, ROW_BARS + 3);
            ctx.lineTo(x, ROW_BARS + ROW_TIME);
            ctx.stroke();
            ctx.fillStyle = state.mode === 'time' ? strongText : textColor;
            ctx.fillText(formatRulerTime(t), x + 3, ROW_BARS + ROW_TIME / 2);
        }

        // Selection band across both scales
        const sel = state.selection;
        if (sel && sel.end > sel.start) {
            ctx.fillStyle = withAlpha(strongText, 0.16);
            ctx.fillRect(sel.start * zoom - x0, 0, (sel.end - sel.start) * zoom, ROW_BARS + ROW_TIME);
        }

        // Loop region (red while it is the punch range)
        if ((state.loopEnabled || state.punch) && state.loopEnd > state.loopStart) {
            const color = state.punch ? redColor : emphColor;
            const lx = state.loopStart * zoom - x0;
            const lw = (state.loopEnd - state.loopStart) * zoom;
            ctx.fillStyle = withAlpha(color, 0.18);
            ctx.fillRect(lx, 0, lw, ROW_BARS);
            ctx.fillStyle = color;
            ctx.fillRect(lx, 0, 2, ROW_BARS + ROW_TIME);
            ctx.fillRect(lx + lw - 2, 0, 2, ROW_BARS + ROW_TIME);
        }

        // Row separators
        ctx.strokeStyle = lineColor;
        for (const y of [ROW_BARS, ROW_BARS + ROW_TIME, HEIGHT]) {
            ctx.beginPath();
            ctx.moveTo(0, y - 0.5);
            ctx.lineTo(viewW, y - 0.5);
            ctx.stroke();
        }
    }

    /** "#rgb", "#rrggbb" or "rgb(...)" plus alpha, as a canvas color string. */
    function withAlpha(color, alpha) {
        const c = color.trim();
        if (c.startsWith('#')) {
            const hex = c.length === 4 ? c.slice(1).split('').map(h => h + h).join('') : c.slice(1, 7);
            const n = parseInt(hex, 16);
            return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
        }
        if (c.startsWith('rgb(')) return c.replace('rgb(', 'rgba(').replace(')', `, ${alpha})`);
        return c;
    }

    /** Tick spacing for the Min:Secs row, finer as the zoom grows. */
    function getTickInterval(zoom) {
        if (zoom >= 800) return { major: 0.5, sub: 0.05 };
        if (zoom >= 500) return { major: 1, sub: 0.1 };
        if (zoom >= 200) return { major: 1, sub: 0.25 };
        if (zoom >= 100) return { major: 2, sub: 0.5 };
        if (zoom >= 50) return { major: 5, sub: 1 };
        if (zoom >= 20) return { major: 10, sub: 2 };
        if (zoom >= 10) return { major: 30, sub: 5 };
        return { major: 60, sub: 10 };
    }

    /** "0:05", "1:30", "1:02:00" (plus tenths when the ticks are that fine). */
    function formatRulerTime(seconds) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        const tenths = Math.round((seconds % 1) * 10) % 10;
        const ss = String(s).padStart(2, '0') + (tenths ? '.' + tenths : '');
        return h ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
    }

    function renderMarkers(state) {
        const layer = state.markerLayer;
        layer.style.width = Math.max(state.totalDuration * state.zoom, state.container.clientWidth) + 'px';
        layer.innerHTML = '';
        for (const m of state.markers) {
            const flag = createDiv(null, 'daw-marker');
            flag.style.left = (m.time * state.zoom) + 'px';
            if (m.color) flag.style.setProperty('--marker-color', m.color);
            flag.textContent = m.name;
            flag.title = `${m.name} (${formatRulerTime(m.time)}). ${translate('Click to jump, drag to move, double-click to rename, right-click for options')}`;
            setupMarkerDrag(state, flag, m);
            flag.addEventListener('dblclick', (e) => {
                e.stopPropagation();
                fire(state, 'markerRename', m.id);
            });
            flag.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                e.stopPropagation();
                fire(state, 'markerContext', m.id, e);
            });
            layer.appendChild(flag);
        }
    }

    function setupMarkerDrag(state, flag, marker) {
        flag.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            e.preventDefault();
            e.stopPropagation();
            flag.setPointerCapture(e.pointerId);
            const startX = e.clientX;
            const startTime = marker.time;
            let moved = false;
            const onMove = (me) => {
                if (!moved && Math.abs(me.clientX - startX) < 3) return;
                moved = true;
                let t = Math.max(0, startTime + (me.clientX - startX) / state.zoom);
                if (state.snapper) t = state.snapper(t);
                flag.style.left = (t * state.zoom) + 'px';
                flag.dataset.time = String(t);
            };
            const onUp = (ue) => {
                try { flag.releasePointerCapture(ue.pointerId); } catch (_) {}
                flag.removeEventListener('pointermove', onMove);
                flag.removeEventListener('pointerup', onUp);
                if (moved) fire(state, 'markerMove', marker.id, parseFloat(flag.dataset.time));
                else fire(state, 'markerClick', marker.id);
            };
            flag.addEventListener('pointermove', onMove);
            flag.addEventListener('pointerup', onUp);
        });
    }

    function setupLoopDrag(state, handle, which) {
        handle.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            e.stopPropagation();
            handle.setPointerCapture(e.pointerId);
            const onMove = (me) => {
                let time = state.timeAt(me.clientX);
                if (state.snapper) time = state.snapper(time);
                if (which === 'start') {
                    state.loopStart = Math.min(time, state.loopEnd - 0.05);
                } else {
                    state.loopEnd = Math.max(time, state.loopStart + 0.05);
                }
                updateLoopHandles(state);
                draw(state);
                fire(state, 'loopChange', state.loopStart, state.loopEnd);
            };
            const onUp = (ue) => {
                try { handle.releasePointerCapture(ue.pointerId); } catch (_) {}
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
            };
            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
        });
    }

    function updateLoopHandles(state) {
        const visible = (state.loopEnabled || state.punch) && state.loopEnd > state.loopStart;
        // 'block', not '': the stylesheet hides handles by default, so clearing the inline value hid them for good
        state.loopStartHandle.style.display = visible ? 'block' : 'none';
        state.loopEndHandle.style.display = visible ? 'block' : 'none';
        state.loopStartHandle.classList.toggle('punch', state.punch);
        state.loopEndHandle.classList.toggle('punch', state.punch);
        if (visible) {
            state.loopStartHandle.style.left = (state.loopStart * state.zoom) + 'px';
            state.loopEndHandle.style.left = (state.loopEnd * state.zoom - 6) + 'px';
        }
    }

    function positionPlayhead(state) {
        state.playheadMarker.style.transform = `translateX(${state.playheadTime * state.zoom}px)`;
    }

    function fire(state, event, ...args) {
        const cbs = state.callbacks[event];
        if (cbs) cbs.forEach(cb => cb(...args));
    }

    function buildAPI(state) {
        return {
            /** Update the zoom level (pixels per second) and redraw. */
            setZoom(zoom) {
                state.zoom = zoom;
                layout(state);
            },

            /** Update total timeline duration and redraw. */
            setDuration(duration) {
                state.totalDuration = Math.max(duration, 1);
                layout(state);
            },

            /** Follow the lanes' horizontal scroll (the canvas only covers what is visible). */
            setScrollLeft(scrollLeft) {
                state.scrollLeft = scrollLeft;
                state.container.scrollLeft = scrollLeft;
                state.canvas.style.left = scrollLeft + 'px';
                draw(state);
            },

            /** Update playhead position on ruler. */
            setPlayheadTime(time) {
                state.playheadTime = time;
                positionPlayhead(state);
            },

            /** Set loop region bounds. */
            setLoop(enabled, start, end) {
                state.loopEnabled = enabled;
                state.loopStart = start || 0;
                state.loopEnd = end || state.totalDuration;
                updateLoopHandles(state);
                draw(state);
            },

            /** Show the loop range as the punch range (red, handles visible even with loop off). */
            setPunch(enabled) {
                state.punch = !!enabled;
                updateLoopHandles(state);
                draw(state);
            },

            /** Show a time selection band (null clears it). */
            setSelection(sel) {
                state.selection = sel && sel.end > sel.start ? { start: sel.start, end: sel.end } : null;
                draw(state);
            },

            /** Replace the marker list ([{id, time, name, color}]). */
            setMarkers(markers) {
                state.markers = (markers || []).slice().sort((a, b) => a.time - b.time);
                renderMarkers(state);
            },

            /** Get current zoom level. */
            getZoom() { return state.zoom; },

            /** Set tempo + time signature. */
            setTempo(bpm, timeSig) {
                state.bpm = bpm || state.bpm;
                if (timeSig) state.timeSig = timeSig;
                draw(state);
            },

            /** Which scale the grid follows: 'time' or 'beats' (its labels draw brighter). */
            setMode(mode) {
                state.mode = mode === 'beats' ? 'beats' : 'time';
                draw(state);
            },

            getMode() { return state.mode; },

            /** Provide a fn(time)->time used to snap loop-handle and marker drags. */
            setSnapper(fn) { state.snapper = fn; },

            /** Subscribe: 'seek', 'scrub', 'loopChange', 'markerAdd', 'markerMove', 'markerClick',
             *  'markerRename', 'markerContext'. */
            on(event, callback) {
                if (!state.callbacks[event]) state.callbacks[event] = [];
                state.callbacks[event].push(callback);
            },

            /** Re-measure and redraw (container resize, theme change). */
            redraw() {
                layout(state);
            },

            /** Ruler height in pixels (all rows). */
            getHeight() { return HEIGHT; },

            getCanvas() { return state.canvas; },

            /** Destroy and clean up. */
            destroy() {
                state.container.innerHTML = '';
                state.callbacks = {};
            }
        };
    }

    return { create, formatRulerTime, HEIGHT };
})();
