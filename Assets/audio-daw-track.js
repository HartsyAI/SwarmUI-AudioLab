/**
 * AudioDawTrack — Track and clip management for the DAW.
 * Handles track headers (mute/solo/volume/arm), clip lanes, and per-clip WaveSurfer rendering.
 * Reuses SwarmUI utilities: createDiv(), escapeHtml().
 */
const AudioDawTrack = (() => {
    'use strict';

    let trackIdCounter = 0;
    let clipIdCounter = 0;

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
        return {
            id: `clip-${++clipIdCounter}`,
            blob,
            decodedBuffer: null,      // AudioBuffer, populated after decode
            name: opts.name || `Clip ${clipIdCounter}`,
            startTime: opts.startTime || 0,
            duration: 0,               // set after decode
            offset: 0,                 // internal trim start (seconds into source)
            trimEnd: 0,                // internal trim end (0 = full length)
            gain: 1.0,
            muted: false,
            color: opts.color || null, // override track color
            blobKey: opts.blobKey || `blob-${clipIdCounter}-${Date.now()}`
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
            clips: [],
            volume: 0.8,
            pan: 0.0,
            muted: false,
            soloed: false,
            armed: false,
            color: opts.color || TRACK_COLORS[(id - 1) % TRACK_COLORS.length],
            height: opts.height || 80,
            collapsed: false,
            // Runtime DOM refs (not serialized)
            headerEl: null,
            laneEl: null,
            clipElements: new Map()  // clipId -> { el, ws }
        };
    }

    // ===== TRACK HEADER UI =====

    /**
     * Build the track header DOM element.
     * @param {Object} track - Track data
     * @param {Object} callbacks - Event callbacks { onMute, onSolo, onVolume, onArm, onSelect, onRename }
     * @returns {HTMLElement}
     */
    function buildTrackHeader(track, callbacks = {}) {
        const header = createDiv(null, 'daw-track-header');
        header.dataset.trackId = track.id;
        header.style.height = track.height + 'px';
        header.style.borderLeft = `3px solid ${track.color}`;

        // Top row: name + remove button
        const topRow = createDiv(null, 'daw-track-name-row');
        topRow.style.display = 'flex';
        topRow.style.alignItems = 'center';
        topRow.style.gap = '0.2rem';

        // Track name (editable)
        const nameEl = document.createElement('span');
        nameEl.className = 'daw-track-name';
        nameEl.textContent = track.name;
        nameEl.title = 'Double-click to rename';
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
            if (callbacks.onRename) callbacks.onRename(track);
        });
        nameEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); nameEl.blur(); }
            if (e.key === 'Escape') { nameEl.textContent = track.name; nameEl.blur(); }
        });
        topRow.appendChild(nameEl);

        // Remove track button (visible on hover)
        const removeBtn = document.createElement('button');
        removeBtn.className = 'daw-track-remove';
        removeBtn.innerHTML = '&#x2715;';
        removeBtn.title = 'Remove track';
        removeBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (callbacks.onRemove) callbacks.onRemove(track);
        });
        topRow.appendChild(removeBtn);

        header.appendChild(topRow);

        // Control buttons row
        const controls = createDiv(null, 'daw-track-controls');

        // Mute
        const muteBtn = document.createElement('button');
        muteBtn.className = 'daw-track-btn' + (track.muted ? ' active-mute' : '');
        muteBtn.textContent = 'M';
        muteBtn.title = 'Mute';
        muteBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            track.muted = !track.muted;
            muteBtn.classList.toggle('active-mute', track.muted);
            if (callbacks.onMute) callbacks.onMute(track);
        });
        controls.appendChild(muteBtn);

        // Solo
        const soloBtn = document.createElement('button');
        soloBtn.className = 'daw-track-btn' + (track.soloed ? ' active-solo' : '');
        soloBtn.textContent = 'S';
        soloBtn.title = 'Solo';
        soloBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            track.soloed = !track.soloed;
            soloBtn.classList.toggle('active-solo', track.soloed);
            if (callbacks.onSolo) callbacks.onSolo(track);
        });
        controls.appendChild(soloBtn);

        // Volume slider (compact inline)
        const volSlider = document.createElement('input');
        volSlider.type = 'range';
        volSlider.className = 'daw-track-volume';
        volSlider.min = '0';
        volSlider.max = '1';
        volSlider.step = '0.05';
        volSlider.value = track.volume;
        volSlider.title = `Volume: ${Math.round(track.volume * 100)}%`;
        volSlider.addEventListener('input', (e) => {
            track.volume = parseFloat(e.target.value);
            volSlider.title = `Volume: ${Math.round(track.volume * 100)}%`;
            if (callbacks.onVolume) callbacks.onVolume(track);
        });
        controls.appendChild(volSlider);

        // Arm (record)
        const armBtn = document.createElement('button');
        armBtn.className = 'daw-track-btn daw-track-arm' + (track.armed ? ' active-arm' : '');
        armBtn.textContent = 'R';
        armBtn.title = 'Arm for recording';
        armBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            track.armed = !track.armed;
            armBtn.classList.toggle('active-arm', track.armed);
            if (callbacks.onArm) callbacks.onArm(track);
        });
        controls.appendChild(armBtn);

        header.appendChild(controls);

        // Click to select track
        header.addEventListener('click', () => {
            if (callbacks.onSelect) callbacks.onSelect(track);
        });

        track.headerEl = header;
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
     * Render (or re-render) all clips in a track's lane.
     * @param {Object} track - Track data
     * @param {number} zoom - Pixels per second
     * @param {Object} callbacks - { onClipSelect, onClipMove, onClipContext }
     * @param {string|null} selectedClipId - Currently selected clip ID
     */
    function renderClips(track, zoom, callbacks = {}, selectedClipId = null) {
        if (!track.laneEl) return;

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
            const left = clip.startTime * zoom;
            const width = Math.max((clip.duration - clip.offset - clip.trimEnd) * zoom, 4);

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

            // Update position and size
            entry.el.style.left = left + 'px';
            entry.el.style.width = width + 'px';
            entry.el.style.backgroundColor = hexToRgba(clip.color || track.color, 0.3);

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

        clipEl.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            isDragging = false;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            dragStartTime = clip.startTime;
            dragTargetLane = null;
            clipEl.setPointerCapture(e.pointerId);

            const onMove = (me) => {
                const dx = me.clientX - dragStartX;
                const dy = me.clientY - dragStartY;
                if (Math.abs(dx) > 3 || Math.abs(dy) > 3) isDragging = true;
                if (!isDragging) return;

                // Horizontal: reposition in time
                const newTime = Math.max(0, dragStartTime + dx / zoom);
                clip.startTime = newTime;
                clipEl.style.left = (newTime * zoom) + 'px';

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
                clipEl.releasePointerCapture(ue.pointerId);
                clipEl.removeEventListener('pointermove', onMove);
                clipEl.removeEventListener('pointerup', onUp);
                document.querySelectorAll('.daw-track-lane').forEach(l => l.classList.remove('daw-drop-target'));

                if (isDragging) {
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
        });
    }

    /**
     * Update all WaveSurfer instances in a track for a new zoom level.
     * @param {Object} track - Track data
     * @param {number} zoom - New pixels per second
     */
    function updateZoom(track, zoom) {
        for (const clip of track.clips) {
            const entry = track.clipElements.get(clip.id);
            if (!entry) continue;
            const left = clip.startTime * zoom;
            const width = Math.max((clip.duration - clip.offset - clip.trimEnd) * zoom, 4);
            entry.el.style.left = left + 'px';
            entry.el.style.width = width + 'px';
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
            color: track.color,
            height: track.height,
            clips: track.clips.map(c => ({
                id: c.id,
                blobKey: c.blobKey,
                name: c.name,
                startTime: c.startTime,
                duration: c.duration,
                offset: c.offset,
                trimEnd: c.trimEnd,
                gain: c.gain,
                muted: c.muted,
                color: c.color
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
        createClip,
        decodeClip,
        createTrack,
        buildTrackHeader,
        buildClipLane,
        renderClips,
        updateZoom,
        destroyTrack,
        serializeTrack,
        getTrackDuration,
        hexToRgba
    };
})();
