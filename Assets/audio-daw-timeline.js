/**
 * AudioDawTimeline — Timeline ruler with time markers, playhead, zoom, and loop region.
 * Renders a canvas-based ruler synced to the clip lanes scroll position.
 * Reuses SwarmUI utilities: createDiv(), formatTime from AudioLabPlayer.
 */
const AudioDawTimeline = (() => {
    'use strict';

    /**
     * Create a timeline ruler instance.
     * @param {HTMLElement} container - DOM element to mount the ruler into
     * @param {Object} opts - Configuration options
     * @param {number} opts.zoom - Pixels per second (default 100)
     * @param {number} opts.height - Ruler height in pixels (default 30)
     * @param {number} opts.totalDuration - Total timeline duration in seconds
     * @returns {Object} Timeline API
     */
    function create(container, opts = {}) {
        const state = {
            container,
            zoom: opts.zoom || 100,
            height: opts.height || 30,
            totalDuration: opts.totalDuration || 60,
            scrollLeft: 0,
            loopEnabled: false,
            loopStart: 0,
            loopEnd: 0,
            playheadTime: 0,
            callbacks: {}
        };

        const canvas = document.createElement('canvas');
        canvas.className = 'daw-ruler-canvas';
        canvas.height = state.height;
        canvas.style.display = 'block';
        canvas.style.height = state.height + 'px';
        container.appendChild(canvas);

        state.canvas = canvas;
        state.ctx = canvas.getContext('2d');

        // Playhead indicator on ruler
        const playheadMarker = createDiv(null, 'daw-ruler-playhead');
        container.appendChild(playheadMarker);
        state.playheadMarker = playheadMarker;

        // Click on ruler to seek
        canvas.addEventListener('click', (e) => {
            const rect = canvas.getBoundingClientRect();
            const x = e.clientX - rect.left + state.scrollLeft;
            const time = x / state.zoom;
            fire(state, 'seek', Math.max(0, Math.min(time, state.totalDuration)));
        });

        // Loop region handles (draggable)
        const loopStartHandle = createDiv(null, 'daw-loop-handle daw-loop-start');
        loopStartHandle.title = 'Drag to set loop start';
        container.appendChild(loopStartHandle);

        const loopEndHandle = createDiv(null, 'daw-loop-handle daw-loop-end');
        loopEndHandle.title = 'Drag to set loop end';
        container.appendChild(loopEndHandle);

        state.loopStartHandle = loopStartHandle;
        state.loopEndHandle = loopEndHandle;

        setupLoopDrag(state, loopStartHandle, 'start');
        setupLoopDrag(state, loopEndHandle, 'end');
        updateLoopHandles(state);

        updateCanvasWidth(state);
        draw(state);

        return buildAPI(state);
    }

    function updateCanvasWidth(state) {
        const totalWidth = Math.max(state.totalDuration * state.zoom, state.container.clientWidth);
        state.canvas.width = totalWidth;
        state.canvas.style.width = totalWidth + 'px';
    }

    function draw(state) {
        const { ctx, canvas, zoom, height, totalDuration, loopEnabled, loopStart, loopEnd } = state;
        const width = canvas.width;

        // Detect theme colors from CSS vars
        const cs = getComputedStyle(document.documentElement);
        const textColor = cs.getPropertyValue('--text-soft').trim() || '#999';
        const lineColor = cs.getPropertyValue('--shadow').trim() || '#444';
        const emphColor = cs.getPropertyValue('--emphasis').trim() || '#6af';
        const bgColor = cs.getPropertyValue('--background').trim() || '#1a1a1a';

        ctx.clearRect(0, 0, width, height);

        // Background
        ctx.fillStyle = bgColor;
        ctx.fillRect(0, 0, width, height);

        // Determine tick interval based on zoom level
        const tickInterval = getTickInterval(zoom);
        const subTicks = tickInterval.sub;
        const majorInterval = tickInterval.major;

        // Draw ticks
        ctx.strokeStyle = lineColor;
        ctx.fillStyle = textColor;
        ctx.font = '10px var(--font-monospace, monospace)';
        ctx.textAlign = 'center';

        const startTime = 0;
        const endTime = totalDuration;

        // Sub-ticks
        ctx.lineWidth = 0.5;
        for (let t = 0; t <= endTime; t += subTicks) {
            const x = Math.round(t * zoom) + 0.5;
            ctx.beginPath();
            ctx.moveTo(x, height - 5);
            ctx.lineTo(x, height);
            ctx.stroke();
        }

        // Major ticks with labels
        ctx.lineWidth = 1;
        ctx.strokeStyle = textColor;
        for (let t = 0; t <= endTime; t += majorInterval) {
            const x = Math.round(t * zoom) + 0.5;
            ctx.beginPath();
            ctx.moveTo(x, height - 12);
            ctx.lineTo(x, height);
            ctx.stroke();
            ctx.fillText(formatRulerTime(t), x, height - 14);
        }

        // Loop region overlay
        if (loopEnabled && loopEnd > loopStart) {
            ctx.fillStyle = emphColor.startsWith('#') ? emphColor + '26' : emphColor.replace(')', ', 0.15)').replace('rgb(', 'rgba(');
            const lx = loopStart * zoom;
            const lw = (loopEnd - loopStart) * zoom;
            ctx.fillRect(lx, 0, lw, height);

            // Loop markers
            ctx.fillStyle = emphColor;
            ctx.fillRect(lx, 0, 2, height);
            ctx.fillRect(lx + lw - 2, 0, 2, height);
        }

        // Bottom border line
        ctx.strokeStyle = lineColor;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(0, height - 0.5);
        ctx.lineTo(width, height - 0.5);
        ctx.stroke();
    }

    /**
     * Determine appropriate tick intervals based on zoom level.
     * Higher zoom = finer ticks, lower zoom = coarser ticks.
     */
    function getTickInterval(zoom) {
        // zoom = pixels per second
        if (zoom >= 500) return { major: 1, sub: 0.1 };
        if (zoom >= 200) return { major: 1, sub: 0.25 };
        if (zoom >= 100) return { major: 2, sub: 0.5 };
        if (zoom >= 50) return { major: 5, sub: 1 };
        if (zoom >= 20) return { major: 10, sub: 2 };
        if (zoom >= 10) return { major: 30, sub: 5 };
        return { major: 60, sub: 10 };
    }

    /**
     * Format time for ruler labels.
     * Short format: "0:00" for < 1 hour, "1:00:00" for >= 1 hour.
     */
    function formatRulerTime(seconds) {
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        const ms = Math.round((seconds % 1) * 10);
        if (seconds < 60) {
            return s + (ms > 0 ? '.' + ms : '') + 's';
        }
        return m + ':' + s.toString().padStart(2, '0');
    }

    function setupLoopDrag(state, handle, which) {
        handle.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            e.stopPropagation();
            handle.setPointerCapture(e.pointerId);

            const onMove = (me) => {
                const rect = state.canvas.getBoundingClientRect();
                const x = me.clientX - rect.left + state.scrollLeft;
                const time = Math.max(0, Math.min(x / state.zoom, state.totalDuration));
                if (which === 'start') {
                    state.loopStart = Math.min(time, state.loopEnd - 0.1);
                } else {
                    state.loopEnd = Math.max(time, state.loopStart + 0.1);
                }
                updateLoopHandles(state);
                draw(state);
                fire(state, 'loopChange', state.loopStart, state.loopEnd);
            };

            const onUp = (ue) => {
                handle.releasePointerCapture(ue.pointerId);
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
            };

            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
        });
    }

    function updateLoopHandles(state) {
        if (!state.loopStartHandle || !state.loopEndHandle) return;
        const visible = state.loopEnabled;
        state.loopStartHandle.style.display = visible ? '' : 'none';
        state.loopEndHandle.style.display = visible ? '' : 'none';
        if (visible) {
            state.loopStartHandle.style.left = (state.loopStart * state.zoom - state.scrollLeft) + 'px';
            state.loopEndHandle.style.left = (state.loopEnd * state.zoom - state.scrollLeft) + 'px';
        }
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
                updateCanvasWidth(state);
                draw(state);
            },

            /** Update total timeline duration and redraw. */
            setDuration(duration) {
                state.totalDuration = Math.max(duration, 1);
                updateCanvasWidth(state);
                draw(state);
            },

            /** Sync scroll position with clip lanes. */
            setScrollLeft(scrollLeft) {
                state.scrollLeft = scrollLeft;
                state.container.scrollLeft = scrollLeft;
            },

            /** Update playhead position on ruler. */
            setPlayheadTime(time) {
                state.playheadTime = time;
                const x = time * state.zoom - state.scrollLeft;
                state.playheadMarker.style.transform = `translateX(${x}px)`;
            },

            /** Set loop region bounds. */
            setLoop(enabled, start, end) {
                state.loopEnabled = enabled;
                state.loopStart = start || 0;
                state.loopEnd = end || state.totalDuration;
                updateLoopHandles(state);
                draw(state);
            },

            /** Get current zoom level. */
            getZoom() { return state.zoom; },

            /** Subscribe to events: 'seek'. */
            on(event, callback) {
                if (!state.callbacks[event]) state.callbacks[event] = [];
                state.callbacks[event].push(callback);
            },

            /** Force redraw (e.g., on theme change). */
            redraw() {
                updateCanvasWidth(state);
                draw(state);
            },

            /** Get the canvas element for external layout queries. */
            getCanvas() { return state.canvas; },

            /** Destroy and clean up. */
            destroy() {
                state.container.innerHTML = '';
                state.callbacks = {};
            }
        };
    }

    return { create };
})();
