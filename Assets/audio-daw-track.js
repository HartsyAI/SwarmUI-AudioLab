/**
 * AudioDawTrack — Track and clip management for the DAW.
 * Handles track headers (mute/solo/volume/arm), clip lanes, and per-clip WaveSurfer rendering.
 * Reuses SwarmUI utilities: createDiv(), escapeHtml().
 */
const AudioDawTrack = (() => {
    'use strict';

    let trackIdCounter = 0;
    let clipIdCounter = 0;
    // Live zoom (px/sec) read by drag handlers — closures capturing a zoom
    // snapshot went stale after zoom changes and dragged clips the wrong distance.
    let currentZoom = 100;
    const MIN_CLIP_DUR = 0.05; // seconds; trim handles can't shrink a clip below this

    const TRACK_COLORS = [
        '#4a9eff', '#ff6b6b', '#51cf66', '#ffd43b', '#cc5de8',
        '#ff922b', '#22b8cf', '#e64980', '#82c91e', '#7950f2'
    ];

    // ===== CLIP DATA MODEL =====

    /**
     * Create a new clip data object.
     * @param {Blob} blob - Audio data
     * @param {Object} opts - Clip options
     * @returns {Object} Clip data
     */
    function createClip(blob, opts = {}) {
        // A restored clip keeps its id: score versions link by clip id, so a regenerated one orphans the tree.
        const restored = /^clip-(\d+)$/.exec(opts.id || '');
        if (restored) clipIdCounter = Math.max(clipIdCounter, parseInt(restored[1], 10));
        return {
            id: opts.id || `clip-${++clipIdCounter}`,
            blob,
            decodedBuffer: null,      // AudioBuffer, populated after decode
            name: opts.name || `Clip ${clipIdCounter}`,
            startTime: opts.startTime || 0,
            duration: 0,               // set after decode
            offset: 0,                 // internal trim start (seconds into source)
            trimEnd: 0,                // internal trim end (0 = full length)
            gain: 1.0,
            fadeIn: 0,                 // fade-in length in seconds
            fadeOut: 0,                // fade-out length in seconds
            muted: false,
            color: opts.color || null, // override track color
            blobKey: opts.blobKey || `blob-${clipIdCounter}-${Date.now()}`,
            // Free-form per-clip data that outlives undo and reload (currently { score } from the Score tab).
            // Deliberately one bag rather than named fields: clip shape is whitelisted in four places
            // (here, serializeTrack, restoreSnapshot, restoreProject) and each new field had to be added to
            // all four or it vanished on the first undo.
            meta: opts.meta ? JSON.parse(JSON.stringify(opts.meta)) : null
        };
    }

    /**
     * Decode a clip's blob to an AudioBuffer and store it.
     * @param {Object} clip - Clip data object
     * @returns {Promise<AudioBuffer>}
     */
    async function decodeClip(clip) {
        if (clip.decodedBuffer) return clip.decodedBuffer;
        const arrayBuffer = await clip.blob.arrayBuffer();
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        try {
            clip.decodedBuffer = await ctx.decodeAudioData(arrayBuffer);
            clip.duration = clip.decodedBuffer.duration;
        } finally {
            ctx.close();
        }
        return clip.decodedBuffer;
    }

    // ===== TRACK DATA MODEL =====

    /**
     * Create a new track data object.
     * @param {Object} opts - Track options
     * @returns {Object} Track data
     */
    function createTrack(opts = {}) {
        const id = ++trackIdCounter;
        return {
            id: `track-${id}`,
            name: opts.name || `Track ${id}`,
            // instrument tracks play MIDI clips; a bus (aux) has no clips and sums what tracks send it
            kind: ['instrument', 'bus'].includes(opts.kind) ? opts.kind : 'audio',
            instrument: opts.instrument || null,     // AudioDawInstruments.createInstrument() for instrument tracks
            midiClips: [],                           // [{id, name, startTime, length, notes: [{id, pitch, start, dur, vel}], muted, color}]
            clips: [],
            volume: 0.8,
            pan: 0.0,
            muted: false,
            soloed: false,
            armed: false,                            // R: records on the transport's Record
            monitor: false,                          // I: hear the live input through this track
            input: { deviceId: null, channels: 'mono' }, // device (null = system default); 'mono', 'stereo' or a 1-based channel
            takeCount: 0,
            color: opts.color || TRACK_COLORS[(id - 1) % TRACK_COLORS.length],
            height: opts.height || 100,
            collapsed: false,
            fx: [],                                  // ordered [{type, enabled, params}] insert chain
            automation: { volume: [], pan: [] },     // sorted [{t, v}] envelopes
            automationVisible: false,
            automationParam: 'volume',
            automationMode: 'read',                  // 'read', 'touch', 'latch', 'write' or 'off'
            sends: [],                               // [{busId, level, pre, muted}] to aux bus tracks
            // Runtime DOM refs (not serialized)
            headerEl: null,
            laneEl: null,
            clipElements: new Map()  // clipId -> { el, ws }
        };
    }

    // ===== TRACK HEADER UI =====

    /** Short text for a track's input: "Default · Mono", "USB Mic · In 2". */
    function inputText(track, deviceLabel) {
        if (track.kind === 'bus') return translate('AUX') + ' · ' + translate('Sends in');
        if (track.kind === 'instrument') {
            const inst = track.instrument;
            return `MIDI · ${inst?.type === 'sampler' ? (inst.sampleName || translate('Sampler')) : (inst?.preset || translate('Synth'))}`;
        }
        const ch = track.input?.channels ?? 'mono';
        const chText = ch === 'stereo' ? translate('Stereo') : (typeof ch === 'number' ? `${translate('In')} ${ch}` : translate('Mono'));
        const dev = track.input?.deviceId ? (deviceLabel || translate('Input')) : translate('Default');
        return `${dev} · ${chText}`;
    }

    /** Touch, Latch and Write record fader moves during playback. */
    function isWriteMode(mode) {
        return mode === 'touch' || mode === 'latch' || mode === 'write';
    }

    function dbText(v) {
        return v <= 0 ? '-∞' : (20 * Math.log10(v)).toFixed(1);
    }

    /**
     * Build the track header DOM element, laid out like a pro DAW's track column:
     * number + name; R (arm), I (input monitor), S, M, automation, pan, volume; input chooser, inserts, dB; meter.
     * @param {Object} track - Track data
     * @param {Object} callbacks - { onMute, onSolo, onVolume, onPan, onArm, onMonitor, onInputMenu, onOpenFx,
     *   onSelect, onRename, onRecolor, onAutomationToggle, onRemove, onTrackMenu, inputLabel(track) }
     * @param {number} index - position in the track list (shown as the track number)
     * @returns {HTMLElement}
     */
    function buildTrackHeader(track, callbacks = {}, index = 0) {
        const header = createDiv(null, 'daw-track-header');
        header.dataset.trackId = track.id;
        header.style.height = track.height + 'px';
        header.style.borderLeft = `3px solid ${track.color}`;
        header.title = translate('Right-click for track options');
        header.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (callbacks.onTrackMenu) callbacks.onTrackMenu(track, e);
            else if (callbacks.onRecolor) callbacks.onRecolor(track, e);
        });

        // Row 1: number + name + remove
        const topRow = createDiv(null, 'daw-track-name-row');
        const num = createSpan(null, 'daw-track-num');
        num.textContent = String(index + 1);
        topRow.appendChild(num);

        const nameEl = document.createElement('span');
        nameEl.className = 'daw-track-name';
        nameEl.textContent = track.name;
        // Direct translate(), never class="translate": the text is the user's own track name and is
        // reassigned on every rename, so a later sweep would overwrite it from a stale cache.
        nameEl.title = translate('Double-click to rename');
        nameEl.addEventListener('dblclick', () => {
            nameEl.contentEditable = 'true';
            nameEl.focus();
            const range = document.createRange();
            range.selectNodeContents(nameEl);
            window.getSelection().removeAllRanges();
            window.getSelection().addRange(range);
        });
        nameEl.addEventListener('blur', () => {
            nameEl.contentEditable = 'false';
            track.name = nameEl.textContent.trim() || track.name;
            nameEl.textContent = track.name;
            if (callbacks.onRename) callbacks.onRename(track);
        });
        nameEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); nameEl.blur(); }
            if (e.key === 'Escape') { nameEl.textContent = track.name; nameEl.blur(); }
        });
        topRow.appendChild(nameEl);

        const removeBtn = document.createElement('button');
        removeBtn.className = 'daw-track-remove';
        removeBtn.innerHTML = '&#x2715;';
        removeBtn.title = translate('Remove track');
        removeBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (callbacks.onRemove) callbacks.onRemove(track);
        });
        topRow.appendChild(removeBtn);
        header.appendChild(topRow);

        // Row 2: R I S M, automation, pan, volume
        const controls = createDiv(null, 'daw-track-controls');
        const mkBtn = (role, label, title, activeClass, isActive, onClick) => {
            const b = document.createElement('button');
            b.className = 'daw-track-btn' + (isActive ? ' ' + activeClass : '');
            b.dataset.role = role;
            b.textContent = label;
            b.title = title;
            b.addEventListener('click', (e) => {
                e.stopPropagation();
                onClick(b);
            });
            controls.appendChild(b);
            return b;
        };
        if (track.kind !== 'bus') {
            mkBtn('arm', translate('R'), translate('Record arm: records on the transport\'s Record (Shift+R)'), 'active-arm', track.armed, (b) => {
                if (callbacks.onArm) callbacks.onArm(track);
                b.classList.toggle('active-arm', track.armed);
            });
            mkBtn('monitor', translate('I'), translate('Input monitor: hear the live input through this track (I)'), 'active-monitor', track.monitor, (b) => {
                if (callbacks.onMonitor) callbacks.onMonitor(track);
                b.classList.toggle('active-monitor', track.monitor);
            });
        }
        mkBtn('solo', translate('S'), translate('Solo'), 'active-solo', track.soloed, (b) => {
            track.soloed = !track.soloed;
            b.classList.toggle('active-solo', track.soloed);
            if (callbacks.onSolo) callbacks.onSolo(track);
        });
        mkBtn('mute', translate('M'), translate('Mute'), 'active-mute', track.muted, (b) => {
            track.muted = !track.muted;
            b.classList.toggle('active-mute', track.muted);
            if (callbacks.onMute) callbacks.onMute(track);
        });
        const autoBtn = mkBtn('automation', translate('A'), translate('Show/hide the automation lane'), 'active-solo', track.automationVisible, () => {
            if (callbacks.onAutomationToggle) callbacks.onAutomationToggle(track);
        });
        autoBtn.classList.toggle('auto-writing', isWriteMode(track.automationMode));

        // Pan knob (reuses the mixer's knob widget)
        if (typeof AudioDawMixer !== 'undefined' && AudioDawMixer.createKnob) {
            const knob = AudioDawMixer.createKnob({
                value: track.pan || 0, min: -1, max: 1, defaultValue: 0,
                title: translate('Pan (drag up/down, double-click to center)'),
                onChange: (v) => {
                    track.pan = Math.round(v * 20) / 20;
                    if (callbacks.onPan) callbacks.onPan(track);
                },
                onGrab: () => callbacks.onFaderGrab?.(track, 'pan'),
                onRelease: () => callbacks.onFaderRelease?.(track, 'pan')
            });
            knob.classList.add('daw-track-pan-knob');
            knob.dataset.role = 'pan';
            controls.appendChild(knob);
        }

        // Volume slider (compact inline, dB taper shared with the mixer)
        const hasTaper = typeof AudioDawMixer !== 'undefined' && AudioDawMixer.gainToFaderPos;
        const volSlider = document.createElement('input');
        volSlider.type = 'range';
        volSlider.className = 'daw-track-volume';
        volSlider.dataset.role = 'volume';
        volSlider.min = '0';
        volSlider.max = '1';
        volSlider.step = '0.005';
        volSlider.value = hasTaper ? AudioDawMixer.gainToFaderPos(track.volume) : track.volume;
        // Live readout: re-assigned on every drag, so it translates itself here and must stay off class="translate".
        const volTitle = (v) => `${translate('Volume')}: ${dbText(v)} dB`;
        volSlider.title = volTitle(track.volume);
        controls.appendChild(volSlider);
        header.appendChild(controls);

        // Row 3: input chooser (a bus shows what feeds it), inserts, volume readout
        const io = createDiv(null, 'daw-track-io');
        const inBtn = document.createElement('button');
        inBtn.className = 'daw-track-input';
        inBtn.dataset.role = 'input';
        inBtn.textContent = inputText(track, callbacks.inputLabel?.(track));
        inBtn.title = track.kind === 'instrument'
            ? translate('Instrument and MIDI input: the sound this track plays, keyboard and MIDI device')
            : track.kind === 'bus'
                ? translate('Aux bus: tracks send to it from their menu or the Mixer')
                : translate('Input: device and channels this track records and monitors');
        inBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (callbacks.onInputMenu) callbacks.onInputMenu(track, e);
        });
        io.appendChild(inBtn);
        const fxBtn = document.createElement('button');
        fxBtn.className = 'daw-track-fxcount' + (track.fx.length ? ' has-fx' : '');
        fxBtn.dataset.role = 'fx';
        fxBtn.textContent = track.fx.length ? `FX ${track.fx.length}` : 'FX';
        fxBtn.title = track.fx.length
            ? translate('Inserts') + ': ' + track.fx.map(f => f.type).join(', ')
            : translate('No inserts. Click to add effects');
        fxBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (callbacks.onOpenFx) callbacks.onOpenFx(track);
        });
        io.appendChild(fxBtn);
        const volVal = createSpan(null, 'daw-track-vol-val');
        volVal.dataset.role = 'volume-value';
        volVal.textContent = dbText(track.volume);
        volVal.title = translate('Track volume in dB (double-click the slider for 0 dB)');
        io.appendChild(volVal);
        header.appendChild(io);

        volSlider.addEventListener('input', (e) => {
            const p = parseFloat(e.target.value);
            track.volume = hasTaper ? AudioDawMixer.faderPosToGain(p) : p;
            volSlider.title = volTitle(track.volume);
            volVal.textContent = dbText(track.volume);
            if (callbacks.onVolume) callbacks.onVolume(track);
        });
        volSlider.addEventListener('dblclick', () => {
            track.volume = 1;
            volSlider.value = hasTaper ? AudioDawMixer.gainToFaderPos(1) : 1;
            volSlider.title = volTitle(1);
            volVal.textContent = dbText(1);
            if (callbacks.onVolume) callbacks.onVolume(track);
        });
        // Grab and release bracket an automation write (Touch, Latch, Write)
        volSlider.addEventListener('pointerdown', () => callbacks.onFaderGrab?.(track, 'volume'));
        volSlider.addEventListener('change', () => callbacks.onFaderRelease?.(track, 'volume'));
        // A press with no movement, or a double-click reset, fires no change event: release on pointer up as well
        volSlider.addEventListener('pointerup', () => callbacks.onFaderRelease?.(track, 'volume'));
        volSlider.addEventListener('pointercancel', () => callbacks.onFaderRelease?.(track, 'volume'));

        // Live stereo meter (OBS-style: zone gradient lights up to the level, L/R bars)
        const meter = createDiv(null, 'daw-meter-hpair daw-track-meter-pair');
        track.meterChans = [];
        for (let ch = 0; ch < 2; ch++) {
            const bar = createDiv(null, 'daw-meter-h');
            const mask = createDiv(null, 'daw-meter-h-mask');
            const peakTick = createDiv(null, 'daw-meter-h-peak');
            bar.appendChild(mask);
            bar.appendChild(peakTick);
            meter.appendChild(bar);
            track.meterChans.push({ mask, peak: peakTick });
        }
        header.appendChild(meter);
        header.classList.toggle('armed', !!track.armed);
        header.classList.toggle('instrument', track.kind === 'instrument');
        header.classList.toggle('bus', track.kind === 'bus');

        // Click to select track
        header.addEventListener('click', () => {
            if (callbacks.onSelect) callbacks.onSelect(track);
        });

        track.headerEl = header;
        track.headerCallbacks = callbacks;
        return header;
    }

    // ===== CLIP LANE UI =====

    /**
     * Build the clip lane DOM element for a track.
     * @param {Object} track - Track data
     * @param {number} zoom - Current zoom (px/sec)
     * @param {Object} callbacks - { onClipSelect, onClipMove, onClipContext }
     * @returns {HTMLElement}
     */
    function buildClipLane(track, zoom, callbacks = {}) {
        const lane = createDiv(null, 'daw-track-lane');
        lane.dataset.trackId = track.id;
        lane.style.height = track.height + 'px';
        track.laneEl = lane;
        return lane;
    }

    /**
     * Apply a clip's timeline position, trimmed width, and waveform alignment to its element.
     * The WaveSurfer canvas renders the FULL source blob; shifting it left by offset*zoom
     * inside the overflow-hidden clip element shows exactly the trimmed window.
     */
    function applyClipLayout(el, clip) {
        const vis = clip.duration - clip.offset - clip.trimEnd;
        const width = Math.max(vis * currentZoom, 4);
        el.style.left = (clip.startTime * currentZoom) + 'px';
        el.style.width = width + 'px';
        const wsC = el.querySelector('.daw-clip-waveform');
        if (wsC) {
            wsC.style.width = (clip.duration * currentZoom) + 'px';
            wsC.style.transform = `translateX(${-clip.offset * currentZoom}px)`;
        }
        // Fade shapes and their corner handles
        const fiPx = Math.min(width, (clip.fadeIn || 0) * currentZoom);
        const foPx = Math.min(width - fiPx, (clip.fadeOut || 0) * currentZoom);
        const fadeIn = el.querySelector('.daw-clip-fade-in');
        const fadeOut = el.querySelector('.daw-clip-fade-out');
        if (fadeIn) {
            fadeIn.style.width = fiPx + 'px';
            fadeIn.style.display = fiPx > 0.5 ? '' : 'none';
        }
        if (fadeOut) {
            fadeOut.style.width = foPx + 'px';
            fadeOut.style.display = foPx > 0.5 ? '' : 'none';
        }
        const hIn = el.querySelector('.daw-clip-fade-handle-in');
        const hOut = el.querySelector('.daw-clip-fade-handle-out');
        if (hIn) hIn.style.left = Math.max(0, fiPx - 5) + 'px';
        if (hOut) hOut.style.right = Math.max(0, foPx - 5) + 'px';
        const gainEl = el.querySelector('.daw-clip-gain');
        if (gainEl) gainEl.textContent = clipGainText(clip);
    }

    /** Pro-DAW style clip gain readout: "0 dB", "+2.5 dB", "-6 dB". */
    function clipGainText(clip) {
        const g = clip.gain ?? 1;
        if (g <= 0) return '-∞ dB';
        const db = 20 * Math.log10(g);
        if (Math.abs(db) < 0.05) return '0 dB';
        return (db > 0 ? '+' : '') + db.toFixed(1) + ' dB';
    }

    /**
     * Render (or re-render) all clips in a track's lane.
     * @param {Object} track - Track data
     * @param {number} zoom - Pixels per second
     * @param {Object} callbacks - { onClipSelect, onClipMove, onClipContext }
     * @param {string|null} selectedClipId - Currently selected clip ID
     */
    function renderClips(track, zoom, callbacks = {}, selectedClipId = null) {
        if (!track.laneEl) return;
        currentZoom = zoom;

        // Remove clip elements no longer in track.clips
        const currentClipIds = new Set(track.clips.map(c => c.id));
        for (const [clipId, entry] of track.clipElements) {
            if (!currentClipIds.has(clipId)) {
                if (entry.ws) entry.ws.destroy();
                entry.el.remove();
                track.clipElements.delete(clipId);
            }
        }

        // Update or create each clip element
        for (const clip of track.clips) {
            let entry = track.clipElements.get(clip.id);

            if (!entry) {
                // Create new clip element
                const clipEl = createDiv(null, 'daw-clip');
                clipEl.dataset.clipId = clip.id;
                clipEl.dataset.trackId = track.id;

                // Label
                const label = createSpan(null, 'daw-clip-label');
                label.textContent = clip.name;
                clipEl.appendChild(label);

                // Waveform container
                const wsContainer = createDiv(null, 'daw-clip-waveform');
                clipEl.appendChild(wsContainer);

                // Fade shapes (the shaded, attenuated corners) and their drag handles
                const fadeInShape = createDiv(null, 'daw-clip-fade daw-clip-fade-in');
                const fadeOutShape = createDiv(null, 'daw-clip-fade daw-clip-fade-out');
                clipEl.appendChild(fadeInShape);
                clipEl.appendChild(fadeOutShape);
                const fadeInHandle = createDiv(null, 'daw-clip-fade-handle daw-clip-fade-handle-in');
                fadeInHandle.title = translate('Drag to set the fade-in');
                const fadeOutHandle = createDiv(null, 'daw-clip-fade-handle daw-clip-fade-handle-out');
                fadeOutHandle.title = translate('Drag to set the fade-out');
                clipEl.appendChild(fadeInHandle);
                clipEl.appendChild(fadeOutHandle);
                setupFadeDrag(fadeInHandle, 'in', clipEl, clip, track, callbacks);
                setupFadeDrag(fadeOutHandle, 'out', clipEl, clip, track, callbacks);

                // Clip gain readout, drag up/down to change it
                const gainEl = createDiv(null, 'daw-clip-gain');
                gainEl.title = translate('Clip gain: drag up or down, double-click for 0 dB');
                clipEl.appendChild(gainEl);
                setupGainDrag(gainEl, clipEl, clip, track, callbacks);

                // Trim handles (left = offset, right = trimEnd)
                const trimL = createDiv(null, 'daw-clip-trim daw-clip-trim-left');
                trimL.title = translate('Drag to trim clip start');
                const trimR = createDiv(null, 'daw-clip-trim daw-clip-trim-right');
                trimR.title = translate('Drag to trim clip end');
                clipEl.appendChild(trimL);
                clipEl.appendChild(trimR);
                setupTrimDrag(trimL, 'left', clipEl, clip, track, callbacks);
                setupTrimDrag(trimR, 'right', clipEl, clip, track, callbacks);

                track.laneEl.appendChild(clipEl);

                // Create WaveSurfer for waveform rendering (visual only)
                let ws = null;
                if (clip.blob) {
                    ws = WaveSurfer.create({
                        container: wsContainer,
                        height: track.height - 8,
                        waveColor: clip.color || track.color,
                        progressColor: 'transparent',
                        cursorWidth: 0,
                        barWidth: 1,
                        barGap: 1,
                        barRadius: 1,
                        interact: false,
                        hideScrollbar: true,
                        normalize: true,
                        minPxPerSec: zoom
                    });
                    ws.loadBlob(clip.blob);
                }

                entry = { el: clipEl, ws, wsContainer };
                track.clipElements.set(clip.id, entry);

                // Click to select
                clipEl.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (callbacks.onClipSelect) callbacks.onClipSelect(clip, track);
                });

                // Right-click context menu
                clipEl.addEventListener('contextmenu', (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    if (callbacks.onClipContext) callbacks.onClipContext(e, clip, track);
                });

                // Drag to reposition
                setupClipDrag(clipEl, clip, track, zoom, callbacks);
            }

            // Re-attach to lane if removed (e.g., renderAllTracks rebuilds lanes)
            if (entry.el.parentElement !== track.laneEl) {
                track.laneEl.appendChild(entry.el);
            }

            // Update position, size, waveform alignment, and colors (track may be recolored)
            applyClipLayout(entry.el, clip);
            // Opaque, so a later clip visibly covers the one below it (it is also the one heard)
            entry.el.style.backgroundColor = `color-mix(in srgb, ${clip.color || track.color} 28%, var(--background-soft))`;
            entry.el.style.zIndex = String(2 + track.clips.indexOf(clip));
            if (entry.ws) {
                try { entry.ws.setOptions({ waveColor: clip.color || track.color }); } catch (_) {}
            }

            // Selected state
            entry.el.classList.toggle('selected', clip.id === selectedClipId);

            // Muted visual
            entry.el.classList.toggle('clip-muted', clip.muted);
        }
    }

    /**
     * Set up drag-to-reposition on a clip element.
     * Supports horizontal repositioning + cross-track dragging (vertical).
     */
    function setupClipDrag(clipEl, clip, track, zoom, callbacks) {
        let dragStartX = 0;
        let dragStartY = 0;
        let dragStartTime = 0;
        let isDragging = false;
        let dragTargetLane = null;
        let outsideDrop = false;

        clipEl.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            // The range tool selects across clips instead; let the lanes take the gesture
            if (callbacks.isRangeTool && callbacks.isRangeTool()) return;
            isDragging = false;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            dragStartTime = clip.startTime;
            dragTargetLane = null;
            outsideDrop = false;
            clipEl.setPointerCapture(e.pointerId);

            // Edge auto-scroll: dragging against the viewport edge scrolls the
            // lanes and (rightward) extends the timeline so clips can be placed
            // past the current end, like any DAW arrange view.
            const scroller = document.getElementById('daw_clip_lanes');
            const scrollBase = scroller ? scroller.scrollLeft : 0;
            let lastClientX = e.clientX;
            let edgeDir = 0;
            let edgeRaf = null;

            const applyMove = () => {
                const scrollDelta = scroller ? scroller.scrollLeft - scrollBase : 0;
                let newTime = Math.max(0, dragStartTime + (lastClientX - dragStartX + scrollDelta) / currentZoom);
                if (callbacks.snapTime) newTime = Math.max(0, callbacks.snapTime(newTime));
                clip.startTime = newTime;
                clipEl.style.left = (newTime * currentZoom) + 'px';
            };

            const edgeTick = () => {
                edgeRaf = null;
                if (!edgeDir || !scroller) return;
                scroller.scrollLeft += edgeDir * 14;
                if (edgeDir > 0 && callbacks.onTimelineExtend
                    && scroller.scrollLeft + scroller.clientWidth >= scroller.scrollWidth - 40) {
                    callbacks.onTimelineExtend((scroller.scrollWidth + 300) / currentZoom);
                }
                applyMove();
                edgeRaf = requestAnimationFrame(edgeTick);
            };

            const onMove = (me) => {
                const dx = me.clientX - dragStartX;
                const dy = me.clientY - dragStartY;
                if (!isDragging && (Math.abs(dx) > 3 || Math.abs(dy) > 3)) {
                    isDragging = true;
                    clipEl.classList.add('dragging');
                    // Snapshot for undo once per real drag gesture, before any movement applies
                    if (callbacks.onClipDragStart) callbacks.onClipDragStart(clip, track);
                }
                if (!isDragging) return;

                // Horizontal: reposition in time (currentZoom, NOT the creation-time zoom)
                lastClientX = me.clientX;
                applyMove();
                if (scroller) {
                    const r = scroller.getBoundingClientRect();
                    edgeDir = me.clientX > r.right - 40 ? 1
                        : (me.clientX < r.left + 40 && scroller.scrollLeft > 0 ? -1 : 0);
                    if (edgeDir && !edgeRaf) edgeRaf = requestAnimationFrame(edgeTick);
                }

                // A pane below the timeline can claim the drag instead (the Score sheet reads what it is given).
                outsideDrop = callbacks.onClipDragOver ? !!callbacks.onClipDragOver(me.clientX, me.clientY) : false;

                // Vertical: detect target track lane for cross-track drag
                const lanes = document.querySelectorAll('.daw-track-lane');
                dragTargetLane = null;
                for (const lane of lanes) {
                    const rect = lane.getBoundingClientRect();
                    if (me.clientY >= rect.top && me.clientY <= rect.bottom) {
                        dragTargetLane = lane;
                        break;
                    }
                }
                // Visual feedback: highlight target lane
                lanes.forEach(l => l.classList.remove('daw-drop-target'));
                if (dragTargetLane && dragTargetLane !== track.laneEl) {
                    dragTargetLane.classList.add('daw-drop-target');
                }
            };

            const onUp = (ue) => {
                edgeDir = 0;
                if (edgeRaf) { cancelAnimationFrame(edgeRaf); edgeRaf = null; }
                try { clipEl.releasePointerCapture(ue.pointerId); } catch (_) {}
                clipEl.removeEventListener('pointermove', onMove);
                clipEl.removeEventListener('pointerup', onUp);
                clipEl.removeEventListener('pointercancel', onUp);
                clipEl.classList.remove('dragging');
                document.querySelectorAll('.daw-track-lane').forEach(l => l.classList.remove('daw-drop-target'));
                if (callbacks.onClipDragOver) callbacks.onClipDragOver(-1, -1);

                if (isDragging) {
                    // Dropped off the timeline: the clip was read, not moved, so it goes back where it was.
                    // A cancelled touch has no drop position, so it is never a drop off the timeline
                    if (outsideDrop && ue.type !== 'pointercancel' && callbacks.onClipDropOutside
                        && callbacks.onClipDropOutside(clip, track, ue.clientX, ue.clientY)) {
                        clip.startTime = dragStartTime;
                        applyClipLayout(clipEl, clip);
                        if (callbacks.onClipMove) callbacks.onClipMove(clip, track);
                        return;
                    }
                    // Check for cross-track move
                    if (dragTargetLane && dragTargetLane !== track.laneEl) {
                        const targetTrackId = dragTargetLane.dataset.trackId;
                        if (targetTrackId && callbacks.onClipCrossTrack) {
                            callbacks.onClipCrossTrack(clip, track, targetTrackId);
                            return;
                        }
                    }
                    if (callbacks.onClipMove) {
                        callbacks.onClipMove(clip, track);
                    }
                }
            };

            clipEl.addEventListener('pointermove', onMove);
            clipEl.addEventListener('pointerup', onUp);
            clipEl.addEventListener('pointercancel', onUp);
        });
    }

    /**
     * Drag a clip edge to trim it. Left handle adjusts offset (start of the source
     * window) and shifts startTime so the remaining audio stays anchored on the
     * timeline; right handle adjusts trimEnd. Undo snapshots via onClipTrimStart.
     */
    function setupTrimDrag(handle, side, clipEl, clip, track, callbacks) {
        handle.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            if (callbacks.isRangeTool && callbacks.isRangeTool()) return;
            e.stopPropagation();
            e.preventDefault();
            handle.setPointerCapture(e.pointerId);
            const startX = e.clientX;
            const start = { offset: clip.offset, trimEnd: clip.trimEnd, startTime: clip.startTime };
            let moved = false;
            const onMove = (me) => {
                if (!moved && Math.abs(me.clientX - startX) > 2) {
                    moved = true;
                    if (callbacks.onClipTrimStart) callbacks.onClipTrimStart(clip, track);
                }
                if (!moved) return;
                const dSec = (me.clientX - startX) / currentZoom;
                if (side === 'left') {
                    // Snap the visible left edge (timeline position), then derive offset
                    let edge = start.startTime + dSec;
                    if (callbacks.snapTime) edge = callbacks.snapTime(edge);
                    // Offset can't push startTime below 0, nor eat past the min duration
                    const minOffset = Math.max(0, start.offset - start.startTime);
                    const maxOffset = clip.duration - start.trimEnd - MIN_CLIP_DUR;
                    const newOffset = Math.max(minOffset, Math.min(start.offset + (edge - start.startTime), maxOffset));
                    clip.startTime = start.startTime + (newOffset - start.offset);
                    clip.offset = newOffset;
                } else {
                    // Snap the visible right edge, then derive trimEnd
                    const startEdge = start.startTime + (clip.duration - start.offset - start.trimEnd);
                    let edge = startEdge + dSec;
                    if (callbacks.snapTime) edge = callbacks.snapTime(edge);
                    const maxTrim = clip.duration - start.offset - MIN_CLIP_DUR;
                    const newTrim = clip.duration - start.offset - (edge - start.startTime);
                    clip.trimEnd = Math.max(0, Math.min(newTrim, maxTrim));
                }
                applyClipLayout(clipEl, clip);
            };
            const onUp = (ue) => {
                try { handle.releasePointerCapture(ue.pointerId); } catch (_) {}
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                if (moved && callbacks.onClipTrimEnd) callbacks.onClipTrimEnd(clip, track);
            };
            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
            handle.addEventListener('pointercancel', onUp);
        });
    }

    /**
     * Drag a clip's top corner to set its fade. The fade-in runs from the clip start, the fade-out to its end;
     * the two never overlap. Undo snapshots via onClipFadeStart, rescheduling via onClipFadeEnd.
     */
    function setupFadeDrag(handle, side, clipEl, clip, track, callbacks) {
        handle.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            if (callbacks.isRangeTool && callbacks.isRangeTool()) return;
            e.stopPropagation();
            e.preventDefault();
            handle.setPointerCapture(e.pointerId);
            const startX = e.clientX;
            const start = { fadeIn: clip.fadeIn || 0, fadeOut: clip.fadeOut || 0 };
            let moved = false;
            const onMove = (me) => {
                if (!moved && Math.abs(me.clientX - startX) > 2) {
                    moved = true;
                    if (callbacks.onClipFadeStart) callbacks.onClipFadeStart(clip, track);
                }
                if (!moved) return;
                const vis = clip.duration - clip.offset - clip.trimEnd;
                const dSec = (me.clientX - startX) / currentZoom;
                if (side === 'in') {
                    clip.fadeIn = Math.max(0, Math.min(vis - (clip.fadeOut || 0), start.fadeIn + dSec));
                } else {
                    clip.fadeOut = Math.max(0, Math.min(vis - (clip.fadeIn || 0), start.fadeOut - dSec));
                }
                applyClipLayout(clipEl, clip);
            };
            const onUp = (ue) => {
                try { handle.releasePointerCapture(ue.pointerId); } catch (_) {}
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                if (moved && callbacks.onClipFadeEnd) callbacks.onClipFadeEnd(clip, track);
            };
            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
            handle.addEventListener('pointercancel', onUp);
        });
        handle.addEventListener('click', (e) => e.stopPropagation());
    }

    /** Drag the clip gain readout up/down (0.25 dB per pixel, -24 to +12 dB); double-click resets to 0 dB. */
    function setupGainDrag(gainEl, clipEl, clip, track, callbacks) {
        gainEl.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            if (callbacks.isRangeTool && callbacks.isRangeTool()) return;
            e.stopPropagation();
            e.preventDefault();
            gainEl.setPointerCapture(e.pointerId);
            const startY = e.clientY;
            const g0 = clip.gain ?? 1;
            const db0 = g0 > 0 ? 20 * Math.log10(g0) : -24;
            let moved = false;
            const onMove = (me) => {
                if (!moved && Math.abs(me.clientY - startY) > 2) {
                    moved = true;
                    if (callbacks.onClipGainStart) callbacks.onClipGainStart(clip, track);
                }
                if (!moved) return;
                const db = Math.max(-24, Math.min(12, db0 + (startY - me.clientY) * 0.25));
                clip.gain = Math.pow(10, db / 20);
                gainEl.textContent = clipGainText(clip);
                if (callbacks.onClipGainChange) callbacks.onClipGainChange(clip, track);
            };
            const onUp = (ue) => {
                try { gainEl.releasePointerCapture(ue.pointerId); } catch (_) {}
                gainEl.removeEventListener('pointermove', onMove);
                gainEl.removeEventListener('pointerup', onUp);
                gainEl.removeEventListener('pointercancel', onUp);
                if (moved && callbacks.onClipGainEnd) callbacks.onClipGainEnd(clip, track);
            };
            gainEl.addEventListener('pointermove', onMove);
            gainEl.addEventListener('pointerup', onUp);
            gainEl.addEventListener('pointercancel', onUp);
        });
        gainEl.addEventListener('click', (e) => e.stopPropagation());
        gainEl.addEventListener('dblclick', (e) => {
            e.stopPropagation();
            if (callbacks.onClipGainStart) callbacks.onClipGainStart(clip, track);
            clip.gain = 1;
            gainEl.textContent = clipGainText(clip);
            if (callbacks.onClipGainChange) callbacks.onClipGainChange(clip, track);
            if (callbacks.onClipGainEnd) callbacks.onClipGainEnd(clip, track);
        });
    }

    /**
     * Update all WaveSurfer instances in a track for a new zoom level.
     * @param {Object} track - Track data
     * @param {number} zoom - New pixels per second
     */
    function updateZoom(track, zoom) {
        currentZoom = zoom;
        for (const clip of track.clips) {
            const entry = track.clipElements.get(clip.id);
            if (!entry) continue;
            applyClipLayout(entry.el, clip);
            // WaveSurfer zoom update
            if (entry.ws) {
                try { entry.ws.zoom(zoom); } catch (_) { /* ignore if not ready */ }
            }
        }
        // Update lane/header height if changed
        if (track.laneEl) track.laneEl.style.height = track.height + 'px';
        if (track.headerEl) track.headerEl.style.height = track.height + 'px';
    }

    /**
     * Refresh a track header's mute/solo/volume controls from track state
     * (used when the mixer changes them, to avoid a full re-render).
     */
    function syncHeaderControls(track) {
        if (!track.headerEl) return;
        const q = (role) => track.headerEl.querySelector(`[data-role="${role}"]`);
        q('mute')?.classList.toggle('active-mute', track.muted);
        q('solo')?.classList.toggle('active-solo', track.soloed);
        q('arm')?.classList.toggle('active-arm', !!track.armed);
        q('monitor')?.classList.toggle('active-monitor', !!track.monitor);
        track.headerEl.classList.toggle('armed', !!track.armed);
        const volSlider = q('volume');
        if (volSlider) {
            volSlider.value = (typeof AudioDawMixer !== 'undefined' && AudioDawMixer.gainToFaderPos)
                ? AudioDawMixer.gainToFaderPos(track.volume) : track.volume;
        }
        const volVal = q('volume-value');
        if (volVal) volVal.textContent = dbText(track.volume);
        q('pan')?.setValue?.(track.pan || 0);
        q('automation')?.classList.toggle('auto-writing', isWriteMode(track.automationMode));
        const inBtn = q('input');
        if (inBtn) inBtn.textContent = inputText(track, track.headerCallbacks?.inputLabel?.(track));
        const fxBtn = q('fx');
        if (fxBtn) {
            fxBtn.textContent = track.fx.length ? `FX ${track.fx.length}` : 'FX';
            fxBtn.classList.toggle('has-fx', track.fx.length > 0);
            fxBtn.title = track.fx.length
                ? translate('Inserts') + ': ' + track.fx.map(f => f.type).join(', ')
                : translate('No inserts. Click to add effects');
        }
    }

    const AUTO_LANE_H = 40;
    const SVG_NS = 'http://www.w3.org/2000/svg';

    /** Value<->y mapping per automation param. */
    function autoToY(param, v) {
        return param === 'volume' ? AUTO_LANE_H * (1 - v / 2) : AUTO_LANE_H * (1 - (v + 1) / 2);
    }
    function autoFromY(param, y) {
        const n = 1 - Math.max(0, Math.min(AUTO_LANE_H, y)) / AUTO_LANE_H;
        return param === 'volume' ? Math.max(0, Math.min(2, n * 2)) : Math.max(-1, Math.min(1, n * 2 - 1));
    }

    /**
     * Build (or rebuild) a track's automation sub-lane: an SVG breakpoint editor.
     * Click empty = add point (snapped); drag handle = move; double-click handle = delete.
     * callbacks: { snapTime, onAutomationGestureStart(track), onAutomationChange(track) }
     */
    function buildAutomationLane(track, zoom, callbacks) {
        const lane = createDiv(null, 'daw-automation-lane');
        lane.dataset.trackId = track.id;
        track.autoLaneEl = lane;
        renderAutomationLane(track, zoom, callbacks);
        return lane;
    }

    /** Polyline points for an automation lane, extended flat to both lane edges. */
    function automationLinePoints(points, param, zoom) {
        const pts = points.map(p => `${p.t * zoom},${autoToY(param, p.v)}`);
        return `0,${autoToY(param, points[0].v)} ` + pts.join(' ') + ` 100000,${autoToY(param, points[points.length - 1].v)}`;
    }

    function renderAutomationLane(track, zoom, callbacks) {
        const lane = track.autoLaneEl;
        if (!lane) return;
        lane.innerHTML = '';
        const param = track.automationParam || 'volume';
        const points = track.automation[param];
        const dotEls = new Map(); // point -> its circle, so a segment drag can move the circles in place
        const svg = document.createElementNS(SVG_NS, 'svg');
        svg.setAttribute('height', AUTO_LANE_H);
        svg.setAttribute('width', '100%');
        svg.classList.add('daw-automation-svg');

        // Identity baseline (v=1 for volume, 0 offset for pan)
        const base = document.createElementNS(SVG_NS, 'line');
        const baseY = autoToY(param, param === 'volume' ? 1 : 0);
        base.setAttribute('x1', 0);
        base.setAttribute('x2', '100%');
        base.setAttribute('y1', baseY);
        base.setAttribute('y2', baseY);
        base.classList.add('daw-automation-baseline');
        svg.appendChild(base);

        // Suppresses the click-to-add that follows a segment drag; kept on the track because the drag re-renders the lane

        if (points.length) {
            const poly = document.createElementNS(SVG_NS, 'polyline');
            poly.setAttribute('points', automationLinePoints(points, param, zoom));
            poly.classList.add('daw-automation-line');
            svg.appendChild(poly);

            // One-gesture section edit: grab the line between two points and pull it up/down
            poly.addEventListener('pointerdown', (e) => {
                if (e.button !== 0) return;
                e.stopPropagation();
                e.preventDefault();
                poly.setPointerCapture(e.pointerId);
                const rect = svg.getBoundingClientRect();
                const t = (e.clientX - rect.left) / currentZoom;
                // bracketing points (grabbing before the first / after the last drags that one point)
                let left = null, right = null;
                for (const p of points) {
                    if (p.t <= t) left = p; else { right = p; break; }
                }
                const targets = [left, right].filter(Boolean);
                const startVals = targets.map(p => p.v);
                const startY = e.clientY;
                let moved = false;
                const range = param === 'volume' ? 2 : 2; // full lane height spans this much value
                const onMove = (me) => {
                    if (!moved) {
                        moved = true;
                        track.autoSegmentDragged = true;
                        if (callbacks.onAutomationGestureStart) callbacks.onAutomationGestureStart(track);
                    }
                    const dv = (startY - me.clientY) / AUTO_LANE_H * range;
                    targets.forEach((p, i) => {
                        p.v = param === 'volume'
                            ? Math.max(0, Math.min(2, startVals[i] + dv))
                            : Math.max(-1, Math.min(1, startVals[i] + dv));
                    });
                    // Move the line and the two circles in place; the lane is rebuilt once, on pointer up
                    poly.setAttribute('points', automationLinePoints(points, param, currentZoom));
                    for (const p of targets) {
                        const d = dotEls.get(p);
                        if (d) d.setAttribute('cy', autoToY(param, p.v));
                    }
                };
                // Window listeners keep the drag alive; they are removed on pointer up
                const onUp = (ue) => {
                    try { poly.releasePointerCapture(ue.pointerId); } catch (_) {}
                    window.removeEventListener('pointermove', onMove);
                    window.removeEventListener('pointerup', onUp);
                    window.removeEventListener('pointercancel', onUp);
                    if (moved) renderAutomationLane(track, currentZoom, callbacks);
                    if (moved && callbacks.onAutomationChange) callbacks.onAutomationChange(track);
                    setTimeout(() => { track.autoSegmentDragged = false; }, 0);
                };
                window.addEventListener('pointermove', onMove);
                window.addEventListener('pointerup', onUp);
                window.addEventListener('pointercancel', onUp);
            });
        }

        const sortPoints = () => points.sort((a, b) => a.t - b.t);

        points.forEach((pt) => {
            const dot = document.createElementNS(SVG_NS, 'circle');
            dot.setAttribute('cx', pt.t * zoom);
            dot.setAttribute('cy', autoToY(param, pt.v));
            dot.setAttribute('r', 4);
            dot.classList.add('daw-automation-dot');
            dotEls.set(pt, dot);
            dot.addEventListener('dblclick', (e) => {
                e.stopPropagation();
                if (callbacks.onAutomationGestureStart) callbacks.onAutomationGestureStart(track);
                const i = points.indexOf(pt);
                if (i >= 0) points.splice(i, 1);
                renderAutomationLane(track, currentZoom, callbacks);
                if (callbacks.onAutomationChange) callbacks.onAutomationChange(track);
            });
            dot.addEventListener('pointerdown', (e) => {
                if (e.button !== 0) return;
                e.stopPropagation();
                e.preventDefault();
                dot.setPointerCapture(e.pointerId);
                let moved = false;
                const rect = svg.getBoundingClientRect();
                const onMove = (me) => {
                    if (!moved) {
                        moved = true;
                        if (callbacks.onAutomationGestureStart) callbacks.onAutomationGestureStart(track);
                    }
                    let t = (me.clientX - rect.left) / currentZoom;
                    if (callbacks.snapTime) t = callbacks.snapTime(t);
                    pt.t = Math.max(0, t);
                    pt.v = autoFromY(param, me.clientY - rect.top);
                    dot.setAttribute('cx', pt.t * currentZoom);
                    dot.setAttribute('cy', autoToY(param, pt.v));
                };
                const onUp = (ue) => {
                    try { dot.releasePointerCapture(ue.pointerId); } catch (_) {}
                    dot.removeEventListener('pointermove', onMove);
                    dot.removeEventListener('pointerup', onUp);
                    dot.removeEventListener('pointercancel', onUp);
                    if (moved) {
                        sortPoints();
                        renderAutomationLane(track, currentZoom, callbacks);
                        if (callbacks.onAutomationChange) callbacks.onAutomationChange(track);
                    }
                };
                dot.addEventListener('pointermove', onMove);
                dot.addEventListener('pointerup', onUp);
                dot.addEventListener('pointercancel', onUp);
            });
            svg.appendChild(dot);
        });

        // Click empty space to add a point (line clicks are segment drags, handled above)
        svg.addEventListener('click', (e) => {
            if (track.autoSegmentDragged) return;
            if (e.target !== svg && e.target !== base) return;
            const rect = svg.getBoundingClientRect();
            let t = (e.clientX - rect.left) / currentZoom;
            if (callbacks.snapTime) t = callbacks.snapTime(t);
            if (callbacks.onAutomationGestureStart) callbacks.onAutomationGestureStart(track);
            points.push({ t: Math.max(0, t), v: autoFromY(param, e.clientY - rect.top) });
            sortPoints();
            renderAutomationLane(track, currentZoom, callbacks);
            if (callbacks.onAutomationChange) callbacks.onAutomationChange(track);
        });

        lane.appendChild(svg);
    }

    /**
     * Destroy all WaveSurfer instances in a track and clean up DOM.
     */
    function destroyTrack(track) {
        for (const [, entry] of track.clipElements) {
            if (entry.ws) entry.ws.destroy();
            entry.el.remove();
        }
        track.clipElements.clear();
        if (track.headerEl) { track.headerEl.remove(); track.headerEl = null; }
        if (track.laneEl) { track.laneEl.remove(); track.laneEl = null; }
        if (track.autoLaneEl) { track.autoLaneEl.remove(); track.autoLaneEl = null; }
    }

    /**
     * Serialize track state to a plain object (for undo snapshots).
     */
    function serializeTrack(track) {
        return {
            id: track.id,
            name: track.name,
            volume: track.volume,
            pan: track.pan,
            muted: track.muted,
            soloed: track.soloed,
            armed: track.armed,
            monitor: !!track.monitor,
            input: { deviceId: track.input?.deviceId || null, channels: track.input?.channels ?? 'mono' },
            takeCount: track.takeCount || 0,
            kind: track.kind || 'audio',
            instrument: track.instrument ? JSON.parse(JSON.stringify(track.instrument)) : null,
            midiClips: (track.midiClips || []).map(c => ({ ...c, notes: c.notes.map(n => ({ ...n })), cc: (c.cc || []).map(e => ({ ...e })) })),
            color: track.color,
            height: track.height,
            // Deep copies: undo snapshots must never alias the live objects
            fx: track.fx.map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } })),
            automation: {
                volume: track.automation.volume.map(p => ({ ...p })),
                pan: track.automation.pan.map(p => ({ ...p }))
            },
            automationVisible: track.automationVisible,
            automationParam: track.automationParam,
            automationMode: track.automationMode || 'read',
            sends: (track.sends || []).map(sd => ({ ...sd })),
            clips: track.clips.map(c => ({
                id: c.id,
                blobKey: c.blobKey,
                name: c.name,
                startTime: c.startTime,
                duration: c.duration,
                offset: c.offset,
                trimEnd: c.trimEnd,
                gain: c.gain,
                fadeIn: c.fadeIn,
                fadeOut: c.fadeOut,
                muted: c.muted,
                color: c.color,
                meta: c.meta ? JSON.parse(JSON.stringify(c.meta)) : null
            }))
        };
    }

    /**
     * Compute the total duration of a track (furthest clip end time).
     */
    function getTrackDuration(track) {
        let max = 0;
        for (const clip of track.clips) {
            const end = clip.startTime + (clip.duration - clip.offset - clip.trimEnd);
            if (end > max) max = end;
        }
        return max;
    }

    // ===== HELPERS =====

    function hexToRgba(hex, alpha) {
        if (!hex || hex.startsWith('rgba')) return hex;
        const r = parseInt(hex.slice(1, 3), 16);
        const g = parseInt(hex.slice(3, 5), 16);
        const b = parseInt(hex.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }

    return {
        COLORS: TRACK_COLORS,
        createClip,
        decodeClip,
        createTrack,
        buildTrackHeader,
        buildClipLane,
        renderClips,
        syncHeaderControls,
        buildAutomationLane,
        renderAutomationLane,
        updateZoom,
        destroyTrack,
        serializeTrack,
        getTrackDuration,
        hexToRgba
    };
})();
