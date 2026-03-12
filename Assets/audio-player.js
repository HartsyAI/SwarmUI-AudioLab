/**
 * AudioLab Player Component — Reusable waveform audio player wrapping WaveSurfer.js v7
 * Drop-in replacement for <audio controls> with waveform visualization, regions, and editing.
 */
const AudioLabPlayer = (() => {
    'use strict';

    const instances = new Map();
    let idCounter = 0;

    const DEFAULTS = {
        height: 80,
        waveColor: 'rgba(130, 160, 220, 0.6)',
        progressColor: 'rgba(100, 140, 210, 0.85)',
        cursorColor: '#fff',
        cursorWidth: 2,
        barWidth: 2,
        barGap: 1,
        barRadius: 2,
        dragToSeek: true,
        hideScrollbar: true,
        normalize: true,
        showControls: true,
        showDownload: true,
        showSpeed: true,
        showVolume: true,
        enableRegions: false,
        editorMode: false,
        compact: false
    };

    /**
     * Create a new AudioLabPlayer instance inside a container element.
     * @param {string|HTMLElement} container - CSS selector or DOM element
     * @param {Object} options - Player options
     * @returns {Object} Player API
     */
    function create(container, options = {}) {
        const el = typeof container === 'string' ? document.getElementById(container) : container;
        if (!el) {
            console.error('[AudioLabPlayer] Container not found:', container);
            return null;
        }

        const id = `alp-${++idCounter}`;
        const opts = Object.assign({}, DEFAULTS, options);

        // Build player DOM
        const playerEl = document.createElement('div');
        playerEl.className = `audiolab-player${opts.compact ? ' audiolab-player-compact' : ''}`;
        playerEl.id = id;

        // Waveform container
        const waveformEl = document.createElement('div');
        waveformEl.className = 'audiolab-player-waveform';
        playerEl.appendChild(waveformEl);

        // Controls bar
        let controlsEl = null;
        if (opts.showControls) {
            controlsEl = document.createElement('div');
            controlsEl.className = 'audiolab-player-controls';
            controlsEl.innerHTML = buildControlsHTML(opts);
            playerEl.appendChild(controlsEl);
        }

        el.innerHTML = '';
        el.appendChild(playerEl);

        // Create WaveSurfer instance
        const ws = WaveSurfer.create({
            container: waveformEl,
            height: opts.height,
            waveColor: opts.waveColor,
            progressColor: opts.progressColor,
            cursorColor: opts.cursorColor,
            cursorWidth: opts.cursorWidth,
            barWidth: opts.barWidth,
            barGap: opts.barGap,
            barRadius: opts.barRadius,
            dragToSeek: opts.dragToSeek,
            hideScrollbar: opts.hideScrollbar,
            normalize: opts.normalize,
            interact: true
        });

        // Optional Regions plugin
        let regionsPlugin = null;
        if (opts.enableRegions && WaveSurfer.Regions) {
            regionsPlugin = WaveSurfer.Regions.create();
            ws.registerPlugin(regionsPlugin);
        }

        const state = {
            id,
            ws,
            regionsPlugin,
            controlsEl,
            playerEl,
            opts,
            currentUrl: null,
            currentBlob: null,
            activeRegion: null,
            callbacks: {}
        };

        // Wire up controls
        if (controlsEl) {
            wireControls(state);
        }

        // Wire up WaveSurfer events
        wireWaveSurferEvents(state);

        instances.set(id, state);

        return buildAPI(state);
    }

    function buildControlsHTML(opts) {
        const parts = [];
        // Play/pause button (Unicode: ▶ play, ⏸ pause, ■ stop)
        parts.push('<button class="alp-btn alp-play" title="Play/Pause"><span>&#x25B6;</span></button>');
        parts.push('<button class="alp-btn alp-stop" title="Stop"><span>&#x25A0;</span></button>');
        // Time display
        parts.push('<span class="alp-time"><span class="alp-current">0:00</span> / <span class="alp-duration">0:00</span></span>');
        // Volume
        if (opts.showVolume) {
            parts.push('<div class="alp-volume-group">');
            parts.push('<button class="alp-btn alp-mute" title="Mute"><span>&#x266B;</span></button>');
            parts.push('<input type="range" class="alp-volume" min="0" max="1" step="0.05" value="0.8">');
            parts.push('</div>');
        }
        // Speed
        if (opts.showSpeed) {
            parts.push('<select class="alp-speed" title="Playback speed">');
            parts.push('<option value="0.5">0.5x</option>');
            parts.push('<option value="0.75">0.75x</option>');
            parts.push('<option value="1" selected>1x</option>');
            parts.push('<option value="1.25">1.25x</option>');
            parts.push('<option value="1.5">1.5x</option>');
            parts.push('<option value="2">2x</option>');
            parts.push('</select>');
        }
        // Editing controls (when regions enabled and NOT in editorMode — editor has its own toolbar)
        if (opts.enableRegions && !opts.editorMode) {
            parts.push('<span class="alp-separator"></span>');
            parts.push('<button class="alp-btn alp-select-region" title="Select region for trim"><span>&#x2194;</span></button>');
            parts.push('<button class="alp-btn alp-trim" title="Trim to selection" disabled><span>&#x2702;</span></button>');
            parts.push('<button class="alp-btn alp-split" title="Split at cursor"><span>&#x2502;&#x2502;</span></button>');
        }
        // Download
        if (opts.showDownload) {
            parts.push('<button class="alp-btn alp-download" title="Download"><span>&#x2913;</span></button>');
        }
        return parts.join('');
    }

    function wireControls(state) {
        const el = state.controlsEl;
        const ws = state.ws;

        const playBtn = el.querySelector('.alp-play');
        const stopBtn = el.querySelector('.alp-stop');
        const muteBtn = el.querySelector('.alp-mute');
        const volumeSlider = el.querySelector('.alp-volume');
        const speedSelect = el.querySelector('.alp-speed');
        const downloadBtn = el.querySelector('.alp-download');

        if (playBtn) {
            playBtn.addEventListener('click', () => ws.playPause());
        }
        if (stopBtn) {
            stopBtn.addEventListener('click', () => { ws.stop(); });
        }
        if (muteBtn) {
            muteBtn.addEventListener('click', () => {
                const muted = !ws.getMuted();
                ws.setMuted(muted);
                muteBtn.querySelector('span').textContent = muted ? '\u266A' : '\u266B';
            });
        }
        if (volumeSlider) {
            volumeSlider.addEventListener('input', (e) => {
                ws.setVolume(parseFloat(e.target.value));
                ws.setMuted(false);
                if (muteBtn) muteBtn.querySelector('span').textContent = '\u266B';
            });
        }
        if (speedSelect) {
            speedSelect.addEventListener('change', (e) => {
                ws.setPlaybackRate(parseFloat(e.target.value), true);
            });
        }
        if (downloadBtn) {
            downloadBtn.addEventListener('click', () => {
                if (state.currentUrl) {
                    const a = document.createElement('a');
                    a.href = state.currentUrl;
                    a.download = `audiolab-${Date.now()}.wav`;
                    a.click();
                }
            });
        }

        // Editing controls
        const selectRegionBtn = el.querySelector('.alp-select-region');
        const trimBtn = el.querySelector('.alp-trim');
        const splitBtn = el.querySelector('.alp-split');

        if (selectRegionBtn && state.regionsPlugin) {
            selectRegionBtn.addEventListener('click', () => {
                const duration = state.ws.getDuration();
                if (duration <= 0) return;
                // Default: select middle 50%
                const start = duration * 0.25;
                const end = duration * 0.75;
                if (state.activeRegion) state.activeRegion.remove();
                state.activeRegion = state.regionsPlugin.addRegion({
                    start, end,
                    color: 'rgba(100, 140, 210, 0.3)',
                    drag: true,
                    resize: true
                });
                if (trimBtn) trimBtn.disabled = false;
            });
        }

        if (trimBtn) {
            trimBtn.addEventListener('click', async () => {
                const api = buildAPI(state);
                await api.trimToRegion();
                trimBtn.disabled = true;
            });
        }

        if (splitBtn) {
            splitBtn.addEventListener('click', async () => {
                const api = buildAPI(state);
                const parts = await api.splitAtCursor();
                if (parts) fire(state, 'split', parts);
            });
        }
    }

    function wireWaveSurferEvents(state) {
        const ws = state.ws;
        const el = state.controlsEl;

        ws.on('play', () => {
            const span = el?.querySelector('.alp-play span');
            if (span) span.textContent = '\u23F8';
            fire(state, 'play');
        });
        ws.on('pause', () => {
            const span = el?.querySelector('.alp-play span');
            if (span) span.textContent = '\u25B6';
            fire(state, 'pause');
        });
        ws.on('finish', () => {
            const span = el?.querySelector('.alp-play span');
            if (span) span.textContent = '\u25B6';
            fire(state, 'finish');
        });
        ws.on('timeupdate', (currentTime) => {
            const currentEl = el?.querySelector('.alp-current');
            if (currentEl) currentEl.textContent = formatTime(currentTime);
            fire(state, 'timeupdate', currentTime);
        });
        ws.on('decode', (duration) => {
            const durationEl = el?.querySelector('.alp-duration');
            if (durationEl) durationEl.textContent = formatTime(duration);
            fire(state, 'decode', duration);
        });
        ws.on('ready', (duration) => {
            const durationEl = el?.querySelector('.alp-duration');
            if (durationEl) durationEl.textContent = formatTime(duration);
            fire(state, 'ready', duration);
        });
    }

    function fire(state, event, ...args) {
        const cbs = state.callbacks[event];
        if (cbs) cbs.forEach(cb => cb(...args));
    }

    function formatTime(seconds) {
        if (!seconds || !isFinite(seconds)) return '0:00';
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        return `${m}:${s.toString().padStart(2, '0')}`;
    }

    /**
     * Build the public API object for a player instance.
     */
    function buildAPI(state) {
        return {
            id: state.id,

            /** Load audio from a URL (fetches as blob first — ws.load(url) hangs in SwarmUI) */
            async load(url) {
                const resp = await fetch(url);
                const blob = await resp.blob();
                return this.loadBlob(blob);
            },

            /** Load from base64 audio data */
            loadBase64(base64, mimeType = 'audio/wav') {
                const data = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
                const blob = new Blob([data], { type: mimeType });
                return this.loadBlob(blob);
            },

            /** Load from a Blob — the only safe load path (direct decode, no <audio> element) */
            loadBlob(blob) {
                if (state.currentUrl?.startsWith('blob:')) URL.revokeObjectURL(state.currentUrl);
                const url = URL.createObjectURL(blob);
                state.currentUrl = url;
                state.currentBlob = blob;
                return state.ws.loadBlob(blob);
            },

            /** Reload from a blob after an edit operation */
            async reloadFromBlob(blob) {
                if (state.currentUrl?.startsWith('blob:')) URL.revokeObjectURL(state.currentUrl);
                const url = URL.createObjectURL(blob);
                state.currentUrl = url;
                state.currentBlob = blob;
                await state.ws.loadBlob(blob);
            },

            play() { return state.ws.play(); },
            pause() { state.ws.pause(); },
            stop() { state.ws.stop(); },
            playPause() { return state.ws.playPause(); },

            getDuration() { return state.ws.getDuration(); },
            getCurrentTime() { return state.ws.getCurrentTime(); },
            setTime(t) { state.ws.setTime(t); },

            setVolume(v) { state.ws.setVolume(v); },
            getVolume() { return state.ws.getVolume(); },

            /** Create a region for trim selection */
            setRegion(start, end, opts = {}) {
                if (!state.regionsPlugin) return null;
                if (state.activeRegion) state.activeRegion.remove();
                state.activeRegion = state.regionsPlugin.addRegion({
                    start,
                    end,
                    color: opts.color || 'rgba(100, 140, 210, 0.3)',
                    drag: opts.drag !== false,
                    resize: opts.resize !== false,
                    ...opts
                });
                return state.activeRegion;
            },

            /** Get the active region bounds */
            getRegion() {
                if (!state.activeRegion) return null;
                return { start: state.activeRegion.start, end: state.activeRegion.end };
            },

            /** Clear all regions */
            clearRegions() {
                if (state.regionsPlugin) state.regionsPlugin.clearRegions();
                state.activeRegion = null;
            },

            /** Subscribe to events */
            on(event, callback) {
                if (!state.callbacks[event]) state.callbacks[event] = [];
                state.callbacks[event].push(callback);
                return () => {
                    state.callbacks[event] = state.callbacks[event].filter(cb => cb !== callback);
                };
            },

            /** Trim to the currently selected region and reload */
            async trimToRegion() {
                const region = this.getRegion();
                const source = state.currentBlob || state.currentUrl;
                if (!region || !source) return null;
                const blob = await AudioLabCore.trimAudio(source, region.start, region.end);
                await this.reloadFromBlob(blob);
                this.clearRegions();
                fire(state, 'edit', 'trim');
                return blob;
            },

            /** Split audio at current cursor position, returns {before, after} Blobs */
            async splitAtCursor() {
                const source = state.currentBlob || state.currentUrl;
                if (!source) return null;
                const time = state.ws.getCurrentTime();
                if (time <= 0) return null;
                return await AudioLabCore.splitAudio(source, time);
            },

            /** Append another audio source (Blob or URL) to current audio */
            async appendAudio(source) {
                const current = state.currentBlob || state.currentUrl;
                if (!current) return null;
                const blob = await AudioLabCore.concatAudio([current, source]);
                await this.reloadFromBlob(blob);
                fire(state, 'edit', 'concat');
                return blob;
            },

            /** Mix/overlay another audio source with current audio */
            async overlayAudio(source, options = {}) {
                const current = state.currentBlob || state.currentUrl;
                if (!current) return null;
                const blob = await AudioLabCore.mixAudio([current, source], options);
                await this.reloadFromBlob(blob);
                fire(state, 'edit', 'mix');
                return blob;
            },

            /** Export current audio as a WAV Blob */
            async exportBlob() {
                if (state.currentBlob) return state.currentBlob;
                if (!state.currentUrl) return null;
                const resp = await fetch(state.currentUrl);
                return resp.blob();
            },

            /** Get the WaveSurfer instance for advanced usage */
            getWaveSurfer() { return state.ws; },

            /** Get the Regions plugin instance */
            getRegionsPlugin() { return state.regionsPlugin; },

            /** Get the current audio URL */
            getUrl() { return state.currentUrl; },

            /** Check if currently playing */
            isPlaying() { return state.ws.isPlaying(); },

            /** Destroy the player and clean up */
            destroy() {
                state.ws.destroy();
                if (state.currentUrl?.startsWith('blob:')) {
                    URL.revokeObjectURL(state.currentUrl);
                }
                state.currentBlob = null;
                instances.delete(state.id);
            }
        };
    }

    /**
     * Create a compact mini-player for history items (no volume/speed controls).
     */
    function createMini(container, options = {}) {
        return create(container, {
            height: 40,
            compact: true,
            showVolume: false,
            showSpeed: false,
            showDownload: true,
            barWidth: 1,
            barGap: 1,
            ...options
        });
    }

    /**
     * Create a recording visualizer using WaveSurfer Record plugin.
     * @param {string|HTMLElement} container
     * @param {Object} options
     * @returns {Object} Recorder API
     */
    function createRecorder(container, options = {}) {
        const el = typeof container === 'string' ? document.getElementById(container) : container;
        if (!el) return null;

        const id = `alr-${++idCounter}`;
        const playerEl = document.createElement('div');
        playerEl.className = 'audiolab-player audiolab-recorder';
        playerEl.id = id;

        const waveformEl = document.createElement('div');
        waveformEl.className = 'audiolab-player-waveform';
        playerEl.appendChild(waveformEl);

        el.innerHTML = '';
        el.appendChild(playerEl);

        const ws = WaveSurfer.create({
            container: waveformEl,
            height: options.height || 60,
            waveColor: options.waveColor || 'rgba(220, 80, 80, 0.6)',
            progressColor: options.progressColor || 'rgba(220, 80, 80, 0.85)',
            cursorWidth: 0,
            barWidth: 2,
            barGap: 1,
            barRadius: 2,
            interact: false
        });

        let recordPlugin = null;
        if (WaveSurfer.Record) {
            recordPlugin = WaveSurfer.Record.create({
                scrollingWaveform: options.scrolling !== false,
                scrollingWaveformWindow: options.scrollingWindow || 5,
                renderRecordedAudio: options.renderRecorded !== false
            });
            ws.registerPlugin(recordPlugin);
        }

        const callbacks = {};
        const fireEvent = (event, ...args) => {
            if (callbacks[event]) callbacks[event].forEach(cb => cb(...args));
        };

        if (recordPlugin) {
            recordPlugin.on('record-progress', (time) => fireEvent('progress', time));
            recordPlugin.on('record-end', (blob) => fireEvent('end', blob));
        }

        return {
            id,
            async startRecording(deviceId) {
                if (!recordPlugin) throw new Error('Record plugin not available');
                const constraints = deviceId ? { deviceId: { exact: deviceId } } : true;
                await recordPlugin.startRecording({ deviceId: constraints });
            },
            async stopRecording() {
                if (!recordPlugin) throw new Error('Record plugin not available');
                recordPlugin.stopRecording();
            },
            isRecording() {
                return recordPlugin?.isRecording() || false;
            },
            isPaused() {
                return recordPlugin?.isPaused() || false;
            },
            pauseRecording() {
                recordPlugin?.pauseRecording();
            },
            resumeRecording() {
                recordPlugin?.resumeRecording();
            },
            getDuration() {
                return recordPlugin?.getDuration() || 0;
            },
            on(event, cb) {
                if (!callbacks[event]) callbacks[event] = [];
                callbacks[event].push(cb);
            },
            destroy() {
                ws.destroy();
            }
        };
    }

    /** Get a player instance by ID */
    function getById(id) {
        const state = instances.get(id);
        return state ? buildAPI(state) : null;
    }

    /** Destroy all player instances */
    function destroyAll() {
        instances.forEach((state) => {
            state.ws.destroy();
            if (state.currentUrl?.startsWith('blob:')) {
                URL.revokeObjectURL(state.currentUrl);
            }
            state.currentBlob = null;
        });
        instances.clear();
    }

    return {
        create,
        createMini,
        createRecorder,
        getById,
        destroyAll,
        formatTime
    };
})();
