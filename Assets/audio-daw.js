/**
 * AudioDaw — Multi-track DAW shell for AudioLab.
 * Lives in a conditionally-visible native main tab (Tabs/Text2Image/Audio Lab.html):
 * hidden by default, shown when opened or when a resumable autosave exists.
 * Switching main tabs keeps the session (and playback) alive; Close tears down
 * everything and frees all memory. Manages the transport bar, playback engine
 * (Web Audio API), track/clip arrangement, scroll sync, undo/redo, and export.
 * Reuses SwarmUI: quickAppendButton, createDiv, doNoticePopover, escapeHtml.
 * Depends on: AudioDawTimeline, AudioDawTrack, AudioLabCore, AudioLabPlayer.
 */
const AudioDaw = (() => {
    'use strict';

    const MAX_UNDO = 30;

    // ===== DAW STATE =====
    let state = null;
    let initialized = false; // DAW bound to the tab DOM + state live
    let listenersInitialized = false;

    // DOM references
    let transportEl = null;
    let rulerContainer = null;
    let trackHeadersEl = null;
    let clipLanesEl = null;
    let playheadEl = null;
    let bottomPanelEl = null;
    let timeDisplayEl = null;
    let bpmInputEl = null;

    // Runtime
    let timeline = null;
    let audioCtx = null;
    let rafId = null;
    let blobStore = new Map(); // blobKey -> { blob, decodedBuffer }
    // Live playback session graph; null when stopped. See buildTrackChain/scheduleClip.
    // { ctx, masterGain, chains: Map<trackId, {trackGain, trackPan}>,
    //   liveClips: [{clipId, trackId, iteration, source, fadeGain, clipGain}],
    //   baseCtxTime, baseTimelineTime, schedulerId, loop: {nextWrapCtxTime, nextIterScheduled, iteration}|null }
    let playback = null;
    const LOOKAHEAD = 0.2;         // seconds ahead the loop scheduler pre-schedules the next iteration
    const SCHEDULER_TICK_MS = 25;  // setInterval period (rAF would stall in background tabs)
    const START_EPSILON = 0.03;    // shared start instant so no source starts "in the past"
    // Active mic recording session; null when idle. { track, startP, recorder, placeholderEl }
    let recording = null;
    // Guards against double-triggering a Demucs pass while one is running
    let stemsSeparating = false;
    // Clip-editor waveform preview player (destroyed on each panel re-render)
    let clipEditorPlayer = null;
    let recordSettings = { deviceId: null, voiceMode: false };

    function getDefaultState() {
        return {
            tracks: [],
            masterVolume: 1.0,
            masterLimiterEnabled: true,
            beatPattern: { steps: 16, swing: 0, lanes: [] },
            bpm: 120,
            timeSignature: [4, 4],
            currentTime: 0,
            isPlaying: false,
            isRecording: false,
            loopEnabled: false,
            loopStart: 0,
            loopEnd: 0,
            rulerMode: 'time', // 'time' | 'beats'
            snapEnabled: true,
            zoom: 100,        // pixels per second
            scrollLeft: 0,
            totalDuration: 10,
            contentDuration: 0, // end of the last clip — playback/export stop here
            selectedTrackId: null,
            selectedClipId: null,
            undoStack: [],
            redoStack: []
        };
    }

    // ===== PUBLIC API =====

    // ===== TAB VISIBILITY =====
    // The tab <li> is hidden with a class (not inline style) because permissions.js
    // re-applies inline display on permission-gated elements and would undo us.

    function dawTabLink() { return document.getElementById('maintab_audiolab'); }

    function showDawTab() {
        const link = dawTabLink();
        if (!link) return;
        link.closest('.nav-item')?.classList.remove('daw-tab-hidden');
        link.click();
    }

    function hideDawTab() {
        dawTabLink()?.closest('.nav-item')?.classList.add('daw-tab-hidden');
    }

    /**
     * Size the container to the real viewport: .tab-hundred is 100% of body, so
     * pure-CSS height overflows by the top tab bar's height and runs under the
     * fixed version footer. Measure instead (the GenTabLayout approach).
     */
    function sizeDawContainer() {
        const container = document.getElementById('daw_container');
        if (!container || container.offsetParent === null) return;
        const top = container.getBoundingClientRect().top;
        // 28px clearance keeps the DAW footer above the fixed version display
        container.style.height = Math.max(400, window.innerHeight - top - 28) + 'px';
    }

    /** Bind to the static tab DOM and build the UI. Idempotent per session. */
    function ensureInitialized() {
        if (initialized) return;
        if (!document.getElementById('daw_container')) return;
        initialized = true;
        sizeDawContainer();
        resetState();
        initLayout();
        renderAllTracks();
        updateTimeDisplay();
    }

    /**
     * Open the DAW with an audio source. First open initializes a fresh session;
     * while a session is live, the audio is added as a new track at the playhead.
     * @param {string} audioSrc - URL or data: URI of audio file (optional)
     */
    async function open(audioSrc) {
        const firstOpen = !initialized;
        showDawTab(); // the tab-shown hook runs ensureInitialized()
        ensureInitialized();
        if (!audioSrc) {
            if (firstOpen) maybeOfferResume().then(maybeShowStartScreen);
            return;
        }
        const overlay = showDawLoadingOverlay('Loading audio...');
        try {
            const blob = await fetchAsBlob(audioSrc);
            if (!firstOpen) pushUndo(); // adding into a live session
            const track = addTrack();
            const clip = await addClipToTrack(track, blob, {
                name: getFilenameFromSrc(audioSrc),
                startTime: firstOpen ? 0 : snapTime(state.currentTime)
            });
            state.selectedClipId = clip.id;
            state.selectedTrackId = track.id;
            updateTotalDuration();
            renderAllTracks();
            updateTimeDisplay();
            updateBottomPanel(); // panel was built before the clip existed
            resyncPlayback();
        } catch (err) {
            console.error('[AudioDaw] Failed to load audio:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Failed to load audio: ' + err.message, 'notice-pop-red');
            }
        }
        hideDawLoadingOverlay(overlay);
        if (firstOpen) maybeOfferResume();
    }

    /**
     * Close = deliberate end of session (the pro-DAW pattern): sessions with
     * content prompt Save / Discard / Cancel; either way the crash-recovery
     * autosave is deleted, so the dormant tab only ever reappears after an
     * UNCLEAN end (crash, page nav) — never for junk sessions.
     */
    function close() {
        const hasContent = state && state.tracks.some(t => t.clips.length > 0);
        if (!hasContent) { finishClose(); return; }
        showCloseDialog();
    }

    function showCloseDialog() {
        const existing = document.querySelector('.daw-close-dialog');
        if (existing) existing.remove();
        const dlg = createDiv(null, 'daw-shortcut-help daw-close-dialog');
        const title = createDiv(null, 'daw-shortcut-help-title');
        title.textContent = 'Close Audio Lab?';
        dlg.appendChild(title);
        const msg = createDiv(null, 'daw-stems-desc');
        msg.textContent = 'Save this session as a project, or discard it. Discarded sessions are gone for good.';
        dlg.appendChild(msg);
        const row = createDiv(null, 'daw-clip-editor-actions');
        row.style.marginTop = '0.75rem';
        quickAppendButton(row, 'Save & Close', async () => {
            const name = prompt('Project name:', currentProjectName || 'My Project');
            if (!name || !name.trim()) return; // keep dialog open — treat as cancel
            dlg.remove();
            await saveProjectToServer(name.trim());
            finishClose();
        }, ' basic-button btn-primary', 'Save the project to the server, then close');
        quickAppendButton(row, 'Discard', () => {
            dlg.remove();
            finishClose();
        }, ' basic-button', 'Throw this session away and close');
        quickAppendButton(row, 'Cancel', () => dlg.remove(), ' basic-button', 'Keep working');
        dlg.appendChild(row);
        (document.getElementById('daw_container') || document.body).appendChild(dlg);
    }

    /** Full teardown: delete the crash-recovery autosave, free all memory, hide the tab. */
    function finishClose() {
        if (typeof AudioDawStore !== 'undefined') {
            AudioDawStore.deleteProject(AUTOSAVE_SLOT).catch(() => {});
        }
        destroyAll();
        initialized = false;
        hideDawTab();
        document.getElementById('text2imagetabbutton')?.click();
    }

    function initLayout() {
        transportEl = document.getElementById('daw_transport');
        rulerContainer = document.getElementById('daw_ruler');
        trackHeadersEl = document.getElementById('daw_track_headers');
        clipLanesEl = document.getElementById('daw_clip_lanes');
        playheadEl = document.getElementById('daw_playhead');
        bottomPanelEl = document.getElementById('daw_bbar_content');

        buildTransport();
        initTimeline();
        buildBottomPanel();
        updateLaneGrid();
        // Splitters + scroll sync bind document/element listeners — the tab DOM is
        // static across sessions, so bind exactly once or handlers stack up every open.
        if (!listenersInitialized) {
            listenersInitialized = true;
            setupScrollSync();
            initBottomSplitter();
            initHeaderSplitter();
        }
    }

    function initHeaderSplitter() {
        const splitter = document.getElementById('daw_header_splitter');
        const main = document.getElementById('daw_main');
        if (!splitter || !main) return;
        let dragging = false;
        const savedWidth = localStorage.getItem('daw_header_width');
        if (savedWidth) {
            main.style.setProperty('--daw-header-width', savedWidth + 'px');
        }
        splitter.addEventListener('mousedown', (e) => { dragging = true; e.preventDefault(); });
        splitter.addEventListener('touchstart', (e) => { dragging = true; e.preventDefault(); }, { passive: false });
        const onMove = (clientX) => {
            if (!dragging) return;
            const mainRect = main.getBoundingClientRect();
            const newWidth = Math.max(120, Math.min(clientX - mainRect.left, mainRect.width * 0.4));
            main.style.setProperty('--daw-header-width', newWidth + 'px');
        };
        document.addEventListener('mousemove', (e) => onMove(e.clientX));
        document.addEventListener('touchmove', (e) => onMove(e.touches[0].clientX));
        const onUp = () => {
            if (dragging) {
                dragging = false;
                const width = parseInt(getComputedStyle(main).getPropertyValue('--daw-header-width'));
                if (width) localStorage.setItem('daw_header_width', width);
            }
        };
        document.addEventListener('mouseup', onUp);
        document.addEventListener('touchend', onUp);
    }

    // ===== BOTTOM BAR SPLITTER (Swarm GenTabLayout pattern: drag to resize,
    // drag to the bottom or click the quick-button to snap shut) =====

    let bottomBarShut = localStorage.getItem('daw_bbar_shut') === 'true';

    function setBottomBarShut(shut) {
        bottomBarShut = shut;
        localStorage.setItem('daw_bbar_shut', shut ? 'true' : 'false');
        applyBottomBarLayout();
    }

    function applyBottomBarLayout() {
        const bar = document.getElementById('daw_bottom_bar');
        const btn = document.getElementById('daw_bbar_button');
        if (!bar) return;
        const px = parseInt(localStorage.getItem('daw_bbar_px')) || 380;
        const tabsH = document.getElementById('daw_bbar_tabs')?.offsetHeight || 38;
        bar.style.height = (bottomBarShut ? tabsH : Math.max(120, px)) + 'px';
        bar.classList.toggle('daw-bbar-shut', bottomBarShut);
        if (btn) btn.innerHTML = bottomBarShut ? '&#x290A;' : '&#x290B;';
    }

    function initBottomSplitter() {
        const split = document.getElementById('daw_bbar_split');
        const bar = document.getElementById('daw_bottom_bar');
        const btn = document.getElementById('daw_bbar_button');
        if (!split || !bar) return;
        let dragging = false;
        btn?.addEventListener('click', (e) => {
            e.stopPropagation();
            if (bottomBarShut) {
                // Reopen to a useful height even if it was dragged tiny before
                localStorage.setItem('daw_bbar_px', Math.max(380, parseInt(localStorage.getItem('daw_bbar_px')) || 0));
            }
            setBottomBarShut(!bottomBarShut);
        });
        split.addEventListener('mousedown', (e) => { if (e.target === btn) return; dragging = true; e.preventDefault(); });
        split.addEventListener('touchstart', (e) => { if (e.target === btn) return; dragging = true; e.preventDefault(); }, { passive: false });
        const onMove = (clientY) => {
            if (!dragging) return;
            const container = document.getElementById('daw_container');
            if (!container) return;
            const rect = container.getBoundingClientRect();
            const h = rect.bottom - clientY;
            const shut = h < 60; // dragging against the bottom snaps closed
            if (!shut) {
                localStorage.setItem('daw_bbar_px', Math.round(Math.max(120, Math.min(h, rect.height * 0.75))));
            }
            if (shut !== bottomBarShut) setBottomBarShut(shut);
            else applyBottomBarLayout();
        };
        document.addEventListener('mousemove', (e) => onMove(e.clientY));
        document.addEventListener('touchmove', (e) => { if (dragging) onMove(e.touches[0].clientY); });
        const onUp = () => { dragging = false; };
        document.addEventListener('mouseup', onUp);
        document.addEventListener('touchend', onUp);
        // Clicking any bottom tab while shut reopens the bar (core Swarm behavior)
        document.getElementById('daw_bbar_tabs')?.addEventListener('click', (e) => {
            if (bottomBarShut && e.target.closest('.nav-link')) setBottomBarShut(false);
        });
        applyBottomBarLayout();
    }

    // ===== TRANSPORT BAR =====

    // Inline SVG icons — all currentColor so they inherit theme colors (no hardcoded colors).
    const svgFill = (path) => `<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">${path}</svg>`;
    const svgLine = (path) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
    const DAW_ICONS = {
        record: svgFill('<circle cx="12" cy="12" r="7"/>'),
        play: svgFill('<path d="M7 4.8v14.4a0.6 0.6 0 0 0 0.9 0.5l11.6-7.2a0.6 0.6 0 0 0 0-1L7.9 4.3a0.6 0.6 0 0 0-0.9 0.5z"/>'),
        pause: svgFill('<rect x="6" y="5" width="4.2" height="14" rx="1"/><rect x="13.8" y="5" width="4.2" height="14" rx="1"/>'),
        stop: svgFill('<rect x="6" y="6" width="12" height="12" rx="1.5"/>'),
        toStart: svgFill('<rect x="5" y="5" width="3" height="14" rx="1"/><path d="M19.2 5.7v12.6a0.6 0.6 0 0 1-0.95 0.5L10 12.5a0.6 0.6 0 0 1 0-1l8.25-6.3a0.6 0.6 0 0 1 0.95 0.5z"/>'),
        toEnd: svgFill('<rect x="16" y="5" width="3" height="14" rx="1"/><path d="M4.8 5.7v12.6a0.6 0.6 0 0 0 0.95 0.5L14 12.5a0.6 0.6 0 0 0 0-1L5.75 5.2a0.6 0.6 0 0 0-0.95 0.5z"/>'),
        caretDown: svgFill('<path d="M7.5 10l4.5 5 4.5-5z"/>'),
        refresh: svgLine('<path d="M3 4v6h6"/><path d="M4.2 15a8 8 0 1 0 1.9-8.4L3 10"/>')
    };

    function buildTransport() {
        transportEl.innerHTML = '';
        // Segmented pill groups: related controls share a rounded container,
        // buttons are flat inside, active states fill with the emphasis color.
        const mkGroup = (cls = '') => {
            const g = createDiv(null, 'daw-tgroup' + cls);
            transportEl.appendChild(g);
            return g;
        };

        // ── Transport: record (+mic), rewind, play, stop, end, loop ──
        const transGroup = mkGroup();
        const recWrap = createDiv(null, 'daw-btn-group');
        const recBtn = document.createElement('button');
        recBtn.className = 'daw-transport-btn daw-btn-rec';
        recBtn.innerHTML = DAW_ICONS.record;
        recBtn.title = 'Record into armed track (R)';
        recBtn.addEventListener('click', () => {
            if (recording) stopRecordingFlow(); else startRecordingFlow();
        });
        recWrap.appendChild(recBtn);
        const micBtn = document.createElement('button');
        micBtn.className = 'daw-transport-btn daw-btn-mic-settings';
        micBtn.innerHTML = DAW_ICONS.caretDown;
        micBtn.title = 'Microphone settings';
        micBtn.addEventListener('click', (e) => showMicSettingsMenu(e));
        recWrap.appendChild(micBtn);
        transGroup.appendChild(recWrap);
        quickAppendButton(transGroup, DAW_ICONS.toStart, () => seekTo(0), ' daw-transport-btn', 'Rewind to start');
        const playBtn = document.createElement('button');
        playBtn.className = 'daw-transport-btn daw-btn-play';
        playBtn.innerHTML = DAW_ICONS.play;
        playBtn.title = 'Play / Pause (Space)';
        playBtn.addEventListener('click', togglePlayback);
        transGroup.appendChild(playBtn);
        quickAppendButton(transGroup, DAW_ICONS.stop, () => {
            if (recording) { stopRecordingFlow(); return; }
            stopPlayback();
            seekTo(0);
        }, ' daw-transport-btn', 'Stop');
        quickAppendButton(transGroup, DAW_ICONS.toEnd, () => seekTo(state.contentDuration), ' daw-transport-btn', 'Go to end');
        const loopBtn = document.createElement('button');
        loopBtn.className = 'daw-transport-btn daw-btn-text daw-btn-loop' + (state.loopEnabled ? ' active' : '');
        loopBtn.textContent = 'LOOP';
        loopBtn.title = 'Toggle Loop (L)';
        loopBtn.addEventListener('click', toggleLoop);
        transGroup.appendChild(loopBtn);

        // ── LCD position cluster: time + bars.beats ──
        const lcd = createDiv(null, 'daw-lcd');
        timeDisplayEl = createSpan(null, 'daw-lcd-time');
        timeDisplayEl.textContent = '0:00.0 / 0:00.0';
        const lcdBeats = createSpan(null, 'daw-lcd-beats');
        lcdBeats.textContent = '1.1.1';
        lcdBeats.title = 'Position in bars.beats.sixteenths';
        lcd.appendChild(timeDisplayEl);
        lcd.appendChild(lcdBeats);
        transportEl.appendChild(lcd);

        const spacer = createDiv(null, 'daw-transport-spacer');
        transportEl.appendChild(spacer);

        // ── Zoom ──
        const zoomGroup = mkGroup(' daw-tgroup-fields');
        const zoomLabel = createSpan(null, 'daw-transport-label');
        zoomLabel.textContent = 'ZOOM';
        const zoomSlider = document.createElement('input');
        zoomSlider.type = 'range';
        zoomSlider.className = 'daw-transport-zoom';
        zoomSlider.min = '10';
        zoomSlider.max = '500';
        zoomSlider.value = state.zoom;
        zoomSlider.title = 'Timeline zoom (pixels per second)';
        zoomSlider.addEventListener('input', (e) => {
            setZoom(parseInt(e.target.value));
        });
        zoomGroup.appendChild(zoomLabel);
        zoomGroup.appendChild(zoomSlider);

        // ── Tempo: BPM + time signature ──
        const tempoGroup = mkGroup(' daw-tgroup-fields');
        const bpmLabel = createSpan(null, 'daw-transport-label');
        bpmLabel.textContent = 'BPM';
        bpmInputEl = document.createElement('input');
        bpmInputEl.type = 'number';
        bpmInputEl.className = 'daw-transport-bpm';
        bpmInputEl.value = state.bpm;
        bpmInputEl.min = 20;
        bpmInputEl.max = 300;
        bpmInputEl.addEventListener('change', (e) => {
            state.bpm = parseInt(e.target.value) || 120;
            if (timeline) timeline.setTempo(state.bpm, state.timeSignature);
            updateLaneGrid();
            if (playback && typeof AudioDawFx !== 'undefined') {
                for (const [id, chain] of playback.chains) {
                    const tr = state.tracks.find(t => t.id === id);
                    if (tr) AudioDawFx.syncDelayTimes(chain.fxChain, tr.fx, state.bpm, playback.ctx.currentTime);
                }
            }
        });
        const sigSelect = document.createElement('select');
        sigSelect.className = 'daw-transport-timesig';
        sigSelect.title = 'Time signature';
        for (const sig of ['4/4', '3/4', '6/8', '2/4', '5/4']) {
            const opt = document.createElement('option');
            opt.value = sig;
            opt.textContent = sig;
            sigSelect.appendChild(opt);
        }
        sigSelect.value = state.timeSignature.join('/');
        sigSelect.addEventListener('change', () => {
            state.timeSignature = sigSelect.value.split('/').map(Number);
            if (timeline) timeline.setTempo(state.bpm, state.timeSignature);
            updateLaneGrid();
        });
        tempoGroup.appendChild(bpmLabel);
        tempoGroup.appendChild(bpmInputEl);
        tempoGroup.appendChild(sigSelect);

        // ── Ruler mode: TIME | BARS segmented radio ──
        const rulerGroup = mkGroup();
        const segBtns = [];
        const mkSeg = (label, mode, title) => {
            const b = document.createElement('button');
            b.className = 'daw-transport-btn daw-btn-text daw-seg' + (state.rulerMode === mode ? ' active' : '');
            b.textContent = label;
            b.title = title;
            b.addEventListener('click', () => {
                state.rulerMode = mode;
                if (timeline) timeline.setMode(mode);
                segBtns.forEach(([btn, m]) => btn.classList.toggle('active', m === mode));
                updateLaneGrid();
            });
            segBtns.push([b, mode]);
            rulerGroup.appendChild(b);
        };
        mkSeg('TIME', 'time', 'Ruler shows minutes:seconds');
        mkSeg('BARS', 'beats', 'Ruler shows bars/beats at the project tempo');

        // ── Workspace toggles: snap-to-grid, sound palette ──
        const togGroup = mkGroup();
        const snapBtn = document.createElement('button');
        snapBtn.className = 'daw-transport-btn daw-btn-text daw-btn-snap' + (state.snapEnabled ? ' active' : '');
        snapBtn.textContent = 'SNAP';
        snapBtn.title = 'Snap to grid';
        snapBtn.addEventListener('click', () => {
            state.snapEnabled = !state.snapEnabled;
            snapBtn.classList.toggle('active', state.snapEnabled);
        });
        togGroup.appendChild(snapBtn);
        const palBtn = document.createElement('button');
        palBtn.className = 'daw-transport-btn daw-btn-text daw-btn-palette';
        palBtn.textContent = 'SOUNDS';
        palBtn.title = 'Sound Palette — generate SFX/loops on demand (audition, then add)';
        palBtn.addEventListener('click', () => togglePalette(palBtn));
        togGroup.appendChild(palBtn);

        // (Master volume/meter/loudness live in the Mixer's Master strip — one
        // master control, not two.)

        // Second spacer: floats the controls cluster over the working area and
        // pins the file operations + close to the far right.
        const spacer2 = createDiv(null, 'daw-transport-spacer');
        transportEl.appendChild(spacer2);

        // ── File operations (DAW menu-bar convention) + window close ──
        const fileGroup = mkGroup();
        const mkFileBtn = (label, title, fn) => {
            const b = document.createElement('button');
            b.className = 'daw-transport-btn daw-btn-text';
            b.innerHTML = `${label} &#x25BE;`;
            b.title = title;
            b.addEventListener('click', fn);
            fileGroup.appendChild(b);
            return b;
        };
        mkFileBtn('PROJECT', 'Save, load, or start projects', (e) => showProjectMenu(e));
        mkFileBtn('IMPORT', 'Add audio — from your outputs or your computer', (e) => showImportMenu(e));
        mkFileBtn('EXPORT', 'Export the mixdown (WAV/MP3/OGG/FLAC/AAC or to Outputs)', (e) => showExportMenu(e));
        const closeBtn = document.createElement('button');
        closeBtn.className = 'daw-transport-btn daw-btn-close';
        closeBtn.innerHTML = '&#x2715;';
        closeBtn.title = 'Close Audio Lab';
        closeBtn.addEventListener('click', close);
        transportEl.appendChild(closeBtn);
    }

    // ===== FOOTER =====

    /** Show the unified import menu: server outputs or local files. */
    function showImportMenu(e) {
        dawMenu(e, [
            { label: 'From Outputs…', action: () => showOutputsPicker(e) },
            { label: 'From Computer…', action: () => importAudioToTrack() }
        ]);
    }

    // ===== TIMELINE =====

    /**
     * Quantize a timeline position to the active grid. No-op when snap is off.
     * Beats mode: nearest beat, refining to 1/2 or 1/4 beat when zoomed in.
     * Time mode: finest of 5s/1s/0.5s/0.1s that still spans >= ~12px.
     */
    function snapTime(t) {
        if (!state || !state.snapEnabled) return t;
        let grid;
        if (state.rulerMode === 'beats') {
            const secPerBeat = 60 / state.bpm;
            grid = secPerBeat;
            while ((grid / 2) * state.zoom >= 12 && grid > secPerBeat / 4 + 1e-9) grid /= 2;
        } else {
            grid = 5;
            for (const g of [5, 1, 0.5, 0.1]) {
                if (g * state.zoom >= 12) grid = g;
            }
        }
        return Math.round(t / grid) * grid;
    }

    function initTimeline() {
        rulerContainer.innerHTML = '';
        timeline = AudioDawTimeline.create(rulerContainer, {
            zoom: state.zoom,
            height: 30,
            totalDuration: state.totalDuration,
            mode: state.rulerMode,
            bpm: state.bpm,
            timeSig: state.timeSignature
        });
        timeline.setSnapper(snapTime);
        timeline.on('seek', (time) => {
            seekTo(snapTime(time));
        });
        // Live scrub: while paused just move the display; while playing, commit
        // throttled seeks so the audio follows the drag without per-frame rebuilds.
        let lastScrubSeek = 0;
        timeline.on('scrub', (time) => {
            const t = snapTime(time);
            if (state.isPlaying) {
                const now = performance.now();
                if (now - lastScrubSeek > 120) { lastScrubSeek = now; seekTo(t); }
            } else {
                state.currentTime = t;
                updatePlayheadPosition();
                updateTimeDisplay();
            }
        });
        timeline.on('loopChange', (start, end) => {
            state.loopStart = start;
            state.loopEnd = end;
            updateLoopShade();
            resyncPlayback(); // live bounds drag while playing reschedules the window
        });
    }

    // ===== BOTTOM PANEL =====
    // The tab strip + panes are static markup (Audio Lab.html); Bootstrap handles
    // switching. JS only fills the panes.

    function switchBottomTab(id) {
        document.querySelector(`#daw_bbar_tabs .nav-link[data-tab="${id}"]`)?.click();
        if (bottomBarShut) setBottomBarShut(false);
    }

    function buildBottomPanel() {
        if (!bottomPanelEl) return;
        const pane = (id) => bottomPanelEl.querySelector(`.daw-bottom-tab-content[data-tab="${id}"]`);
        // Beats + Generate are built ONCE per session (not in updateBottomPanel) so
        // typed prompts and pattern edits survive selection-driven panel refreshes
        renderBeatsPanel(pane('beats'));
        renderGeneratePanel(pane('generate'));
        updateBottomPanel();
    }

    // One undo snapshot per FX knob gesture (continuous onChange events)
    let fxUndoTimer = null;
    function fxGestureUndo() {
        if (fxUndoTimer) clearTimeout(fxUndoTimer);
        else pushUndo();
        fxUndoTimer = setTimeout(() => { fxUndoTimer = null; }, 800);
    }

    function fxPanelCallbacks() {
        return {
            masterLimiterEnabled: state.masterLimiterEnabled,
            showMenu: (e, items) => dawMenu(e, items),
            onPresetApplied: () => updateBottomPanel(), // refresh knob positions/readouts
            onSaveChain: (track, e) => {
                if (!track.fx.length) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Add some effects first', 'notice-pop-yellow');
                    return;
                }
                const name = prompt('Chain name:', 'My Chain');
                if (!name || !name.trim()) return;
                const chains = JSON.parse(localStorage.getItem('audiolab_fx_chains') || '{}');
                chains[name.trim()] = track.fx.map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } }));
                localStorage.setItem('audiolab_fx_chains', JSON.stringify(chains));
                if (typeof doNoticePopover === 'function') doNoticePopover(`Chain "${name.trim()}" saved`, 'notice-pop-green');
            },
            onLoadChain: (track, e) => {
                const chains = JSON.parse(localStorage.getItem('audiolab_fx_chains') || '{}');
                const names = Object.keys(chains);
                if (!names.length) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('No saved chains yet — build one and hit Save Chain', 'notice-pop-yellow');
                    return;
                }
                dawMenu(e, names.map(n => ({
                    label: n,
                    action: () => {
                        pushUndo();
                        track.fx = chains[n].map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } }));
                        rebuildTrackFx(track);
                        updateBottomPanel();
                    }
                })));
            },
            onParamChange: (track, fx, index) => {
                fxGestureUndo();
                const chain = playback?.chains.get(track.id);
                if (chain?.fxChain) {
                    AudioDawFx.applyEffectParams(chain.fxChain, index, fx, playback.ctx.currentTime,
                        { bpm: state.bpm, ctx: playback.ctx });
                }
            },
            onToggle: (track, fx, index) => {
                pushUndo();
                const chain = playback?.chains.get(track.id);
                if (chain?.fxChain) {
                    AudioDawFx.setEffectEnabled(chain.fxChain, index, fx, playback.ctx.currentTime);
                }
            },
            onAdd: (track, type) => {
                pushUndo();
                const fx = AudioDawFx.createEffectState(type);
                if (!fx) return;
                track.fx.push(fx);
                rebuildTrackFx(track);
                updateBottomPanel();
            },
            onRemove: (track, index) => {
                pushUndo();
                track.fx.splice(index, 1);
                rebuildTrackFx(track);
                updateBottomPanel();
            },
            onMove: (track, index, dir) => {
                const j = index + dir;
                if (j < 0 || j >= track.fx.length) return;
                pushUndo();
                [track.fx[index], track.fx[j]] = [track.fx[j], track.fx[index]];
                rebuildTrackFx(track);
                updateBottomPanel();
            },
            onMasterLimiter: (enabled) => {
                state.masterLimiterEnabled = enabled;
                // Master path topology change: cleanly restart the graph if playing
                if (state.isPlaying) {
                    const P = currentTimelineTime();
                    stopPlayback();
                    state.currentTime = P;
                    startPlayback();
                }
            }
        };
    }

    function updateBottomPanel() {
        if (!bottomPanelEl) return;

        // Auto-select when unambiguous: a lone track, and a lone clip project-wide.
        // Saves a pointless click before Clip Editor / Generate voice-ref do anything.
        if (state.tracks.length && !state.tracks.some(t => t.id === state.selectedTrackId)) {
            state.selectedTrackId = state.tracks[0].id;
            updateTrackSelection();
        }
        if (!findClipById(state.selectedClipId)) {
            const allClips = state.tracks.flatMap(t => t.clips);
            if (allClips.length === 1) {
                state.selectedClipId = allClips[0].id;
            }
        }

        // Update Clip Editor tab
        const clipEditorContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="clip-editor"]');
        if (clipEditorContent) {
            clipEditorContent.innerHTML = '';
            const selectedClip = findClipById(state.selectedClipId);
            if (selectedClip) {
                const { clip, track } = selectedClip;
                const info = createDiv(null, 'daw-clip-editor daw-panel-split');
                // Wide panels: clip card (waveform + actions) left, mix card right
                const editLeft = createDiv(null, 'daw-panel-col');
                const editRight = createDiv(null, 'daw-panel-col');
                info.appendChild(editLeft);
                info.appendChild(editRight);

                // Clip inspector card: name, metadata, waveform preview
                const clipCard = createDiv(null, 'daw-fx-card daw-clip-card');
                const nameEl = createDiv(null, 'daw-clip-card-name');
                nameEl.textContent = clip.name;
                nameEl.title = clip.name;
                clipCard.appendChild(nameEl);
                const metaEl = createDiv(null, 'daw-clip-card-meta');
                metaEl.textContent = `${track.name} · ${formatTimePrecise(clip.duration)}s · starts at ${formatTimePrecise(clip.startTime)}s`;
                clipCard.appendChild(metaEl);
                const waveHolder = createDiv(null, 'daw-clip-card-wave');
                clipCard.appendChild(waveHolder);
                if (clipEditorPlayer) {
                    try { clipEditorPlayer.destroy(); } catch (_) {}
                    clipEditorPlayer = null;
                }
                if (clip.blob && typeof AudioLabPlayer !== 'undefined') {
                    clipEditorPlayer = AudioLabPlayer.createMini(waveHolder, { height: 48, showDownload: false });
                    clipEditorPlayer.loadBlob(clip.blob);
                }
                editLeft.appendChild(clipCard);

                // Re-lookup clip at click time to avoid stale closures
                const getClip = () => findClipById(state.selectedClipId);
                const actions = createDiv(null, 'daw-clip-editor-actions');
                const splitBtn = document.createElement('button');
                splitBtn.className = 'basic-button btn-sm';
                splitBtn.textContent = 'Split at Playhead';
                splitBtn.title = 'Split clip at current playhead position';
                splitBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doSplitClip(sel.clip, sel.track);
                });
                actions.appendChild(splitBtn);
                const dupBtn = document.createElement('button');
                dupBtn.className = 'basic-button btn-sm';
                dupBtn.textContent = 'Duplicate';
                dupBtn.title = 'Duplicate this clip';
                dupBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doDuplicateClip(sel.clip, sel.track);
                });
                actions.appendChild(dupBtn);
                const delBtn = document.createElement('button');
                delBtn.className = 'basic-button btn-sm';
                delBtn.textContent = 'Delete';
                delBtn.title = 'Delete this clip';
                delBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doDeleteClip(sel.clip, sel.track);
                });
                actions.appendChild(delBtn);
                const muteBtn = document.createElement('button');
                muteBtn.className = 'basic-button btn-sm';
                muteBtn.textContent = clip.muted ? 'Unmute' : 'Mute';
                muteBtn.title = 'Toggle clip mute';
                muteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) {
                        sel.clip.muted = !sel.clip.muted;
                        applyClipGain(sel.clip);
                        renderAllTracks();
                        updateBottomPanel();
                    }
                });
                actions.appendChild(muteBtn);
                clipCard.appendChild(actions);

                // Gain + fades row
                const mixRow = createDiv(null, 'daw-clip-editor-info daw-clip-editor-mix');
                const gainLabel = createSpan(null, 'daw-clip-mix-label');
                gainLabel.textContent = 'Gain';
                const gainSlider = document.createElement('input');
                gainSlider.type = 'range';
                gainSlider.className = 'daw-clip-gain-slider';
                gainSlider.min = '-24';
                gainSlider.max = '12';
                gainSlider.step = '0.5';
                const clipDb = clip.gain > 0 ? 20 * Math.log10(clip.gain) : -24;
                gainSlider.value = Math.max(-24, Math.min(12, clipDb));
                const gainVal = createSpan(null, 'daw-clip-mix-val');
                gainVal.textContent = clipDb.toFixed(1) + ' dB';
                let gainGesture = false;
                gainSlider.addEventListener('input', () => {
                    const sel = getClip();
                    if (!sel) return;
                    if (!gainGesture) { gainGesture = true; pushUndo(); } // snapshot pre-gesture value once
                    const db = parseFloat(gainSlider.value);
                    sel.clip.gain = Math.pow(10, db / 20);
                    gainVal.textContent = db.toFixed(1) + ' dB';
                    applyClipGain(sel.clip);
                });
                gainSlider.addEventListener('change', () => { gainGesture = false; });
                gainSlider.addEventListener('dblclick', () => {
                    const sel = getClip();
                    if (!sel) return;
                    pushUndo();
                    sel.clip.gain = 1;
                    gainSlider.value = 0;
                    gainVal.textContent = '0.0 dB';
                    applyClipGain(sel.clip);
                });
                mixRow.appendChild(gainLabel);
                mixRow.appendChild(gainSlider);
                mixRow.appendChild(gainVal);

                const makeFadeInput = (label, prop) => {
                    const lbl = createSpan(null, 'daw-clip-mix-label');
                    lbl.textContent = label;
                    const input = document.createElement('input');
                    input.type = 'number';
                    input.className = 'daw-clip-fade-input';
                    input.min = '0';
                    input.step = '0.1';
                    input.value = clip[prop] || 0;
                    input.title = `${label} length in seconds`;
                    input.addEventListener('change', () => {
                        const sel = getClip();
                        if (!sel) return;
                        pushUndo();
                        sel.clip[prop] = Math.max(0, parseFloat(input.value) || 0);
                        resyncPlayback(); // fades are baked into scheduled ramps
                    });
                    fadeRow.appendChild(lbl);
                    fadeRow.appendChild(input);
                    const unit = createSpan(null, 'daw-clip-mix-label');
                    unit.textContent = 's';
                    fadeRow.appendChild(unit);
                };
                const fadeRow = createDiv(null, 'daw-clip-editor-info daw-clip-editor-mix');
                makeFadeInput('Fade In', 'fadeIn');
                makeFadeInput('Fade Out', 'fadeOut');
                const mixCard = createDiv(null, 'daw-fx-card daw-clip-mix-card');
                const mixTitle = createDiv(null, 'daw-fx-card-title');
                mixTitle.textContent = 'Clip Mix';
                mixCard.appendChild(mixTitle);
                const mixNote = createDiv(null, 'daw-clip-card-meta');
                mixNote.textContent = 'Level and fades for this clip only — track volume lives in the Mixer.';
                mixCard.appendChild(mixNote);
                mixCard.appendChild(mixRow);
                mixCard.appendChild(fadeRow);
                editRight.appendChild(mixCard);

                clipEditorContent.appendChild(info);
            } else {
                if (clipEditorPlayer) {
                    try { clipEditorPlayer.destroy(); } catch (_) {}
                    clipEditorPlayer = null;
                }
                clipEditorContent.innerHTML = '<div style="color:var(--text-soft);font-size:0.85rem;padding:0.5rem;">Select a clip to edit</div>';
            }
        }

        // Update Mixer tab
        const mixerContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="mixer"]');
        if (mixerContent) {
            if (typeof AudioDawMixer !== 'undefined') {
                AudioDawMixer.render(mixerContent, state, (prop, val) => {
                    if (prop === 'masterVolume') { state.masterVolume = val; updatePlaybackGains(); }
                    else if (prop === 'pan') { updatePlaybackGains(); }
                    else if (prop === 'mute' || prop === 'solo' || prop === 'volume') {
                        updatePlaybackGains();
                        AudioDawTrack.syncHeaderControls(val); // val is the track here
                    }
                });
            } else {
                mixerContent.innerHTML = '';
                renderInlineMixer(mixerContent);
            }
        }

        // Update Stems tab
        const stemsContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="stems"]');
        if (stemsContent) {
            stemsContent.innerHTML = '';
            renderStemsPanel(stemsContent);
        }

        // Update FX tab
        const fxContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="fx"]');
        if (fxContent && typeof AudioDawFx !== 'undefined') {
            AudioDawFx.renderFxPanel(fxContent, getSelectedTrack(), fxPanelCallbacks());
        }

    }

    /** Simple inline mixer fallback when AudioDawMixer module isn't loaded. */
    function renderInlineMixer(container) {
        container.innerHTML = '';
        const mixer = createDiv(null, 'daw-mixer');

        for (const track of state.tracks) {
            const row = createDiv(null, 'daw-mixer-row');
            const colorBar = createDiv(null, 'daw-mixer-color');
            colorBar.style.background = track.color;
            row.appendChild(colorBar);
            const label = createDiv(null, 'daw-mixer-label');
            label.textContent = track.name;
            label.title = track.name;
            row.appendChild(label);
            // M/S buttons
            const btns = createDiv(null, 'daw-mixer-btns');
            const muteBtn = document.createElement('button');
            muteBtn.className = 'daw-mixer-btn' + (track.muted ? ' active-mute' : '');
            muteBtn.textContent = 'M';
            muteBtn.addEventListener('click', () => {
                track.muted = !track.muted;
                muteBtn.classList.toggle('active-mute', track.muted);
                updatePlaybackGains();
                renderAllTracks();
            });
            btns.appendChild(muteBtn);
            const soloBtn = document.createElement('button');
            soloBtn.className = 'daw-mixer-btn' + (track.soloed ? ' active-solo' : '');
            soloBtn.textContent = 'S';
            soloBtn.addEventListener('click', () => {
                track.soloed = !track.soloed;
                soloBtn.classList.toggle('active-solo', track.soloed);
                updatePlaybackGains();
                renderAllTracks();
            });
            btns.appendChild(soloBtn);
            row.appendChild(btns);
            // Volume
            const volGroup = createDiv(null, 'daw-mixer-vol-group');
            const volLbl = createSpan(null, 'daw-mixer-vol-label');
            volLbl.textContent = 'Vol';
            const fader = document.createElement('input');
            fader.type = 'range';
            fader.className = 'daw-mixer-fader';
            fader.min = '0'; fader.max = '1'; fader.step = '0.01';
            fader.value = track.volume;
            const dbLabel = createSpan(null, 'daw-mixer-db');
            dbLabel.textContent = volumeToDb(track.volume);
            fader.addEventListener('input', (e) => {
                track.volume = parseFloat(e.target.value);
                dbLabel.textContent = volumeToDb(track.volume);
                updatePlaybackGains();
            });
            volGroup.appendChild(volLbl);
            volGroup.appendChild(fader);
            volGroup.appendChild(dbLabel);
            row.appendChild(volGroup);
            mixer.appendChild(row);
        }

        // Master row
        const sep = createDiv(null, 'daw-mixer-separator');
        mixer.appendChild(sep);
        const master = createDiv(null, 'daw-mixer-row master');
        const masterColor = createDiv(null, 'daw-mixer-color');
        masterColor.style.background = 'var(--emphasis)';
        master.appendChild(masterColor);
        const masterLabel = createDiv(null, 'daw-mixer-label');
        masterLabel.textContent = 'Master';
        masterLabel.style.fontWeight = '600';
        master.appendChild(masterLabel);
        const masterBtns = createDiv(null, 'daw-mixer-btns');
        master.appendChild(masterBtns);
        const masterVolGroup = createDiv(null, 'daw-mixer-vol-group');
        const masterVolLbl = createSpan(null, 'daw-mixer-vol-label');
        masterVolLbl.textContent = 'Vol';
        const masterFader = document.createElement('input');
        masterFader.type = 'range';
        masterFader.className = 'daw-mixer-fader';
        masterFader.min = '0'; masterFader.max = '1'; masterFader.step = '0.01';
        masterFader.value = state.masterVolume;
        const masterDb = createSpan(null, 'daw-mixer-db');
        masterDb.textContent = volumeToDb(state.masterVolume);
        masterFader.addEventListener('input', (e) => {
            state.masterVolume = parseFloat(e.target.value);
            masterDb.textContent = volumeToDb(state.masterVolume);
            updatePlaybackGains();
        });
        masterVolGroup.appendChild(masterVolLbl);
        masterVolGroup.appendChild(masterFader);
        masterVolGroup.appendChild(masterDb);
        master.appendChild(masterVolGroup);
        mixer.appendChild(master);
        container.appendChild(mixer);
    }

    function volumeToDb(vol) {
        if (vol <= 0) return '-inf';
        const db = 20 * Math.log10(vol);
        return db.toFixed(1) + ' dB';
    }

    /** Render the Stems (Demucs) tab content. */
    let stemsPreset = 'split'; // selected output-preset card

    function renderStemsPanel(container) {
        container.innerHTML = '';
        const panel = createDiv(null, 'daw-stems-panel');
        container.appendChild(panel);

        // Output preset cards — the tab's primary choice, front and center
        const PRESET_CARDS = {
            split: ['Full Split', 'Every stem becomes its own track'],
            karaoke: ['Karaoke', 'Vocals + combined instrumental'],
            acapella: ['Acapella', 'Vocals only'],
            instrumental: ['Instrumental', 'Everything except vocals'],
            custom: ['Custom', 'Pick exactly which stems to keep']
        };
        const browser = createDiv(null, 'daw-fx-browser daw-stems-browser');
        for (const preset of STEM_PRESETS) {
            const [name, cdesc] = PRESET_CARDS[preset.id];
            const pick = document.createElement('button');
            pick.className = 'daw-fx-pick daw-inst-pick' + (preset.id === stemsPreset ? ' selected' : '');
            pick.innerHTML = `<span class="daw-fx-pick-name">${name}</span>`
                + `<span class="daw-fx-pick-desc">${cdesc}</span>`;
            pick.addEventListener('click', () => {
                stemsPreset = preset.id;
                renderStemsPanel(container);
            });
            browser.appendChild(pick);
        }
        panel.appendChild(browser);

        // Wide panels: about + model left, stem picks + source + action right
        const section = createDiv(null, 'daw-panel-split');
        panel.appendChild(section);
        const left = createDiv(null, 'daw-panel-col');
        const right = createDiv(null, 'daw-panel-col');
        section.appendChild(left);
        section.appendChild(right);

        const header = createDiv(null, 'daw-stems-header');
        header.innerHTML = '<strong>Stem Separation (Demucs)</strong>';
        left.appendChild(header);
        const desc = createDiv(null, 'daw-stems-desc');
        desc.textContent = 'AI source separation splits a mixed clip into its component parts — each chosen stem becomes a new track in the DAW.';
        left.appendChild(desc);

        // Model picker
        const modelRow = createDiv(null, 'daw-stems-model-row');
        const modelLabel = document.createElement('label');
        modelLabel.className = 'daw-stems-ctl-label';
        modelLabel.textContent = 'Model:';
        const modelSelect = document.createElement('select');
        modelSelect.className = 'daw-stems-select';
        for (const [id, def] of Object.entries(STEM_MODELS)) {
            const opt = document.createElement('option');
            opt.value = id;
            opt.textContent = def.label;
            modelSelect.appendChild(opt);
        }
        modelRow.appendChild(modelLabel);
        modelRow.appendChild(modelSelect);
        left.appendChild(modelRow);

        // Per-stem chips (editable only in Custom mode; read-only preview otherwise)
        const stemsRow = createDiv(null, 'daw-stems-checks');
        right.appendChild(stemsRow);

        const getStems = () => STEM_MODELS[modelSelect.value].stems;
        let customSel = new Set(getStems());

        function rebuildStemChecks() {
            stemsRow.innerHTML = '';
            const stems = getStems();
            const custom = stemsPreset === 'custom';
            const sel = new Set(custom ? [...customSel].filter(s => stems.includes(s)) : presetInvolved(stemsPreset, stems));

            const hint = createDiv(null, 'daw-stems-checks-hint');
            hint.textContent = custom ? 'Choose which stems become tracks:' : 'Included stems:';
            stemsRow.appendChild(hint);

            const grid = createDiv(null, 'daw-stems-check-grid');
            for (const s of stems) {
                const lbl = document.createElement('label');
                lbl.className = 'daw-stems-check' + (custom ? '' : ' is-locked');
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.checked = sel.has(s);
                cb.disabled = !custom;
                cb.addEventListener('change', () => {
                    if (cb.checked) customSel.add(s); else customSel.delete(s);
                });
                const dot = document.createElement('span');
                dot.className = 'daw-stems-dot';
                dot.style.background = STEM_COLORS[s] || 'var(--text-soft)';
                lbl.appendChild(cb);
                lbl.appendChild(dot);
                lbl.appendChild(document.createTextNode(' ' + capStem(s)));
                grid.appendChild(lbl);
            }
            stemsRow.appendChild(grid);
        }

        modelSelect.addEventListener('change', () => { customSel = new Set(getStems()); rebuildStemChecks(); });
        rebuildStemChecks();

        // Turns the current control state into the output plan the separator executes.
        function buildPlan() {
            const stems = getStems();
            if (stemsPreset === 'custom') {
                return stems.filter(s => customSel.has(s)).map(s => ({ name: s, parts: [s] }));
            }
            return STEM_PRESETS.find(p => p.id === stemsPreset).plan(stems);
        }

        // Source clip picker + separate button
        const actionRow = createDiv(null, 'daw-stems-action-row');
        let sepBtn = null;
        const allClips = [];
        for (const t of state.tracks) {
            for (const c of t.clips) allClips.push({ clip: c, track: t });
        }
        if (!allClips.length) {
            actionRow.innerHTML = '<span class="daw-stems-clipinfo">Add or import a clip first — stem separation splits one clip into new tracks</span>';
        } else {
            const srcLabel = document.createElement('label');
            srcLabel.className = 'daw-stems-ctl-label';
            srcLabel.textContent = 'Source:';
            const srcSelect = document.createElement('select');
            srcSelect.className = 'daw-stems-select';
            for (const { clip, track } of allClips) {
                const opt = document.createElement('option');
                opt.value = clip.id;
                const dur = formatTimePrecise(clip.duration - clip.offset - clip.trimEnd);
                opt.textContent = `${track.name} — ${clip.name} (${dur}s)`;
                srcSelect.appendChild(opt);
            }
            if (state.selectedClipId && allClips.some(x => x.clip.id === state.selectedClipId)) {
                srcSelect.value = state.selectedClipId;
            }
            actionRow.appendChild(srcLabel);
            actionRow.appendChild(srcSelect);

            sepBtn = document.createElement('button');
            sepBtn.className = 'basic-button btn-sm daw-stems-go';
            sepBtn.textContent = 'Separate Stems';
            sepBtn.addEventListener('click', async () => {
                const sel = allClips.find(x => x.clip.id === srcSelect.value);
                if (!sel) return;
                const outputs = buildPlan();
                if (!outputs.length) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Select at least one stem', 'notice-pop-yellow');
                    return;
                }
                // Demucs missing? Offer to install it right here, then continue the separation.
                if (!await checkDemucsInstalled()) {
                    if (!confirm('Stem separation requires the Demucs engine (a one-time ~2 GB download).\n\nInstall it now? Separation will start automatically when it finishes.')) {
                        return;
                    }
                    sepBtn.disabled = true;
                    const ok = await installDemucs((msg) => {
                        sepBtn.textContent = 'Installing Demucs… ' + msg.slice(0, 30);
                    });
                    sepBtn.disabled = false;
                    sepBtn.textContent = 'Separate Stems';
                    if (!ok) return;
                }
                doSeparateStems(sel.clip, sel.track, { modelName: modelSelect.value, outputs });
            });
        }
        right.appendChild(actionRow);
        if (sepBtn) right.appendChild(sepBtn);
    }

    // Stem sets each Demucs model produces (names match what the backend returns).
    const STEM_MODELS = {
        htdemucs: { label: 'HTDemucs — 4 stems', stems: ['vocals', 'drums', 'bass', 'other'] },
        htdemucs_ft: { label: 'HTDemucs Fine-tuned — 4 stems (best quality)', stems: ['vocals', 'drums', 'bass', 'other'] },
        htdemucs_6s: { label: 'HTDemucs 6-stem — adds guitar + piano', stems: ['vocals', 'drums', 'bass', 'guitar', 'piano', 'other'] }
    };

    // Track colors per stem name (+ the synthesized "instrumental" combine track).
    const STEM_COLORS = {
        vocals: '#cc5de8', drums: '#ff922b', bass: '#22b8cf',
        other: '#82c91e', guitar: '#ffd43b', piano: '#4a9eff', instrumental: '#20c997'
    };

    // Output presets. `plan(stems)` → [{name, parts}]; a `parts` list longer than 1 is summed into one track.
    const STEM_PRESETS = [
        { id: 'split', label: 'Full split — every stem as its own track', plan: (s) => s.map(x => ({ name: x, parts: [x] })) },
        { id: 'karaoke', label: 'Karaoke — vocals + combined instrumental', plan: (s) => [{ name: 'vocals', parts: ['vocals'] }, { name: 'instrumental', parts: s.filter(x => x !== 'vocals') }] },
        { id: 'acapella', label: 'Acapella — vocals only', plan: (s) => [{ name: 'vocals', parts: ['vocals'] }] },
        { id: 'instrumental', label: 'Instrumental — everything except vocals', plan: (s) => [{ name: 'instrumental', parts: s.filter(x => x !== 'vocals') }] },
        { id: 'custom', label: 'Custom — pick stems below', plan: null }
    ];

    function capStem(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }

    /** Stems a non-custom preset includes, for the (read-only) checkbox display. */
    function presetInvolved(presetId, stems) {
        switch (presetId) {
            case 'acapella': return ['vocals'];
            case 'instrumental': return stems.filter(s => s !== 'vocals');
            case 'split':
            case 'karaoke': return stems.slice();
            default: return stems.slice();
        }
    }


    // ===== GENERATE TAB =====

    let generateEngines = null; // engine list cache for the Generate tab

    /**
     * Generate through SwarmUI's core pipeline (GenerateText2ImageWS) — the same
     * endpoint the main Generate tab uses. The output is saved to the user's
     * outputs folder + history like any generation, and progress streams back.
     * @param {Object} opts - { model: Swarm model name, prompt, params: extra T2I params, onProgress(frac) }
     * @returns {Promise<{blob: Blob, src: string}>} the produced audio + its View URL
     */
    function dawSwarmGenerate({ model, prompt, params = {}, onProgress = null }) {
        return new Promise((resolve, reject) => {
            const genData = { images: 1, model, prompt, seed: -1, ...params };
            let settled = false;
            const fail = (msg) => { if (!settled) { settled = true; reject(new Error(msg)); } };
            makeWSRequestT2I('GenerateText2ImageWS', genData, (data) => {
                if (data.gen_progress && onProgress) {
                    const p = data.gen_progress.overall_percent || data.gen_progress.current_percent;
                    if (p) onProgress(Math.max(0, Math.min(1, p)));
                }
                if (data.error) { fail(data.error); return; }
                if (data.image) {
                    const src = typeof data.image === 'string' ? data.image : data.image.image;
                    fetch(src)
                        .then(r => { if (!r.ok) throw new Error(`Output fetch failed (${r.status})`); return r.blob(); })
                        .then(blob => { if (!settled) { settled = true; resolve({ blob, src }); } })
                        .catch(err => fail(err.message));
                }
            }, (err) => fail(err || 'Generation failed'));
        });
    }

    /** Fetch + cache the engine list (shared by the Generate tab, palette, and beat pads). */
    async function ensureEnginesList() {
        if (!generateEngines) {
            const result = await AudioLabAPI.callAPI('AudioLabListEngines');
            if (!result.success) throw new Error(result.error || 'Failed to list engines');
            generateEngines = result.engines || [];
        }
        return generateEngines;
    }

    /** First installed model's Swarm-registry name for an engine id (cached engine list). */
    function dawSwarmModelFor(engineId) {
        const eng = (generateEngines || []).find(x => x.id === engineId);
        const m = (eng?.models || []).find(x => x.installed !== false);
        return m?.swarm_model || null;
    }

    /** Bottom-panel tab: generate TTS/music/SFX with an installed engine straight into a track. */
    // Generate tab: category cards (TTS / Music / SFX / STT) — each shows only
    // its engines and the params that type actually needs.
    const GEN_CATEGORIES = [
        { id: 'tts', name: 'Text to Speech', desc: 'Speak any text — optionally clone a voice' },
        { id: 'music', name: 'Music', desc: 'Songs and loops from a style prompt + lyrics' },
        { id: 'sfx', name: 'Sound FX', desc: 'One-shots and foley from a description' },
        { id: 'stt', name: 'Speech to Text', desc: 'Transcribe the selected clip' }
    ];
    let generateCategory = 'tts';

    function enginesForCategory(cat) {
        const list = (generateEngines || []).filter(e => e.installed);
        if (cat === 'tts') return list.filter(e => e.category === 'TTS');
        if (cat === 'stt') return list.filter(e => e.category === 'STT');
        const gen = list.filter(e => e.category === 'AudioGeneration');
        return cat === 'sfx' ? gen.filter(e => /sfx/i.test(e.id)) : gen.filter(e => !/sfx/i.test(e.id));
    }

    async function renderGeneratePanel(container) {
        container.innerHTML = '';
        const panel = createDiv(null, 'daw-generate-panel');
        container.appendChild(panel);
        const status = createDiv(null, 'daw-stems-desc');
        status.textContent = 'Loading engines...';
        panel.appendChild(status);
        try {
            await ensureEnginesList();
        } catch (err) {
            status.textContent = 'Failed to load engines: ' + err.message;
            return;
        }
        status.remove();

        // Category cards; fall back to the first category that has an engine
        if (!enginesForCategory(generateCategory).length) {
            const firstReady = GEN_CATEGORIES.find(c => enginesForCategory(c.id).length);
            if (firstReady) generateCategory = firstReady.id;
        }
        const browser = createDiv(null, 'daw-fx-browser daw-gen-browser');
        for (const cat of GEN_CATEGORIES) {
            const count = enginesForCategory(cat.id).length;
            const pick = document.createElement('button');
            pick.className = 'daw-fx-pick daw-inst-pick'
                + (cat.id === generateCategory ? ' selected' : '')
                + (count ? '' : ' not-ready');
            pick.innerHTML = `<span class="daw-fx-pick-name">${cat.name}</span>`
                + `<span class="daw-fx-pick-desc">${count ? cat.desc : 'No engine installed'}</span>`;
            pick.addEventListener('click', () => {
                if (!count) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(`No installed ${cat.name} engine — add one under Server -> Backends`, 'notice-pop-yellow');
                    return;
                }
                generateCategory = cat.id;
                renderGeneratePanel(container);
            });
            browser.appendChild(pick);
        }
        panel.appendChild(browser);

        const engines = enginesForCategory(generateCategory);
        if (!engines.length) {
            const none = createDiv(null, 'daw-stems-desc');
            none.textContent = 'No audio engines installed — add one from the Audio Backend card under Server -> Backends.';
            panel.appendChild(none);
            return;
        }

        const section = createDiv(null, 'daw-panel-split');
        panel.appendChild(section);
        const mainCol = createDiv(null, 'daw-panel-col');
        const sideCol = createDiv(null, 'daw-panel-col');
        section.appendChild(mainCol);
        section.appendChild(sideCol);

        // Engine + model pickers (only this category's engines)
        const engineRow = createDiv(null, 'daw-stems-model-row');
        const engineLabel = document.createElement('label');
        engineLabel.className = 'daw-stems-ctl-label';
        engineLabel.textContent = 'Engine:';
        const engineSelect = document.createElement('select');
        engineSelect.className = 'daw-stems-select';
        for (const eng of engines) {
            const opt = document.createElement('option');
            opt.value = eng.id;
            opt.textContent = eng.name;
            engineSelect.appendChild(opt);
        }
        engineRow.appendChild(engineLabel);
        engineRow.appendChild(engineSelect);
        mainCol.appendChild(engineRow);

        const currentEngine = () => engines.find(x => x.id === engineSelect.value) || engines[0];

        const modelRow = createDiv(null, 'daw-stems-model-row');
        const modelLabel = document.createElement('label');
        modelLabel.className = 'daw-stems-ctl-label';
        modelLabel.textContent = 'Model:';
        const modelSelect = document.createElement('select');
        modelSelect.className = 'daw-stems-select';
        modelRow.appendChild(modelLabel);
        modelRow.appendChild(modelSelect);
        mainCol.appendChild(modelRow);

        function rebuildModels() {
            modelSelect.innerHTML = '';
            const eng = currentEngine();
            const models = (eng?.models || []).filter(m => m.installed !== false);
            for (const m of models) {
                const opt = document.createElement('option');
                opt.value = m.id;
                opt.textContent = m.name;
                modelSelect.appendChild(opt);
            }
            modelRow.style.display = models.length > 1 ? '' : 'none';
        }

        // ==== Speech to Text: transcription workflow (produces text, not audio) ====
        if (generateCategory === 'stt') {
            const resultArea = document.createElement('textarea');
            resultArea.className = 'daw-generate-text';
            resultArea.rows = 4;
            resultArea.placeholder = 'Transcription appears here…';
            mainCol.appendChild(resultArea);

            const actionRow = createDiv(null, 'daw-stems-action-row');
            const hint = createSpan(null, 'daw-stems-clipinfo');
            hint.textContent = 'Transcribes the selected clip with the chosen engine.';
            actionRow.appendChild(hint);
            sideCol.appendChild(actionRow);
            const goBtn = document.createElement('button');
            goBtn.className = 'basic-button btn-sm daw-stems-go';
            goBtn.textContent = 'Transcribe Selected Clip';
            goBtn.addEventListener('click', async () => {
                const sel = findClipById(state.selectedClipId);
                if (!sel) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Select a clip to transcribe', 'notice-pop-yellow');
                    return;
                }
                goBtn.disabled = true;
                const busy = createBusyIndicator('Transcribing…', 'generate');
                sideCol.appendChild(busy);
                try {
                    const b64 = await AudioLabCore.readAsBase64(sel.clip.blob);
                    const result = await AudioLabAPI.processSTT(b64, { providerId: currentEngine().id });
                    if (!result.success || !result.transcription) throw new Error(result.error || 'No transcription returned');
                    resultArea.value = result.transcription.trim();
                } catch (err) {
                    console.error('[AudioDaw] Transcription failed:', err);
                    if (typeof doNoticePopover === 'function') doNoticePopover('Transcription failed: ' + err.message, 'notice-pop-red');
                } finally {
                    busy.done();
                    goBtn.disabled = false;
                }
            });
            sideCol.appendChild(goBtn);
            const copyRow = createDiv(null, 'daw-stems-model-row');
            const copyBtn = document.createElement('button');
            copyBtn.className = 'basic-button btn-sm';
            copyBtn.textContent = 'Copy Text';
            copyBtn.addEventListener('click', () => {
                if (!resultArea.value) return;
                navigator.clipboard?.writeText(resultArea.value);
                if (typeof doNoticePopover === 'function') doNoticePopover('Copied to clipboard', 'notice-pop-green');
            });
            copyRow.appendChild(copyBtn);
            sideCol.appendChild(copyRow);
            engineSelect.addEventListener('change', rebuildModels);
            rebuildModels();
            return;
        }

        // ==== TTS / Music / SFX generation form ====
        const isTts = generateCategory === 'tts';
        const isMusicCat = generateCategory === 'music';

        const promptArea = document.createElement('textarea');
        promptArea.className = 'daw-generate-text';
        promptArea.rows = 2;
        promptArea.placeholder = isTts ? 'Text to speak...'
            : isMusicCat ? 'Describe the music (style, mood, instruments)...'
            : 'Describe the sound: "punchy kick drum", "rain on a tin roof"...';
        mainCol.appendChild(promptArea);

        let lyricsArea = null;
        if (isMusicCat) {
            lyricsArea = document.createElement('textarea');
            lyricsArea.className = 'daw-generate-text';
            lyricsArea.rows = 3;
            lyricsArea.placeholder = 'Lyrics (optional — leave empty for instrumental)';
            mainCol.appendChild(lyricsArea);
        }

        // Options: duration + seed (music/SFX), tempo hints (music)
        let durationInput = null, seedInput = null, tempoCheck = null;
        if (!isTts) {
            const optsRow = createDiv(null, 'daw-stems-model-row');
            const durLabel = document.createElement('label');
            durLabel.className = 'daw-stems-ctl-label';
            durLabel.textContent = 'Duration (s):';
            durationInput = document.createElement('input');
            durationInput.type = 'number';
            durationInput.className = 'daw-clip-fade-input';
            durationInput.min = '1';
            durationInput.value = isMusicCat ? '20' : '3';
            const seedLabel = document.createElement('label');
            seedLabel.className = 'daw-stems-ctl-label';
            seedLabel.textContent = 'Seed:';
            seedInput = document.createElement('input');
            seedInput.type = 'number';
            seedInput.className = 'daw-clip-fade-input';
            seedInput.value = '-1';
            seedInput.title = '-1 = random';
            optsRow.appendChild(durLabel);
            optsRow.appendChild(durationInput);
            optsRow.appendChild(seedLabel);
            optsRow.appendChild(seedInput);
            sideCol.appendChild(optsRow);

            if (isMusicCat) {
                const tempoRow = createDiv(null, 'daw-stems-model-row');
                tempoCheck = document.createElement('input');
                tempoCheck.type = 'checkbox';
                tempoCheck.id = 'daw_gen_tempo_hints';
                tempoCheck.checked = true;
                const tempoLbl = document.createElement('label');
                tempoLbl.htmlFor = 'daw_gen_tempo_hints';
                tempoLbl.className = 'daw-stems-ctl-label';
                tempoLbl.textContent = `Match project tempo (${state.bpm} BPM, ${state.timeSignature.join('/')})`;
                tempoRow.appendChild(tempoCheck);
                tempoRow.appendChild(tempoLbl);
                sideCol.appendChild(tempoRow);
            }
        }

        // Voice reference (TTS only, capability-gated per engine)
        let refCheck = null, refTextInput = null, refRow = null, refNote = null;
        if (isTts) {
            refRow = createDiv(null, 'daw-stems-model-row');
            refCheck = document.createElement('input');
            refCheck.type = 'checkbox';
            refCheck.id = 'daw_gen_voice_ref';
            const refLabel = document.createElement('label');
            refLabel.className = 'daw-stems-ctl-label';
            refLabel.htmlFor = 'daw_gen_voice_ref';
            refLabel.textContent = 'Use selected clip as voice reference';
            refTextInput = document.createElement('input');
            refTextInput.type = 'text';
            refTextInput.className = 'daw-generate-reftext';
            refTextInput.placeholder = 'Reference transcript (optional)';
            // Auto-fill the transcript by running the selected clip through STT
            const sttBtn = document.createElement('button');
            sttBtn.type = 'button';
            sttBtn.className = 'basic-button btn-sm daw-reftext-stt';
            sttBtn.textContent = 'Transcribe';
            sttBtn.title = 'Fill the transcript automatically — runs the selected clip through speech-to-text';
            sttBtn.style.display = enginesForCategory('stt').length ? '' : 'none';
            sttBtn.addEventListener('click', async () => {
                const sel = findClipById(state.selectedClipId);
                if (!sel) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Select a clip to transcribe', 'notice-pop-yellow');
                    return;
                }
                sttBtn.disabled = true;
                sttBtn.textContent = 'Transcribing…';
                try {
                    const b64 = await AudioLabCore.readAsBase64(sel.clip.blob);
                    const result = await AudioLabAPI.processSTT(b64);
                    if (!result.success || !result.transcription) throw new Error(result.error || 'No transcription returned');
                    refTextInput.value = result.transcription.trim();
                    refCheck.checked = true;
                } catch (err) {
                    console.error('[AudioDaw] Transcription failed:', err);
                    if (typeof doNoticePopover === 'function') doNoticePopover('Transcription failed: ' + err.message, 'notice-pop-red');
                } finally {
                    sttBtn.disabled = false;
                    sttBtn.textContent = 'Transcribe';
                }
            });
            const refTextWrap = createDiv(null, 'daw-reftext-wrap');
            refTextWrap.appendChild(refTextInput);
            refTextWrap.appendChild(sttBtn);
            refRow.appendChild(refCheck);
            refRow.appendChild(refLabel);
            refRow.appendChild(refTextWrap);
            sideCol.appendChild(refRow);

            refNote = createDiv(null, 'daw-stems-clipinfo');
            refNote.textContent = 'This engine uses its built-in voice — voice cloning is not supported.';
            sideCol.appendChild(refNote);
        }

        const supportsVoiceRef = () => (currentEngine()?.features || []).includes('tts_voice_ref');

        // Action row: hint above a full-width Generate button
        const actionRow = createDiv(null, 'daw-stems-action-row');
        const hint = createSpan(null, 'daw-stems-clipinfo');
        hint.textContent = 'Result is added as a new track at the playhead (and saved to your outputs).';
        actionRow.appendChild(hint);
        const goBtn = document.createElement('button');
        goBtn.className = 'basic-button btn-sm daw-stems-go';
        goBtn.textContent = 'Generate';
        actionRow.appendChild(goBtn);
        sideCol.appendChild(actionRow);

        function refreshVisibility() {
            if (isTts) {
                refRow.style.display = supportsVoiceRef() ? '' : 'none';
                refNote.style.display = supportsVoiceRef() ? 'none' : '';
            }
            rebuildModels();
        }
        engineSelect.addEventListener('change', refreshVisibility);
        refreshVisibility();

        goBtn.addEventListener('click', async () => {
            const eng = currentEngine();
            if (!eng) return;
            const models = (eng.models || []).filter(m => m.installed !== false);
            const modelDef = models.find(m => m.id === modelSelect.value) || models[0];
            if (!modelDef?.swarm_model) {
                if (typeof doNoticePopover === 'function') doNoticePopover('No installed model for this engine', 'notice-pop-yellow');
                return;
            }
            const promptText = promptArea.value.trim();
            if (!promptText) {
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover(isTts ? 'Enter text to speak first' : 'Enter a prompt first', 'notice-pop-yellow');
                }
                return;
            }
            // Core T2I params — same pipeline + param ids as the main Generate tab
            const params = {};
            if (isTts) {
                if (refCheck.checked && supportsVoiceRef()) {
                    const sel = findClipById(state.selectedClipId);
                    if (!sel) {
                        if (typeof doNoticePopover === 'function') doNoticePopover('Select a clip to use as voice reference', 'notice-pop-yellow');
                        return;
                    }
                    const b64 = await AudioLabCore.readAsBase64(sel.clip.blob);
                    params.referenceaudio = `data:${sel.clip.blob.type || 'audio/wav'};base64,${b64}`;
                    if (refTextInput.value.trim()) params.referencetext = refTextInput.value.trim();
                }
            } else {
                if (lyricsArea?.value.trim()) params.lyrics = lyricsArea.value.trim();
                params.text2audioduration = Math.max(1, parseFloat(durationInput.value) || (isMusicCat ? 20 : 3));
                const seed = parseInt(seedInput.value);
                if (!isNaN(seed) && seed >= 0) params.seed = seed;
                if (isMusicCat && tempoCheck?.checked) {
                    params.text2audiobpm = state.bpm;
                    params.text2audiotimesignature = state.timeSignature.join('/');
                }
            }

            // Non-blocking: only the Generate button locks; the rest of the DAW
            // stays usable while the engine works (progress on the tab too).
            goBtn.disabled = true;
            const busy = createBusyIndicator(`Generating with ${eng.name}…`, 'generate');
            sideCol.appendChild(busy);
            try {
                const { blob } = await dawSwarmGenerate({
                    model: modelDef.swarm_model,
                    prompt: promptText,
                    params,
                    onProgress: (frac) => busy.setProgress(frac)
                });
                pushUndo();
                const track = addTrack({ name: eng.name });
                await addClipToTrack(track, blob, {
                    name: promptText.slice(0, 28) || eng.name,
                    startTime: snapTime(state.currentTime)
                });
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
                if (typeof doNoticePopover === 'function') doNoticePopover('Generated clip added (also saved to your outputs)', 'notice-pop-green');
            } catch (err) {
                console.error('[AudioDaw] Generate failed:', err);
                if (typeof doNoticePopover === 'function') doNoticePopover('Generate failed: ' + err.message, 'notice-pop-red');
            } finally {
                busy.done();
                goBtn.disabled = false;
            }
        });
    }

    /** Select a clip and jump to the Stems tab so its options can be configured before separating. */
    function openStemsForClip(clip, track) {
        if (!clip) return;
        state.selectedClipId = clip.id;
        if (track) state.selectedTrackId = track.id;
        updateTrackSelection();
        updateBottomPanel();
        switchBottomTab('stems');
        renderAllTracks();
    }

    /** Sum several equal-length stem AudioBuffers into one WAV blob (used for Karaoke/Instrumental combines). */
    function sumBuffersToWav(buffers) {
        const ref = buffers[0];
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        const out = ctx.createBuffer(ref.numberOfChannels, ref.length, ref.sampleRate);
        for (let ch = 0; ch < ref.numberOfChannels; ch++) {
            const dst = out.getChannelData(ch);
            for (const buf of buffers) {
                const src = buf.getChannelData(Math.min(ch, buf.numberOfChannels - 1));
                const n = Math.min(dst.length, src.length);
                for (let i = 0; i < n; i++) dst[i] += src[i];
            }
        }
        ctx.close();
        return audioBufferToWav(out);
    }

    /**
     * Render the arrangement offline through the SAME node graph + scheduleClip
     * math as live playback, so exports always match what you hear.
     */
    async function renderMixdownBuffer() {
        if (state.tracks.length === 0 || state.contentDuration <= 0) return null;
        const sampleRate = 44100;
        // Export ends with the music; leave room only when FX tails need to ring out.
        const hasTails = state.tracks.some(t => (t.fx || []).some(f =>
            f.enabled && (f.type === 'reverb' || f.type === 'delay')));
        const renderEnd = state.contentDuration + (hasTails ? 5 : 0.1);
        const totalSamples = Math.max(1, Math.ceil(renderEnd * sampleRate));
        const offlineCtx = new OfflineAudioContext(2, totalSamples, sampleRate);
        const masterGain = offlineCtx.createGain();
        masterGain.gain.value = state.masterVolume;
        let masterOut = masterGain;
        if (state.masterLimiterEnabled && typeof AudioDawFx !== 'undefined') {
            const lim = AudioDawFx.buildMasterLimiter(offlineCtx);
            masterGain.connect(lim);
            masterOut = lim;
        }
        masterOut.connect(offlineCtx.destination);
        const soloActive = hasSoloTracks();
        for (const track of state.tracks) {
            if (track.muted || (soloActive && !track.soloed)) continue; // no point rendering silence
            const chain = buildTrackChain(offlineCtx, track, masterGain);
            scheduleTrackAutomation(chain, track, { t0Ctx: 0, t0Timeline: 0, windowEnd: renderEnd, fresh: true });
            for (const clip of track.clips) {
                if (clip.muted) continue;
                scheduleClip(offlineCtx, chain.trackGain, clip,
                    { t0Ctx: 0, t0Timeline: 0, windowEnd: renderEnd });
            }
        }
        return await offlineCtx.startRendering();
    }

    /** Render mixdown to a WAV Blob (export). */
    async function renderMixdownBlob() {
        try {
            const rendered = await renderMixdownBuffer();
            return rendered ? audioBufferToWav(rendered) : null;
        } catch (err) {
            console.error('[AudioDaw] Mixdown render failed:', err);
            return null;
        }
    }

    function findClipById(clipId) {
        if (!clipId) return null;
        for (const track of state.tracks) {
            const clip = track.clips.find(c => c.id === clipId);
            if (clip) return { clip, track };
        }
        return null;
    }

    // ===== SCROLL SYNC =====

    function setupScrollSync() {
        if (!clipLanesEl) return;
        let syncing = false;
        clipLanesEl.addEventListener('scroll', () => {
            if (syncing) return;
            syncing = true;
            state.scrollLeft = clipLanesEl.scrollLeft;
            // Sync ruler horizontal scroll
            if (rulerContainer) rulerContainer.scrollLeft = clipLanesEl.scrollLeft;
            // Sync track headers vertical scroll
            if (trackHeadersEl) trackHeadersEl.scrollTop = clipLanesEl.scrollTop;
            syncing = false;
        });
        // Reverse direction: scrolling the header column scrolls the lanes
        if (trackHeadersEl) {
            trackHeadersEl.addEventListener('scroll', () => {
                if (syncing) return;
                syncing = true;
                clipLanesEl.scrollTop = trackHeadersEl.scrollTop;
                syncing = false;
            });
        }

        // Click or drag on empty lane area (or the playhead line itself) to scrub;
        // the final position commits on release.
        const laneTimeAt = (e) => {
            const rect = clipLanesEl.getBoundingClientRect();
            const x = e.clientX - rect.left + clipLanesEl.scrollLeft;
            return snapTime(Math.max(0, Math.min(x / state.zoom, state.totalDuration)));
        };
        let lastLaneScrub = 0;
        const laneScrub = (t) => {
            if (state.isPlaying) {
                const now = performance.now();
                if (now - lastLaneScrub > 120) { lastLaneScrub = now; seekTo(t); }
            } else {
                state.currentTime = t;
                updatePlayheadPosition();
                updateTimeDisplay();
            }
        };
        const setupLaneScrub = (el, accepts) => {
            el.addEventListener('pointerdown', (e) => {
                if (e.button !== 0 || !accepts(e)) return;
                e.preventDefault();
                el.setPointerCapture(e.pointerId);
                laneScrub(laneTimeAt(e));
                const onMove = (me) => laneScrub(laneTimeAt(me));
                const onUp = (ue) => {
                    el.releasePointerCapture(ue.pointerId);
                    el.removeEventListener('pointermove', onMove);
                    el.removeEventListener('pointerup', onUp);
                    seekTo(laneTimeAt(ue));
                };
                el.addEventListener('pointermove', onMove);
                el.addEventListener('pointerup', onUp);
            });
        };
        setupLaneScrub(clipLanesEl, (e) => e.target === clipLanesEl || e.target.classList.contains('daw-track-lane'));
        if (playheadEl) setupLaneScrub(playheadEl, () => true);
    }

    // ===== TRACK MANAGEMENT =====

    function addTrack(opts = {}) {
        const track = AudioDawTrack.createTrack(opts);
        state.tracks.push(track);
        if (!state.selectedTrackId) state.selectedTrackId = track.id;
        return track;
    }

    async function addClipToTrack(track, blob, opts = {}) {
        const clip = AudioDawTrack.createClip(blob, opts);

        // Store in blob store
        blobStore.set(clip.blobKey, { blob, decodedBuffer: null });

        // Decode audio
        await AudioDawTrack.decodeClip(clip);

        // Update blob store with decoded buffer
        const stored = blobStore.get(clip.blobKey);
        if (stored) stored.decodedBuffer = clip.decodedBuffer;

        // Position at end of existing clips (or at startTime if specified)
        if (opts.startTime === undefined) {
            clip.startTime = AudioDawTrack.getTrackDuration(track);
        }

        track.clips.push(clip);
        return clip;
    }

    function getSelectedTrack() {
        return state.tracks.find(t => t.id === state.selectedTrackId) || state.tracks[0];
    }

    function removeTrack(trackId) {
        const idx = state.tracks.findIndex(t => t.id === trackId);
        if (idx === -1) return;
        const track = state.tracks[idx];
        // Blobs stay in blobStore for undo; garbageCollectBlobs() reclaims them later
        AudioDawTrack.destroyTrack(track);
        state.tracks.splice(idx, 1);
        if (state.selectedTrackId === trackId) {
            state.selectedTrackId = state.tracks[0]?.id || null;
        }
    }

    // ===== RENDERING =====

    function renderAllTracks() {
        if (!trackHeadersEl || !clipLanesEl) return;

        // Clear existing (except playhead)
        trackHeadersEl.innerHTML = '';
        // Remove all track lanes but keep playhead
        const existingLanes = clipLanesEl.querySelectorAll('.daw-track-lane');
        existingLanes.forEach(el => el.remove());

        const trackCallbacks = {
            // Rebuild the mixer tab on M/S so its buttons stay in sync with the headers
            // (volume skipped: rebuilding mid-drag would fight the slider)
            onMute: () => { updatePlaybackGains(); updateBottomPanel(); },
            onSolo: () => { updatePlaybackGains(); updateBottomPanel(); },
            onVolume: () => { updatePlaybackGains(); },
            onPan: () => { updatePlaybackGains(); },
            onArm: () => {},
            onSelect: (track) => {
                state.selectedTrackId = track.id;
                updateTrackSelection();
                updateBottomPanel();
            },
            onRename: () => {},
            onRecolor: (track, e) => showTrackColorMenu(track, e),
            onAutomationToggle: (track) => {
                track.automationVisible = !track.automationVisible;
                renderAllTracks();
            },
            onRemove: (track) => {
                if (state.tracks.length <= 1) return; // keep at least one track
                pushUndo();
                removeTrack(track.id);
                updateTotalDuration();
                renderAllTracks();
                resyncPlayback();
            }
        };

        const clipCallbacks = {
            snapTime: (t) => snapTime(t),
            onClipSelect: (clip, track) => {
                state.selectedClipId = clip.id;
                state.selectedTrackId = track.id;
                updateTrackSelection();
                updateBottomPanel();
                renderAllTracks(); // re-render to update selection highlight
            },
            onClipDragStart: () => {
                pushUndo(); // one undo step per drag gesture (covers in-lane and cross-track)
            },
            onClipTrimStart: () => {
                pushUndo();
            },
            onClipTrimEnd: () => {
                updateTotalDuration();
                updateBottomPanel();
                resyncPlayback();
            },
            onClipMove: () => {
                updateTotalDuration();
                resyncPlayback();
            },
            onTimelineExtend: (seconds) => {
                // Mid-drag growth past the current end; updateTotalDuration()
                // snaps back to content + padding on drop.
                if (seconds > state.totalDuration) {
                    state.totalDuration = seconds;
                    if (timeline) timeline.setDuration(state.totalDuration);
                    updateClipLanesWidth();
                }
            },
            onClipContext: (e, clip, track) => {
                showClipContextMenu(e, clip, track);
            },
            onClipCrossTrack: (clip, srcTrack, targetTrackId) => {
                const targetTrack = state.tracks.find(t => t.id === targetTrackId);
                if (!targetTrack) return;
                // Remove from source track
                const idx = srcTrack.clips.indexOf(clip);
                if (idx !== -1) srcTrack.clips.splice(idx, 1);
                // Clean up source clip elements
                const entry = srcTrack.clipElements.get(clip.id);
                if (entry?.ws) entry.ws.destroy();
                if (entry?.el) entry.el.remove();
                srcTrack.clipElements.delete(clip.id);
                // Add to target track
                targetTrack.clips.push(clip);
                updateTotalDuration();
                renderAllTracks();
                resyncPlayback();
            }
        };

        for (const track of state.tracks) {
            const header = AudioDawTrack.buildTrackHeader(track, trackCallbacks);
            trackHeadersEl.appendChild(header);

            const lane = AudioDawTrack.buildClipLane(track, state.zoom, clipCallbacks);
            clipLanesEl.appendChild(lane);

            AudioDawTrack.renderClips(track, state.zoom, clipCallbacks, state.selectedClipId);

            if (track.automationVisible) {
                const autoCallbacks = {
                    snapTime: (t) => snapTime(t),
                    onAutomationGestureStart: () => pushUndo(),
                    onAutomationChange: (tr) => rescheduleTrackAutomation(tr)
                };
                clipLanesEl.appendChild(AudioDawTrack.buildAutomationLane(track, state.zoom, autoCallbacks));
                // Matching header spacer keeps headers aligned; hosts the param picker
                const spacer = createDiv(null, 'daw-automation-head');
                const sel = document.createElement('select');
                sel.className = 'daw-fx-select';
                for (const p of ['volume', 'pan']) {
                    const opt = document.createElement('option');
                    opt.value = p;
                    opt.textContent = p === 'volume' ? 'Volume' : 'Pan';
                    sel.appendChild(opt);
                }
                sel.value = track.automationParam || 'volume';
                sel.addEventListener('change', () => {
                    track.automationParam = sel.value;
                    AudioDawTrack.renderAutomationLane(track, state.zoom, autoCallbacks);
                });
                spacer.appendChild(sel);
                trackHeadersEl.appendChild(spacer);
            }
        }

        // "+ Add Track" affordance at the bottom of the header column
        const addBtn = document.createElement('button');
        addBtn.className = 'daw-add-track';
        addBtn.innerHTML = '+ <span class="translate">Add Track</span>';
        addBtn.title = 'Add an empty track';
        addBtn.addEventListener('click', () => {
            pushUndo();
            const track = addTrack();
            state.selectedTrackId = track.id;
            renderAllTracks();
        });
        trackHeadersEl.appendChild(addBtn);

        updateTrackSelection();
        updateClipLanesWidth();
    }

    function updateTrackSelection() {
        // Highlight selected track header
        if (!trackHeadersEl) return;
        trackHeadersEl.querySelectorAll('.daw-track-header').forEach(el => {
            el.classList.toggle('selected', el.dataset.trackId === state.selectedTrackId);
        });
    }

    function updateClipLanesWidth() {
        // Set min-width of clip lanes to match total duration
        const minWidth = Math.max(state.totalDuration * state.zoom + 200, clipLanesEl?.clientWidth || 0);
        if (clipLanesEl) {
            clipLanesEl.querySelectorAll('.daw-track-lane, .daw-automation-lane').forEach(lane => {
                lane.style.minWidth = minWidth + 'px';
            });
        }
    }

    function updateTotalDuration() {
        // contentDuration = where the music actually ends (playback/export stop here);
        // totalDuration = ruler length, kept longer so clips can be dragged past the end.
        let content = 0;
        for (const track of state.tracks) {
            const td = AudioDawTrack.getTrackDuration(track);
            if (td > content) content = td;
        }
        state.contentDuration = content;
        state.totalDuration = Math.max(10, content) + 5;
        if (timeline) timeline.setDuration(state.totalDuration);
        updateClipLanesWidth();
    }

    // ===== PLAYBACK ENGINE (Web Audio API) =====

    function getAudioContext() {
        if (!audioCtx || audioCtx.state === 'closed') {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        }
        return audioCtx;
    }

    function hasSoloTracks() {
        return state.tracks.some(t => t.soloed);
    }

    /** Effective gain for a track's chain given mute/solo state. */
    function computeTrackGain(track) {
        const soloActive = hasSoloTracks();
        const audible = !track.muted && (!soloActive || track.soloed);
        return audible ? track.volume : 0;
    }

    /** Create a track's persistent node chain: trackGain -> trackPan -> masterGain (+ analyser tap for meters). */
    function buildTrackChain(ctx, track, masterGain) {
        const trackGain = ctx.createGain();
        trackGain.gain.value = computeTrackGain(track);
        // Insert FX after the static fader; volAuto carries the volume envelope so
        // live fader moves and automation ramps never fight over one AudioParam.
        const fxChain = (typeof AudioDawFx !== 'undefined')
            ? AudioDawFx.buildFxChain(ctx, track.fx, { bpm: state.bpm }) : null;
        const volAuto = ctx.createGain();
        volAuto.gain.value = 1;
        if (fxChain) {
            trackGain.connect(fxChain.input);
            fxChain.output.connect(volAuto);
        } else {
            trackGain.connect(volAuto);
        }
        let trackPan = null;
        if (ctx.createStereoPanner) {
            trackPan = ctx.createStereoPanner();
            trackPan.pan.value = track.pan || 0;
            volAuto.connect(trackPan);
            trackPan.connect(masterGain);
        } else {
            volAuto.connect(masterGain);
        }
        let analysers = null;
        let meterSplitter = null;
        if (typeof OfflineAudioContext === 'undefined' || !(ctx instanceof OfflineAudioContext)) {
            // Per-channel L/R taps for OBS-style stereo metering
            meterSplitter = ctx.createChannelSplitter(2);
            (trackPan || volAuto).connect(meterSplitter);
            analysers = [ctx.createAnalyser(), ctx.createAnalyser()];
            analysers.forEach((a, i) => { a.fftSize = 512; meterSplitter.connect(a, i); });
        }
        return { trackGain, fxChain, volAuto, trackPan, analysers, meterSplitter };
    }

    /** Fully disconnect one track chain (incl. FX feedback loops, which otherwise persist). */
    function disposeChain(chain) {
        try { chain.trackGain.disconnect(); } catch (_) {}
        if (typeof AudioDawFx !== 'undefined') AudioDawFx.disposeFxChain(chain.fxChain);
        try { chain.volAuto.disconnect(); } catch (_) {}
        if (chain.trackPan) { try { chain.trackPan.disconnect(); } catch (_) {} }
        if (chain.meterSplitter) { try { chain.meterSplitter.disconnect(); } catch (_) {} }
        for (const a of chain.analysers || []) { try { a.disconnect(); } catch (_) {} }
    }

    /** Topology change on a track's FX (add/remove/reorder): rebuild its chain live. */
    function rebuildTrackFx(track) {
        if (!playback) return;
        const chain = playback.chains.get(track.id);
        if (chain) {
            disposeChain(chain);
            playback.chains.delete(track.id);
        }
        resyncPlayback(); // lazily recreates the chain during rescheduling
    }

    /** Schedule volume/pan envelopes onto a chain using the same origin math as clips. */
    function scheduleTrackAutomation(chain, track, { t0Ctx, t0Timeline, windowEnd = Infinity, fresh = true }) {
        if (typeof AudioDawFx === 'undefined') return;
        const auto = track.automation || {};
        if (auto.volume?.length) {
            AudioDawFx.scheduleEnvelope(chain.volAuto.gain, auto.volume, { t0Ctx, t0Timeline, windowEnd, fresh });
        }
        if (chain.trackPan && auto.pan?.length) {
            AudioDawFx.scheduleEnvelope(chain.trackPan.pan, auto.pan, {
                t0Ctx, t0Timeline, windowEnd, fresh,
                transform: (v) => Math.max(-1, Math.min(1, (track.pan || 0) + v))
            });
        }
    }

    /** Live envelope edit: re-anchor + re-ramp this track's automation from the current position. */
    function rescheduleTrackAutomation(track) {
        if (!playback || typeof AudioDawFx === 'undefined') return;
        const chain = playback.chains.get(track.id);
        if (!chain) return;
        const now = playback.ctx.currentTime;
        const P = currentTimelineTime();
        const W = playback.loop ? state.loopEnd : Infinity;
        const auto = track.automation;
        if (auto.volume.length) {
            AudioDawFx.scheduleEnvelope(chain.volAuto.gain, auto.volume,
                { t0Ctx: now, t0Timeline: P, windowEnd: W, fresh: true });
        } else {
            try { chain.volAuto.gain.cancelScheduledValues(now); } catch (_) {}
            chain.volAuto.gain.setTargetAtTime(1, now, 0.015);
        }
        if (chain.trackPan) {
            const xf = (v) => Math.max(-1, Math.min(1, (track.pan || 0) + v));
            if (auto.pan.length) {
                AudioDawFx.scheduleEnvelope(chain.trackPan.pan, auto.pan,
                    { t0Ctx: now, t0Timeline: P, windowEnd: W, fresh: true, transform: xf });
            } else {
                try { chain.trackPan.pan.cancelScheduledValues(now); } catch (_) {}
                chain.trackPan.pan.setTargetAtTime(track.pan || 0, now, 0.015);
            }
        }
        // Re-queue the pre-scheduled loop iteration the fresh cancel wiped
        if (playback.loop?.nextIterScheduled) {
            scheduleTrackAutomation(chain, track, {
                t0Ctx: playback.loop.nextWrapCtxTime, t0Timeline: state.loopStart,
                windowEnd: state.loopEnd, fresh: false
            });
        }
    }

    // ===== LEVEL METERS =====

    const meterBuf = new Uint8Array(512);

    function readPeak(analyser) {
        analyser.getByteTimeDomainData(meterBuf);
        let peak = 0;
        for (let i = 0; i < analyser.fftSize; i++) {
            const v = Math.abs(meterBuf[i] - 128) / 128;
            if (v > peak) peak = v;
        }
        return peak;
    }

    // Slow RMS average for the loudness readout (~LUFS, no K-weighting)
    let loudnessEma = 0;

    function readRms(analyser) {
        analyser.getByteTimeDomainData(meterBuf);
        let sum = 0;
        for (let i = 0; i < analyser.fftSize; i++) {
            const v = (meterBuf[i] - 128) / 128;
            sum += v * v;
        }
        return Math.sqrt(sum / analyser.fftSize);
    }

    // Peak-hold state per meter: key -> { peak, heldAt }
    const meterPeaks = new Map();
    const PEAK_HOLD_MS = 900;
    const PEAK_FALL_PER_FRAME = 0.012;

    /** Track a decaying held peak for one meter. */
    function heldPeak(key, level) {
        let s = meterPeaks.get(key);
        if (!s) { s = { peak: 0, heldAt: 0 }; meterPeaks.set(key, s); }
        const now = performance.now();
        if (level >= s.peak) {
            s.peak = level;
            s.heldAt = now;
        } else if (now - s.heldAt > PEAK_HOLD_MS) {
            s.peak = Math.max(level, s.peak - PEAK_FALL_PER_FRAME);
        }
        return s.peak;
    }

    /** Peak (0..1) -> meter height fraction on a -60..0 dB scale (OBS-style). */
    function levelToPct(peak) {
        if (peak <= 0.001) return 0;
        const db = 20 * Math.log10(peak);
        return Math.max(0, Math.min(1, (db + 60) / 60));
    }

    function readChanPeaks(analysers) {
        if (analysers?.length === 2) return [readPeak(analysers[0]), readPeak(analysers[1])];
        return [0, 0];
    }

    /** Write one channel's level + held peak into its DOM pair (mask covers the UNLIT part). */
    function setMeterChan(chan, pct, peakPct, vertical) {
        if (!chan) return;
        if (vertical) {
            chan.mask.style.height = ((1 - pct) * 100).toFixed(1) + '%';
            chan.peak.style.bottom = (peakPct * 100).toFixed(1) + '%';
        } else {
            chan.mask.style.width = ((1 - pct) * 100).toFixed(1) + '%';
            chan.peak.style.left = `calc(${(peakPct * 100).toFixed(1)}% - 2px)`;
        }
    }

    /** Push live stereo levels + peak-hold ticks into header meters, mixer strips, and the master meter. */
    function updateMeters() {
        if (!playback) return;
        for (const track of state.tracks) {
            const chain = playback.chains.get(track.id);
            if (!chain?.analysers) continue;
            const peaks = readChanPeaks(chain.analysers);
            const strip = AudioDawMixer.getMeterEl?.(track.id);
            for (let ch = 0; ch < 2; ch++) {
                const pct = levelToPct(peaks[ch]);
                const held = heldPeak(`${track.id}:${ch}`, pct);
                setMeterChan(track.meterChans?.[ch], pct, held, false);
                setMeterChan(strip?.chans?.[ch], pct, held, true);
            }
        }
        if (playback.masterAnalysers) {
            const peaks = readChanPeaks(playback.masterAnalysers);
            const strip = AudioDawMixer.getMeterEl?.('__master__');
            for (let ch = 0; ch < 2; ch++) {
                const pct = levelToPct(peaks[ch]);
                const held = heldPeak(`__master__:${ch}`, pct);
                setMeterChan(strip?.chans?.[ch], pct, held, true);
            }
            // ~Loudness readout (slow RMS EMA; not true K-weighted LUFS but tracks it usefully)
            const rms = readRms(playback.masterAnalysers[0]);
            loudnessEma = loudnessEma * 0.95 + rms * 0.05;
            const lufsEl = document.querySelector('.daw-master-lufs');
            if (lufsEl) {
                const db = loudnessEma > 1e-5 ? (20 * Math.log10(loudnessEma)).toFixed(1) : '-\u221E';
                lufsEl.textContent = db + ' LU';
                lufsEl.title = 'Approximate loudness (RMS). Streaming targets sit around -14.';
            }
            // Clip latch: lights when the master pins, click to reset
            if (Math.max(peaks[0], peaks[1]) >= 0.985) {
                const clipDot = document.querySelector('.daw-master-clip');
                if (clipDot) clipDot.classList.add('lit');
            }
        }
    }

    /** Zero out every meter and held peak (playback stopped). */
    function resetMeters() {
        meterPeaks.clear();
        for (const track of state?.tracks || []) {
            for (const chan of track.meterChans || []) setMeterChan(chan, 0, 0, false);
        }
        if (AudioDawMixer.resetMeters) AudioDawMixer.resetMeters();
    }

    /**
     * Schedule one clip into a context — the single source of truth for placement
     * math, used identically by live playback, loop iterations, and offline export.
     * Node chain per clip: source -> fadeGain (fade ramps only) -> clipGain (mute/gain) -> destNode.
     * @param {BaseAudioContext} ctx
     * @param {AudioNode} destNode - the track's trackGain
     * @param {Object} clip
     * @param {Object} origin - { t0Ctx, t0Timeline, windowEnd }: ctx time t0Ctx corresponds
     *   to timeline time t0Timeline; nothing is scheduled at/after windowEnd (loop/export bound).
     * @returns {{source, fadeGain, clipGain}|null}
     */
    function scheduleClip(ctx, destNode, clip, { t0Ctx, t0Timeline, windowEnd = Infinity }) {
        const P = t0Timeline;
        const visibleDur = clip.duration - clip.offset - clip.trimEnd;
        if (!clip.decodedBuffer || visibleDur <= 0) return null;
        const clipStart = clip.startTime;
        const clipEnd = clipStart + visibleDur;
        if (clipEnd <= P || clipStart >= windowEnd) return null;

        const into = Math.max(0, P - clipStart);
        const when = t0Ctx + Math.max(0, clipStart - P);
        const bufferOffset = clip.offset + into;
        const playDuration = Math.min(visibleDur - into, windowEnd - Math.max(P, clipStart));
        if (playDuration <= 0) return null;

        const source = ctx.createBufferSource();
        source.buffer = clip.decodedBuffer;
        const fadeGain = ctx.createGain();
        const clipGain = ctx.createGain();
        clipGain.gain.value = clip.muted ? 0 : (clip.gain ?? 1);
        source.connect(fadeGain);
        fadeGain.connect(clipGain);
        clipGain.connect(destNode);

        // Fade envelope; a mid-clip start resumes at the exact instantaneous value
        const fi = Math.min(clip.fadeIn || 0, visibleDur);
        const fo = Math.min(clip.fadeOut || 0, visibleDur - fi);
        if (fi > 0 || fo > 0) {
            const u0 = into;
            const foStart = visibleDur - fo;
            let startVal = 1;
            if (fi > 0 && u0 < fi) startVal = u0 / fi;
            else if (fo > 0 && u0 > foStart) startVal = (visibleDur - u0) / fo;
            fadeGain.gain.setValueAtTime(startVal, when);
            if (fi > 0 && u0 < fi) fadeGain.gain.linearRampToValueAtTime(1, when + (fi - u0));
            if (fo > 0 && u0 < visibleDur) {
                if (u0 < foStart) fadeGain.gain.setValueAtTime(1, when + (foStart - u0));
                fadeGain.gain.linearRampToValueAtTime(0, when + (visibleDur - u0));
            }
        }

        source.start(when, bufferOffset, playDuration);
        return { source, fadeGain, clipGain };
    }

    /**
     * Schedule every clip of every track from timeline time P.
     * ALL clips are scheduled — muted ones at gain 0 — so mute/solo/unmute during
     * playback is always a pure AudioParam write, never a missing source.
     */
    function scheduleTransportFrom(P, { t0Ctx, windowEnd = Infinity, iteration = 0 }) {
        const ctx = playback.ctx;
        for (const track of state.tracks) {
            let chain = playback.chains.get(track.id);
            if (!chain) {
                chain = buildTrackChain(ctx, track, playback.masterGain);
                playback.chains.set(track.id, chain);
            }
            scheduleTrackAutomation(chain, track, { t0Ctx, t0Timeline: P, windowEnd, fresh: iteration === 0 });
            for (const clip of track.clips) {
                const nodes = scheduleClip(ctx, chain.trackGain, clip, { t0Ctx, t0Timeline: P, windowEnd });
                if (!nodes) continue;
                const entry = { clipId: clip.id, trackId: track.id, iteration, ...nodes };
                nodes.source.onended = () => {
                    if (!playback) return;
                    const i = playback.liveClips.indexOf(entry);
                    if (i >= 0) playback.liveClips.splice(i, 1);
                };
                playback.liveClips.push(entry);
            }
        }
    }

    /** Param-only update of the whole gain topology (mute/solo/volume/pan/master). */
    function updatePlaybackGains() {
        if (!playback) return;
        const now = playback.ctx.currentTime;
        playback.masterGain.gain.setTargetAtTime(state.masterVolume, now, 0.015);
        for (const track of state.tracks) {
            const chain = playback.chains.get(track.id);
            if (!chain) continue;
            chain.trackGain.gain.setTargetAtTime(computeTrackGain(track), now, 0.015);
            const panEnv = track.automation?.pan;
            if (chain.trackPan && !(panEnv?.length)) {
                chain.trackPan.pan.setTargetAtTime(track.pan || 0, now, 0.015);
            }
            else if (chain.trackPan && panEnv?.length && typeof AudioDawFx !== 'undefined') {
                // Envelope active: re-anchor + re-ramp instead of fighting scheduled ramps
                const xf = (v) => Math.max(-1, Math.min(1, (track.pan || 0) + v));
                const P = currentTimelineTime();
                AudioDawFx.scheduleEnvelope(chain.trackPan.pan, panEnv, {
                    t0Ctx: now, t0Timeline: P,
                    windowEnd: playback.loop ? state.loopEnd : Infinity, fresh: true, transform: xf
                });
                if (playback.loop?.nextIterScheduled) {
                    AudioDawFx.scheduleEnvelope(chain.trackPan.pan, panEnv, {
                        t0Ctx: playback.loop.nextWrapCtxTime, t0Timeline: state.loopStart,
                        windowEnd: state.loopEnd, fresh: false, transform: xf
                    });
                }
            }
        }
    }

    /** Param-only update for one clip's gain/mute on all its live sources. */
    function applyClipGain(clip) {
        if (!playback) return;
        const now = playback.ctx.currentTime;
        for (const n of playback.liveClips) {
            if (n.clipId === clip.id) {
                n.clipGain.gain.setTargetAtTime(clip.muted ? 0 : (clip.gain ?? 1), now, 0.015);
            }
        }
    }

    /** Timeline position derived from the audio clock (authoritative while playing). */
    function currentTimelineTime() {
        if (!playback) return state.currentTime;
        return Math.max(playback.baseTimelineTime,
            playback.baseTimelineTime + (playback.ctx.currentTime - playback.baseCtxTime));
    }

    function togglePlayback() {
        if (state.isPlaying) {
            stopPlayback();
        } else {
            startPlayback();
        }
    }

    function toggleLoop() {
        state.loopEnabled = !state.loopEnabled;
        // First enable with no region set: default to the whole arrangement
        if (state.loopEnabled && state.loopEnd <= state.loopStart) {
            state.loopStart = 0;
            state.loopEnd = state.contentDuration || state.totalDuration;
        }
        const btn = transportEl?.querySelector('.daw-btn-loop');
        if (btn) btn.classList.toggle('active', state.loopEnabled);
        if (timeline) timeline.setLoop(state.loopEnabled, state.loopStart, state.loopEnd);
        updateLoopShade();
        resyncPlayback();
    }

    async function startPlayback() {
        if (state.isPlaying) return;
        state.isPlaying = true;
        updatePlayButton(true);

        // Safety: a wedged recording session (record-end never fired) must not put
        // plain playback into record mode (unclamped playhead, no end-stop)
        if (recording && (!recording.recorder || !recording.recorder.isRecording())) {
            abortRecording();
        }

        const ctx = getAudioContext();
        if (ctx.state === 'suspended') {
            try { await ctx.resume(); } catch (_) {}
        }
        if (!state.isPlaying) return; // user hit stop while we awaited the resume

        const masterGain = ctx.createGain();
        masterGain.gain.value = state.masterVolume;
        let masterLimiter = null;
        let masterOut = masterGain;
        if (state.masterLimiterEnabled && typeof AudioDawFx !== 'undefined') {
            masterLimiter = AudioDawFx.buildMasterLimiter(ctx);
            masterGain.connect(masterLimiter);
            masterOut = masterLimiter;
        }
        masterOut.connect(ctx.destination);
        // Post-limiter stereo taps for the master meter
        const masterSplitter = ctx.createChannelSplitter(2);
        masterOut.connect(masterSplitter);
        const masterAnalysers = [ctx.createAnalyser(), ctx.createAnalyser()];
        masterAnalysers.forEach((a, i) => { a.fftSize = 512; masterSplitter.connect(a, i); });

        const P = state.currentTime;
        const t0Ctx = ctx.currentTime + START_EPSILON;
        // Loop is suppressed while recording (a take shouldn't wrap onto itself)
        const loopArmed = !recording && state.loopEnabled && state.loopEnd > state.loopStart && P < state.loopEnd;
        playback = {
            ctx, masterGain, masterLimiter, masterAnalysers,
            chains: new Map(),
            liveClips: [],
            baseCtxTime: t0Ctx,
            baseTimelineTime: P,
            // Stop at the end of the music, not the ruler; starting past the
            // content deliberately lets the transport run out the ruler instead.
            endTime: P < state.contentDuration ? state.contentDuration : state.totalDuration,
            schedulerId: null,
            loop: loopArmed ? {
                nextWrapCtxTime: t0Ctx + (state.loopEnd - P),
                nextIterScheduled: false,
                iteration: 0
            } : null
        };
        scheduleTransportFrom(P, { t0Ctx, windowEnd: loopArmed ? state.loopEnd : Infinity, iteration: 0 });
        playback.schedulerId = setInterval(schedulerTick, SCHEDULER_TICK_MS);
        animatePlayhead();
    }

    /**
     * Audio-clock scheduler: commits sample-accurate loop wraps and pre-schedules
     * the next loop iteration ahead of the wrap point. Runs on setInterval so it
     * keeps working when the tab is backgrounded (rAF would stall).
     */
    function schedulerTick() {
        try {
            schedulerTickInner();
        } catch (err) {
            console.error('[AudioDaw] Scheduler tick failed:', err);
        }
    }

    function schedulerTickInner() {
        if (!playback) return;
        // Safety: if the stop path was raced past (isPlaying already false), tear down here
        if (!state.isPlaying) {
            teardownPlaybackGraph();
            return;
        }
        const ctx = playback.ctx;
        const loop = playback.loop;
        if (loop) {
            // Commit any wrap that has passed, then top up the schedule
            while (ctx.currentTime >= loop.nextWrapCtxTime) {
                playback.baseCtxTime = loop.nextWrapCtxTime;
                playback.baseTimelineTime = state.loopStart;
                loop.nextWrapCtxTime += (state.loopEnd - state.loopStart);
                loop.nextIterScheduled = false;
            }
            if (!loop.nextIterScheduled && loop.nextWrapCtxTime - ctx.currentTime < LOOKAHEAD) {
                loop.iteration++;
                scheduleTransportFrom(state.loopStart, {
                    t0Ctx: loop.nextWrapCtxTime, windowEnd: state.loopEnd, iteration: loop.iteration
                });
                loop.nextIterScheduled = true;
            }
        } else if (!recording && currentTimelineTime() >= playback.endTime) {
            // While recording, playback runs past the end so the take can extend the song
            stopPlayback();
            state.currentTime = 0;
            updatePlayheadPosition();
            updateTimeDisplay();
        }
    }

    /** Stop and disconnect all live sources + chains + master; leaves state.isPlaying alone. */
    function teardownPlaybackGraph() {
        if (!playback) return;
        clearInterval(playback.schedulerId);
        for (const n of playback.liveClips) {
            n.source.onended = null;
            try { n.source.stop(); } catch (_) {}
            try { n.source.disconnect(); } catch (_) {}
            try { n.fadeGain.disconnect(); } catch (_) {}
            try { n.clipGain.disconnect(); } catch (_) {}
        }
        for (const chain of playback.chains.values()) {
            disposeChain(chain);
        }
        try { playback.masterGain.disconnect(); } catch (_) {}
        if (playback.masterLimiter) { try { playback.masterLimiter.disconnect(); } catch (_) {} }
        playback = null;
        resetMeters();
    }

    function stopPlayback() {
        if (!state || !state.isPlaying) return;
        state.currentTime = Math.min(currentTimelineTime(), playback?.endTime ?? state.totalDuration);
        state.isPlaying = false;
        updatePlayButton(false);
        cancelAnimationFrame(rafId);
        teardownPlaybackGraph();
        updatePlayheadPosition();
        updateTimeDisplay();
    }

    /**
     * Re-schedule playback from the current position after a topology change
     * (clip add/move/delete/trim, track add/remove, undo/redo, loop change).
     * Keeps chains for surviving tracks, the scheduler, and the rAF loop —
     * no play-button churn, no UI flicker. No-op when stopped.
     */
    function resyncPlayback() {
        if (!state.isPlaying || !playback) return;
        const ctx = playback.ctx;
        const P = Math.max(0, Math.min(currentTimelineTime(), state.totalDuration));
        for (const n of playback.liveClips) {
            n.source.onended = null;
            try { n.source.stop(); } catch (_) {}
            try { n.source.disconnect(); } catch (_) {}
            try { n.fadeGain.disconnect(); } catch (_) {}
            try { n.clipGain.disconnect(); } catch (_) {}
        }
        playback.liveClips = [];
        // Drop chains for removed tracks (missing ones are lazily created on schedule)
        const liveIds = new Set(state.tracks.map(t => t.id));
        for (const [id, chain] of playback.chains) {
            if (!liveIds.has(id)) {
                disposeChain(chain);
                playback.chains.delete(id);
            }
        }
        const t0Ctx = ctx.currentTime + START_EPSILON;
        playback.baseCtxTime = t0Ctx;
        playback.baseTimelineTime = P;
        playback.endTime = P < state.contentDuration ? state.contentDuration : state.totalDuration;
        const loopArmed = state.loopEnabled && state.loopEnd > state.loopStart && P < state.loopEnd;
        playback.loop = loopArmed ? {
            nextWrapCtxTime: t0Ctx + (state.loopEnd - P),
            nextIterScheduled: false,
            iteration: 0
        } : null;
        scheduleTransportFrom(P, { t0Ctx, windowEnd: loopArmed ? state.loopEnd : Infinity, iteration: 0 });
        updatePlaybackGains();
    }

    function seekTo(time) {
        state.currentTime = Math.max(0, Math.min(time, state.totalDuration));
        updatePlayheadPosition();
        updateTimeDisplay();
        resyncPlayback();
    }

    /** Display-only rAF loop; the audio clock is authoritative (see schedulerTick). */
    function animatePlayhead() {
        if (!state.isPlaying) return;
        const endTime = playback?.endTime ?? state.totalDuration;
        state.currentTime = recording
            ? currentTimelineTime() // unclamped: a take may run past the current end
            : Math.min(currentTimelineTime(), endTime);
        // Redundant end-stop (schedulerTick owns this; keep a display-side backstop)
        if (!recording && !playback?.loop && currentTimelineTime() >= endTime) {
            stopPlayback();
            state.currentTime = 0;
            updatePlayheadPosition();
            updateTimeDisplay();
            return;
        }
        if (recording) {
            // Grow the live-waveform placeholder and stretch the timeline as needed
            recording.placeholderEl.style.width =
                Math.max(4, (state.currentTime - recording.startP) * state.zoom) + 'px';
            if (state.currentTime + 5 > state.totalDuration) {
                state.totalDuration = state.currentTime + 5;
                if (timeline) timeline.setDuration(state.totalDuration);
                updateClipLanesWidth();
            }
        }
        updatePlayheadPosition();
        updateTimeDisplay();
        updateMeters();
        rafId = requestAnimationFrame(animatePlayhead);
    }

    function updatePlayheadPosition() {
        if (!playheadEl) return;
        const x = state.currentTime * state.zoom;
        playheadEl.style.transform = `translateX(${x}px)`;
        if (timeline) timeline.setPlayheadTime(state.currentTime);
    }

    function updatePlayButton(playing) {
        const btn = transportEl?.querySelector('.daw-btn-play');
        if (btn) btn.innerHTML = playing ? DAW_ICONS.pause : DAW_ICONS.play;
    }

    function updateTimeDisplay() {
        if (!timeDisplayEl) return;
        const current = formatTimePrecise(state.currentTime);
        const total = formatTimePrecise(state.totalDuration);
        timeDisplayEl.textContent = `${current} / ${total}`;
        const beatsEl = transportEl?.querySelector('.daw-lcd-beats');
        if (beatsEl) {
            const beatLen = 60 / state.bpm;
            const beatsPerBar = state.timeSignature[0] || 4;
            const totalBeats = state.currentTime / beatLen;
            const bar = Math.floor(totalBeats / beatsPerBar) + 1;
            const beat = Math.floor(totalBeats % beatsPerBar) + 1;
            const sixteenth = Math.floor((totalBeats % 1) * 4) + 1;
            beatsEl.textContent = `${bar}.${beat}.${sixteenth}`;
        }
    }

    /**
     * Paint bar/beat (or seconds) grid lines onto the clip-lane area so the
     * arrangement reads against the same grid as the ruler.
     */
    function updateLaneGrid() {
        if (!clipLanesEl) return;
        let minor, major;
        if (state.rulerMode === 'beats') {
            minor = 60 / state.bpm;
            major = minor * (state.timeSignature[0] || 4);
        } else {
            if (state.zoom >= 100) { minor = 0.5; major = 5; }
            else if (state.zoom >= 50) { minor = 1; major = 5; }
            else if (state.zoom >= 20) { minor = 2; major = 10; }
            else { minor = 5; major = 30; }
        }
        const mpx = minor * state.zoom;
        const Mpx = major * state.zoom;
        const majorColor = 'color-mix(in srgb, var(--text-soft) 22%, transparent)';
        const minorColor = 'color-mix(in srgb, var(--text-soft) 9%, transparent)';
        const layers = [`repeating-linear-gradient(90deg, ${majorColor} 0 1px, transparent 1px ${Mpx}px)`];
        if (mpx >= 8) {
            layers.push(`repeating-linear-gradient(90deg, ${minorColor} 0 1px, transparent 1px ${mpx}px)`);
        }
        clipLanesEl.style.backgroundImage = layers.join(', ');
    }

    /** Translucent wash over the loop region, spanning all lanes (like the ruler overlay). */
    function updateLoopShade() {
        if (!clipLanesEl) return;
        let shade = clipLanesEl.querySelector('.daw-loop-shade');
        if (!shade) {
            shade = createDiv(null, 'daw-loop-shade');
            clipLanesEl.appendChild(shade);
        }
        const visible = state.loopEnabled && state.loopEnd > state.loopStart;
        shade.style.display = visible ? '' : 'none';
        if (visible) {
            shade.style.left = (state.loopStart * state.zoom) + 'px';
            shade.style.width = ((state.loopEnd - state.loopStart) * state.zoom) + 'px';
        }
    }

    // ===== ZOOM =====

    function setZoom(newZoom) {
        state.zoom = Math.max(10, Math.min(500, newZoom));
        if (timeline) timeline.setZoom(state.zoom);
        for (const track of state.tracks) {
            AudioDawTrack.updateZoom(track, state.zoom);
        }
        updateClipLanesWidth();
        updatePlayheadPosition();
        updateLaneGrid();
        updateLoopShade();
        // Keep the transport slider in sync (keyboard +/- also zooms)
        const slider = transportEl?.querySelector('.daw-transport-zoom');
        if (slider) slider.value = state.zoom;
    }

    // ===== IMPORT =====

    function importAudioToTrack() {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = 'audio/*';
        input.multiple = true;
        input.onchange = async () => {
            const overlay = showDawLoadingOverlay('Importing audio...');
            for (const file of input.files) {
                const ext = file.name.split('.').pop().toLowerCase();
                if (!isAudioExt('file.' + ext)) continue;
                const track = addTrack({ name: file.name.replace(/\.[^.]+$/, '') });
                await addClipToTrack(track, file, { name: file.name });
            }
            updateTotalDuration();
            renderAllTracks();
            resyncPlayback();
            hideDawLoadingOverlay(overlay);
        };
        input.click();
    }

    // ===== FLOATING MENUS =====

    /**
     * Single floating-menu implementation for all DAW dropdowns/context menus.
     * items: [{ label, action, checked?, disabled? }] — `checked` renders a ✓ column.
     * Auto-clamps to the viewport; closes on outside click. Returns the menu element.
     */
    function dawMenu(e, items) {
        const existing = document.querySelector('.daw-context-menu');
        if (existing) existing.remove();
        const menu = createDiv(null, 'daw-context-menu');
        menu.style.position = 'fixed';
        menu.style.zIndex = '99999';
        for (const item of items) {
            const btn = document.createElement('button');
            btn.className = 'daw-context-item';
            btn.textContent = (item.checked === undefined ? '' : (item.checked ? '✓ ' : '   ')) + item.label;
            if (item.disabled) btn.disabled = true;
            btn.addEventListener('click', () => {
                menu.remove();
                if (item.action) item.action();
            });
            menu.appendChild(btn);
        }
        document.body.appendChild(menu);
        const w = menu.offsetWidth, h = menu.offsetHeight;
        menu.style.left = Math.max(8, Math.min(e.clientX, window.innerWidth - w - 8)) + 'px';
        menu.style.top = Math.max(8, Math.min(e.clientY, window.innerHeight - h - 8)) + 'px';
        const closeMenu = (ce) => {
            if (!menu.contains(ce.target)) {
                menu.remove();
                document.removeEventListener('click', closeMenu, true);
            }
        };
        setTimeout(() => document.addEventListener('click', closeMenu, true), 0);
        return menu;
    }

    /** Small swatch-grid popover to recolor a track (updates header, clips, mixer). */
    function showTrackColorMenu(track, e) {
        const existing = document.querySelector('.daw-context-menu');
        if (existing) existing.remove();
        const menu = createDiv(null, 'daw-context-menu daw-color-menu');
        menu.style.position = 'fixed';
        menu.style.zIndex = '99999';
        const grid = createDiv(null, 'daw-color-grid');
        for (const color of AudioDawTrack.COLORS) {
            const b = document.createElement('button');
            b.className = 'daw-color-swatch-btn' + (track.color === color ? ' selected' : '');
            b.style.background = color;
            b.title = color;
            b.addEventListener('click', () => {
                menu.remove();
                track.color = color;
                renderAllTracks();
                updateBottomPanel();
            });
            grid.appendChild(b);
        }
        menu.appendChild(grid);
        document.body.appendChild(menu);
        const w = menu.offsetWidth, h = menu.offsetHeight;
        menu.style.left = Math.max(8, Math.min(e.clientX, window.innerWidth - w - 8)) + 'px';
        menu.style.top = Math.max(8, Math.min(e.clientY, window.innerHeight - h - 8)) + 'px';
        const closeMenu = (ce) => {
            if (!menu.contains(ce.target)) {
                menu.remove();
                document.removeEventListener('click', closeMenu, true);
            }
        };
        setTimeout(() => document.addEventListener('click', closeMenu, true), 0);
    }

    /** Pick a recent audio file from the user's Swarm output history and add it as a track. */
    async function showOutputsPicker(e) {
        try {
            const result = await AudioLabAPI.callAPI('ListImages', { path: '', depth: 3, sortBy: 'Date' });
            const files = (result.files || [])
                .map(f => f.src || f.image || f.name || f)
                .filter(f => typeof f === 'string' && typeof isAudioExt === 'function' && isAudioExt(f))
                .slice(0, 20);
            if (!files.length) {
                if (typeof doNoticePopover === 'function') doNoticePopover('No audio files in your outputs yet', 'notice-pop-yellow');
                return;
            }
            dawMenu(e, files.map(f => ({
                label: f.split('/').pop(),
                action: async () => {
                    const overlay = showDawLoadingOverlay('Loading output...');
                    try {
                        const url = f.startsWith('View/') || f.startsWith('Output/') ? '/' + f : `/View/${f}`;
                        const blob = await fetchAsBlob(url);
                        pushUndo();
                        const track = addTrack({ name: f.split('/').pop().replace(/\.[^.]+$/, '').slice(0, 20) });
                        await addClipToTrack(track, blob, { name: f.split('/').pop(), startTime: snapTime(state.currentTime) });
                        updateTotalDuration();
                        renderAllTracks();
                        updateBottomPanel();
                        resyncPlayback();
                    } catch (err) {
                        console.error('[AudioDaw] Add from outputs failed:', err);
                        if (typeof doNoticePopover === 'function') doNoticePopover('Failed to load output: ' + err.message, 'notice-pop-red');
                    }
                    hideDawLoadingOverlay(overlay);
                }
            })));
        } catch (err) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Failed to list outputs: ' + err.message, 'notice-pop-red');
        }
    }

    // ===== CLIP CONTEXT MENU =====

    function showClipContextMenu(e, clip, track) {
        dawMenu(e, [
            { label: 'Split at Playhead', action: () => doSplitClip(clip, track) },
            { label: 'Duplicate', action: () => doDuplicateClip(clip, track) },
            { label: 'Delete', action: () => doDeleteClip(clip, track) },
            { label: clip.muted ? 'Unmute Clip' : 'Mute Clip', action: () => {
                clip.muted = !clip.muted;
                applyClipGain(clip);
                renderAllTracks();
            }},
            { label: 'Separate Stems… (Demucs)', action: () => openStemsForClip(clip, track) },
            { label: `Conform to ${state.bpm} BPM…`, action: () => conformClipToBpm(clip, track) }
        ]);
    }

    // ===== CLIP OPERATIONS =====

    async function doSplitClip(clip, track) {
        // splitTime is relative to the clip's visible (trimmed) region
        const splitTime = state.currentTime - clip.startTime;
        const visibleDur = clip.duration - clip.offset - clip.trimEnd;
        if (splitTime <= 0 || splitTime >= visibleDur) {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Move playhead over clip to split', 'notice-pop-yellow');
            }
            return;
        }
        try {
            pushUndo();
            const parts = await AudioLabCore.splitAudio(clip.blob, splitTime + clip.offset);
            if (!parts) {
                console.error('[AudioDaw] splitAudio returned null');
                return;
            }

            const idx = track.clips.indexOf(clip);
            if (idx === -1) {
                console.error('[AudioDaw] clip not found in track during split');
                return;
            }

            // Create two new clips replacing the original. Part A's blob still contains the
            // trimmed head (offset carries over); part B's blob still contains the trimmed tail.
            const clipA = AudioDawTrack.createClip(parts.before, {
                name: clip.name + ' (A)',
                startTime: clip.startTime,
                color: clip.color
            });
            await AudioDawTrack.decodeClip(clipA);
            clipA.offset = clip.offset;
            clipA.gain = clip.gain;
            blobStore.set(clipA.blobKey, { blob: parts.before, decodedBuffer: clipA.decodedBuffer });

            const clipB = AudioDawTrack.createClip(parts.after, {
                name: clip.name + ' (B)',
                startTime: clip.startTime + splitTime,
                color: clip.color
            });
            await AudioDawTrack.decodeClip(clipB);
            clipB.trimEnd = clip.trimEnd;
            clipB.gain = clip.gain;
            blobStore.set(clipB.blobKey, { blob: parts.after, decodedBuffer: clipB.decodedBuffer });

            // Replace original clip
            track.clips.splice(idx, 1, clipA, clipB);

            // Clean up old clip WaveSurfer
            const oldEntry = track.clipElements.get(clip.id);
            if (oldEntry?.ws) oldEntry.ws.destroy();
            if (oldEntry?.el) oldEntry.el.remove();
            track.clipElements.delete(clip.id);

            state.selectedClipId = null;
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Clip split successfully', 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Split failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Split failed: ' + err.message, 'notice-pop-red');
            }
        }
    }

    /** "Foo" -> "Foo (copy)" -> "Foo (copy 2)" instead of stacking " (copy)" forever. */
    function nextCopyName(name) {
        const m = name.match(/^(.*) \(copy(?: (\d+))?\)$/);
        if (!m) return name + ' (copy)';
        return `${m[1]} (copy ${(parseInt(m[2]) || 1) + 1})`;
    }

    function doDuplicateClip(clip, track) {
        try {
            pushUndo();
            const newClip = AudioDawTrack.createClip(clip.blob, {
                name: nextCopyName(clip.name),
                startTime: clip.startTime + (clip.duration - clip.offset - clip.trimEnd) + 0.5,
                color: clip.color,
                blobKey: clip.blobKey // share the same blob
            });
            newClip.decodedBuffer = clip.decodedBuffer;
            newClip.duration = clip.duration;
            newClip.offset = clip.offset;
            newClip.trimEnd = clip.trimEnd;
            newClip.gain = clip.gain;
            newClip.fadeIn = clip.fadeIn;
            newClip.fadeOut = clip.fadeOut;
            track.clips.push(newClip);
            state.selectedClipId = newClip.id;
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Clip duplicated', 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Duplicate failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Duplicate failed: ' + err.message, 'notice-pop-red');
            }
        }
    }

    function doDeleteClip(clip, track) {
        try {
            pushUndo();
            const idx = track.clips.indexOf(clip);
            if (idx === -1) {
                return;
            }
            track.clips.splice(idx, 1);

            // Clean up element (blob stays in blobStore — undo snapshots may reference it;
            // garbageCollectBlobs() reclaims it once nothing does)
            const entry = track.clipElements.get(clip.id);
            if (entry?.ws) entry.ws.destroy();
            if (entry?.el) entry.el.remove();
            track.clipElements.delete(clip.id);

            if (state.selectedClipId === clip.id) state.selectedClipId = null;
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Clip deleted', 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Delete failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Delete failed: ' + err.message, 'notice-pop-red');
            }
        }
    }

    // ===== STEM SEPARATION (Demucs) =====

    /** Cache the Demucs install status so we don't poll every time the tab renders. */
    let demucsInstallStatus = null; // null = unchecked, true = installed, false = not installed

    async function checkDemucsInstalled(forceRefresh = false) {
        if (!forceRefresh && demucsInstallStatus !== null) return demucsInstallStatus;
        try {
            const result = await AudioLabAPI.callAPI('GetInstallationStatus');
            // Backend returns raw boolean per provider id
            demucsInstallStatus = (result.providers || {})['demucs_fx'] === true;
        } catch (err) {
            console.warn('[AudioDaw] Failed to check Demucs status:', err);
            demucsInstallStatus = false;
        }
        return demucsInstallStatus;
    }

    /** Install the Demucs engine via the streaming installer. Resolves true on success. */
    function installDemucs(onProgress) {
        return new Promise(resolve => {
            makeWSRequest('AudioLabInstallEngine', { provider_id: 'demucs_fx' }, data => {
                if (data.info) {
                    if (onProgress) onProgress(data.info);
                }
                else if (data.success) {
                    demucsInstallStatus = true;
                    if (typeof doNoticePopover === 'function') doNoticePopover('Demucs installed', 'notice-pop-green');
                    resolve(true);
                }
                else if (data.error) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Demucs install failed: ' + data.error, 'notice-pop-red');
                    resolve(false);
                }
            }, 0, e => {
                if (typeof doNoticePopover === 'function') doNoticePopover('Demucs install failed: ' + e, 'notice-pop-red');
                resolve(false);
            });
        });
    }

    /**
     * Separate a clip into stems using Demucs via the backend.
     * Creates new tracks for each stem at the same position as the original clip.
     * @param {Object} clip - The clip to separate
     * @param {Object} track - The track containing the clip
     * @param {string} [modelName='htdemucs'] - Demucs model
     */
    /**
     * Inline non-blocking busy indicator: indeterminate bar + label + elapsed
     * seconds. Optionally pulses a dot on a bottom tab so progress stays visible
     * from other tabs. Call .done() when finished (safe if already removed).
     */
    function createBusyIndicator(label, tabId = null) {
        const row = createDiv(null, 'daw-busy-row');
        const bar = createDiv(null, 'daw-busy-bar');
        bar.appendChild(createDiv(null, 'daw-busy-bar-fill'));
        const text = createSpan(null, 'daw-busy-label');
        text.textContent = label;
        row.appendChild(bar);
        row.appendChild(text);
        const tabBtn = tabId ? document.querySelector(`#daw_bbar_tabs .nav-link[data-tab="${tabId}"]`) : null;
        tabBtn?.classList.add('busy');
        const started = performance.now();
        let pct = null;
        const timer = setInterval(() => {
            const secs = Math.floor((performance.now() - started) / 1000);
            text.textContent = pct === null ? `${label} ${secs}s` : `${label} ${secs}s · ${Math.round(pct * 100)}%`;
        }, 1000);
        // Switches the bar from indeterminate sweep to a real 0..1 fill
        row.setProgress = (frac) => {
            pct = Math.max(0, Math.min(1, frac));
            const fill = bar.firstChild;
            fill.style.animation = 'none';
            fill.style.left = '0';
            fill.style.width = (pct * 100) + '%';
        };
        row.done = () => {
            clearInterval(timer);
            tabBtn?.classList.remove('busy');
            row.remove();
        };
        return row;
    }

    function showDawLoadingOverlay(message = 'Processing...') {
        const body = document.getElementById('daw_container');
        if (!body) return null;
        const overlay = createDiv(null, 'daw-loading-overlay');
        overlay.innerHTML = `
            <div class="daw-loading-content">
                <div class="loading-spinner-parent">
                    <div class="loading-spinner">
                        <div class="loadspin1"></div>
                        <div class="loadspin2"></div>
                        <div class="loadspin3"></div>
                    </div>
                </div>
                <div class="daw-loading-text">${escapeHtml(message)}</div>
            </div>`;
        body.appendChild(overlay);
        if (typeof uiImprover !== 'undefined') {
            uiImprover.runLoadSpinner(overlay.querySelector('.loading-spinner-parent'));
        }
        return overlay;
    }

    function hideDawLoadingOverlay(overlay) {
        if (overlay && overlay.parentElement) {
            overlay.remove();
        }
    }

    async function doSeparateStems(clip, track, options = null) {
        if (!clip || !clip.blob) {
            if (typeof doNoticePopover === 'function') doNoticePopover('No clip to separate', 'notice-pop-yellow');
            return;
        }

        const modelName = options?.modelName || 'htdemucs';
        let outputs = options?.outputs || null; // [{name, parts:[stem...]}]; null = full split of whatever comes back

        if (stemsSeparating) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Stem separation already running', 'notice-pop-yellow');
            return;
        }
        stemsSeparating = true;
        // Non-blocking: progress lives in the Stems panel + a pulse on its tab
        const busy = createBusyIndicator('Separating stems…', 'stems');
        bottomPanelEl?.querySelector('.daw-bottom-tab-content[data-tab="stems"]')?.appendChild(busy);

        try {
            const base64 = await AudioLabCore.readAsBase64(clip.blob);
            const result = await AudioLabAPI.callAPI('ProcessAudio', {
                provider_id: 'demucs_fx',
                args: {
                    audio_data: base64,
                    model_name: modelName
                }
            });

            if (!result.success || !result.stems) {
                throw new Error(result.error || 'Stem separation failed');
            }

            const available = result.metadata?.stem_names || Object.keys(result.stems);
            if (!outputs) outputs = available.map(s => ({ name: s, parts: [s] }));

            // Materialize each requested output track — single-part outputs pass through, multi-part outputs
            // (e.g. instrumental) are summed client-side from the one separation pass. Done while the overlay is up.
            const built = [];
            for (const out of outputs) {
                const parts = out.parts.filter(p => result.stems[p]);
                if (!parts.length) continue;
                let blob;
                if (parts.length === 1) {
                    blob = AudioLabCore.base64ToBlob(result.stems[parts[0]], 'audio/wav');
                } else {
                    const buffers = [];
                    for (const p of parts) {
                        buffers.push(await AudioLabCore.decodeToBuffer(AudioLabCore.base64ToBlob(result.stems[p], 'audio/wav')));
                    }
                    blob = sumBuffersToWav(buffers);
                }
                built.push({ name: out.name, blob });
            }

            if (!built.length) throw new Error('No stems were produced for the chosen output');

            pushUndo();

            for (const b of built) {
                const newTrack = addTrack({
                    name: `${capStem(b.name)} — ${clip.name}`,
                    color: STEM_COLORS[b.name] || undefined
                });
                await addClipToTrack(newTrack, b.blob, {
                    name: b.name,
                    startTime: clip.startTime
                });
            }

            // Mute the original clip so the new stem tracks are heard instead
            clip.muted = true;

            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();

            if (typeof doNoticePopover === 'function') {
                doNoticePopover(`Separated into ${built.length} track${built.length > 1 ? 's' : ''}`, 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Stem separation failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Stem separation failed: ' + err.message, 'notice-pop-red');
            }
        } finally {
            busy.done();
            stemsSeparating = false;
        }
    }

    // ===== RECORDING =====
    // DAW-local mic capture via AudioLabPlayer.createRecorder (WaveSurfer.Record).
    // Deliberately NOT AudioLabCore.startRecording — that path is tuned for STT
    // voice references (16kHz mono, echo cancellation, 30s cap).

    function updateRecordButton(active) {
        const btn = transportEl?.querySelector('.daw-btn-rec');
        if (btn) btn.classList.toggle('active', active);
    }

    async function showMicSettingsMenu(e) {
        const voiceItem = {
            label: 'Voice mode (echo + noise reduction)',
            checked: recordSettings.voiceMode,
            action: () => { recordSettings.voiceMode = !recordSettings.voiceMode; }
        };
        // mediaDevices is absent on insecure (non-HTTPS, non-localhost) origins
        if (!navigator.mediaDevices?.enumerateDevices) {
            dawMenu(e, [
                { label: 'Microphone requires HTTPS (or localhost)', disabled: true, action: () => {} },
                voiceItem
            ]);
            return;
        }
        let devices = [];
        try {
            devices = (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audioinput');
        } catch (_) {}
        // Until mic permission is granted, browsers hide device labels (and may
        // collapse the list to one placeholder entry) — offer an explicit grant.
        const labelsHidden = !devices.length || devices.every(d => !d.label);
        let permState = 'prompt';
        try {
            permState = (await navigator.permissions.query({ name: 'microphone' })).state;
        } catch (_) {} // permissions.query('microphone') is not universal (e.g. Firefox)
        const items = [];
        if (labelsHidden && permState === 'denied') {
            items.push({ label: 'Mic blocked — allow it in browser site settings', disabled: true, action: () => {} });
        } else if (labelsHidden) {
            // No permission yet: show nothing but the grant action — device names
            // are meaningless placeholders until the browser unlocks them.
            items.push({
                label: 'Allow mic access to list devices…',
                action: async () => {
                    try {
                        // One-shot grant: open the mic just to unlock labels, then release it
                        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                        stream.getTracks().forEach(t => t.stop());
                        showMicSettingsMenu(e);
                    } catch (err) {
                        console.error('[AudioDaw] Mic permission request failed:', err);
                        if (typeof doNoticePopover === 'function') {
                            doNoticePopover('Microphone access denied: ' + err.message, 'notice-pop-red');
                        }
                    }
                }
            });
        } else {
            items.push({ label: 'System default microphone', checked: !recordSettings.deviceId,
                action: () => { recordSettings.deviceId = null; } });
            devices.forEach((d, i) => items.push({
                label: d.label || `Microphone ${i + 1}`,
                checked: recordSettings.deviceId === d.deviceId,
                action: () => { recordSettings.deviceId = d.deviceId; }
            }));
        }
        items.push(voiceItem);
        dawMenu(e, items);
    }

    async function startRecordingFlow() {
        if (recording) return;
        let track = state.tracks.find(t => t.armed);
        if (!track) {
            track = addTrack({ name: 'Recording' });
            track.armed = true;
            renderAllTracks();
        }

        // DAW-quality constraints; voice mode re-enables browser voice processing
        const constraints = {
            channelCount: { ideal: 2 },
            sampleRate: { ideal: 48000 },
            echoCancellation: recordSettings.voiceMode,
            noiseSuppression: recordSettings.voiceMode,
            autoGainControl: recordSettings.voiceMode
        };
        if (recordSettings.deviceId) constraints.deviceId = { exact: recordSettings.deviceId };

        // Placeholder clip hosting the live waveform; width grows with the playhead
        const startP = state.currentTime;
        const placeholderEl = createDiv(null, 'daw-clip daw-clip-recording');
        placeholderEl.style.left = (startP * state.zoom) + 'px';
        placeholderEl.style.width = '4px';
        const waveEl = createDiv(null, 'daw-clip-waveform');
        placeholderEl.appendChild(waveEl);
        if (track.laneEl) track.laneEl.appendChild(placeholderEl);

        const recorder = AudioLabPlayer.createRecorder(waveEl, {
            height: track.height - 8,
            continuousWaveform: true,
            renderRecorded: false,
            audioBitsPerSecond: 256000
        });
        if (!recorder) {
            placeholderEl.remove();
            if (typeof doNoticePopover === 'function') doNoticePopover('Recorder unavailable', 'notice-pop-red');
            return;
        }
        recorder.on('end', (blob) => finalizeRecording(blob));

        // Acquire the mic BEFORE touching the transport so a denied/slow permission
        // prompt can't leave playback running with no capture.
        try {
            await recorder.startRecording(constraints);
        } catch (err) {
            recorder.destroy();
            placeholderEl.remove();
            console.error('[AudioDaw] Mic access failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Microphone access failed: ' + err.message, 'notice-pop-red');
            }
            return;
        }

        recording = { track, startP, recorder, placeholderEl };
        state.isRecording = true;
        updateRecordButton(true);
        if (!state.isPlaying) startPlayback(); // hear the other tracks while recording
    }

    async function stopRecordingFlow() {
        if (!recording) return;
        const rec = recording;
        state.isRecording = false;
        updateRecordButton(false);
        try { await rec.recorder.stopRecording(); } catch (_) {} // 'end' event finalizes
        stopPlayback();
        // Safety net: if 'record-end' never fires, clear the session so playback
        // doesn't stay stuck in record mode
        setTimeout(() => {
            if (recording === rec) {
                console.warn('[AudioDaw] record-end never fired; aborting recording session');
                abortRecording();
            }
        }, 2000);
    }

    /** 'record-end' handler: turn the captured blob into a real clip at the record start. */
    async function finalizeRecording(blob) {
        const rec = recording;
        if (!rec) return;
        recording = null;
        try { rec.recorder.destroy(); } catch (_) {}
        rec.placeholderEl.remove();
        if (!blob || blob.size === 0) return;
        pushUndo();
        try {
            await addClipToTrack(rec.track, blob, { name: 'Recording', startTime: rec.startP });
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') doNoticePopover('Recording added', 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Failed to add recording:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Failed to add recording: ' + err.message, 'notice-pop-red');
            }
        }
    }

    /** Abort an in-flight recording without keeping audio (modal teardown). */
    function abortRecording() {
        if (!recording) return;
        const rec = recording;
        recording = null; // null first so the 'end' handler no-ops
        try { rec.recorder.stopRecording(); } catch (_) {}
        try { rec.recorder.destroy(); } catch (_) {}
        rec.placeholderEl.remove();
        if (state) state.isRecording = false;
        updateRecordButton(false);
    }

    // ===== UNDO / REDO =====

    /**
     * Drop blobStore entries referenced by no live clip AND no undo/redo snapshot.
     * Deleting blobs eagerly on clip/track delete would make undo restore silence.
     */
    function garbageCollectBlobs() {
        const referenced = new Set();
        for (const track of state.tracks) {
            for (const clip of track.clips) referenced.add(clip.blobKey);
        }
        for (const snap of [...state.undoStack, ...state.redoStack]) {
            for (const ts of snap.tracks) {
                for (const cs of ts.clips) referenced.add(cs.blobKey);
            }
        }
        for (const key of [...blobStore.keys()]) {
            if (!referenced.has(key)) blobStore.delete(key);
        }
    }

    function pushUndo() {
        const snapshot = {
            tracks: state.tracks.map(t => AudioDawTrack.serializeTrack(t)),
            selectedTrackId: state.selectedTrackId,
            selectedClipId: state.selectedClipId,
            masterVolume: state.masterVolume,
            bpm: state.bpm
        };
        state.undoStack.push(snapshot);
        if (state.undoStack.length > MAX_UNDO) state.undoStack.shift();
        state.redoStack = []; // clear redo on new action
        garbageCollectBlobs();
        scheduleAutosave(); // every undoable mutation marks the session dirty
    }

    async function doUndo() {
        if (state.undoStack.length === 0) return;
        // Save current state to redo
        const currentSnapshot = {
            tracks: state.tracks.map(t => AudioDawTrack.serializeTrack(t)),
            selectedTrackId: state.selectedTrackId,
            selectedClipId: state.selectedClipId,
            masterVolume: state.masterVolume,
            bpm: state.bpm
        };
        state.redoStack.push(currentSnapshot);

        const snapshot = state.undoStack.pop();
        await restoreSnapshot(snapshot);
        garbageCollectBlobs();
    }

    async function doRedo() {
        if (state.redoStack.length === 0) return;
        const currentSnapshot = {
            tracks: state.tracks.map(t => AudioDawTrack.serializeTrack(t)),
            selectedTrackId: state.selectedTrackId,
            selectedClipId: state.selectedClipId,
            masterVolume: state.masterVolume,
            bpm: state.bpm
        };
        state.undoStack.push(currentSnapshot);

        const snapshot = state.redoStack.pop();
        await restoreSnapshot(snapshot);
        garbageCollectBlobs();
    }

    async function restoreSnapshot(snapshot) {
        // Destroy existing tracks
        for (const track of state.tracks) {
            AudioDawTrack.destroyTrack(track);
        }

        // Restore state
        state.selectedTrackId = snapshot.selectedTrackId;
        state.selectedClipId = snapshot.selectedClipId;
        state.masterVolume = snapshot.masterVolume;
        state.bpm = snapshot.bpm;
        if (bpmInputEl) bpmInputEl.value = state.bpm;

        // Recreate tracks from snapshot
        state.tracks = [];
        for (const ts of snapshot.tracks) {
            const track = AudioDawTrack.createTrack({
                name: ts.name,
                color: ts.color,
                height: ts.height
            });
            // Overwrite generated fields with snapshot data
            track.id = ts.id;
            track.volume = ts.volume;
            track.pan = ts.pan;
            track.muted = ts.muted;
            track.soloed = ts.soloed;
            track.armed = ts.armed;
            track.fx = (ts.fx || []).filter(f => typeof AudioDawFx === 'undefined' || AudioDawFx.FX_DEFS[f.type])
                .map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } }));
            track.automation = {
                volume: (ts.automation?.volume || []).map(p => ({ ...p })),
                pan: (ts.automation?.pan || []).map(p => ({ ...p }))
            };
            track.automationVisible = !!ts.automationVisible;
            track.automationParam = ts.automationParam || 'volume';

            for (const cs of ts.clips) {
                const stored = blobStore.get(cs.blobKey);
                if (!stored) continue;
                const clip = AudioDawTrack.createClip(stored.blob, {
                    name: cs.name,
                    startTime: cs.startTime,
                    color: cs.color,
                    blobKey: cs.blobKey
                });
                clip.id = cs.id;
                clip.duration = cs.duration;
                clip.offset = cs.offset;
                clip.trimEnd = cs.trimEnd;
                clip.gain = cs.gain;
                clip.fadeIn = cs.fadeIn || 0;
                clip.fadeOut = cs.fadeOut || 0;
                clip.muted = cs.muted;
                clip.decodedBuffer = stored.decodedBuffer;
                track.clips.push(clip);
            }
            state.tracks.push(track);
        }

        updateTotalDuration();
        renderAllTracks();
        resyncPlayback();
    }

    /**
     * Estimate tempo from an AudioBuffer: onset-energy autocorrelation over 60-200 BPM.
     * Good enough to prefill the conform dialog ("drop a sample and it just fits").
     */
    function detectBpm(buffer) {
        try {
            const ch = buffer.getChannelData(0);
            const hop = 512;
            const frames = Math.floor(ch.length / hop);
            if (frames < 64) return null;
            const energy = new Float32Array(frames);
            for (let i = 0; i < frames; i++) {
                let s = 0;
                const base = i * hop;
                for (let j = 0; j < hop; j++) s += ch[base + j] * ch[base + j];
                energy[i] = s;
            }
            const nov = new Float32Array(frames);
            for (let i = 1; i < frames; i++) nov[i] = Math.max(0, energy[i] - energy[i - 1]);
            const fps = buffer.sampleRate / hop;
            let best = 0, bestBpm = null;
            for (let bpm = 60; bpm <= 200; bpm += 0.5) {
                const lag = Math.round(fps * 60 / bpm);
                if (lag < 1 || lag >= frames) continue;
                let corr = 0;
                for (let i = 0; i + lag < frames; i++) corr += nov[i] * nov[i + lag];
                corr /= (frames - lag);
                if (corr > best) { best = corr; bestBpm = bpm; }
            }
            return bestBpm;
        } catch (_) { return null; }
    }

    /**
     * Tempo-match a clip to the project BPM via the server's ffmpeg time-stretch.
     * Source BPM is auto-detected (onset autocorrelation) and confirmable; pitch is preserved.
     */
    async function conformClipToBpm(clip, track) {
        const guess = clip.decodedBuffer ? detectBpm(clip.decodedBuffer) : null;
        const src = prompt(
            `Source BPM of "${clip.name}"? It will be stretched to ${state.bpm} BPM (pitch preserved).` +
            (guess ? `\n\nDetected: ~${guess} BPM` : ''),
            guess || state.bpm);
        if (!src) return;
        const srcBpm = parseFloat(src);
        if (!srcBpm || srcBpm < 20 || srcBpm > 400) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Enter a BPM between 20 and 400', 'notice-pop-yellow');
            return;
        }
        const rate = state.bpm / srcBpm;
        if (Math.abs(rate - 1) < 0.001) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Clip is already at project tempo', 'notice-pop-green');
            return;
        }
        const overlay = showDawLoadingOverlay(`Stretching ${srcBpm} → ${state.bpm} BPM...`);
        try {
            const base64 = await AudioLabCore.readAsBase64(clip.blob);
            const result = await AudioLabAPI.callAPI('AudioLabTimeStretch', { audio_data: base64, rate });
            if (!result.success || !result.audio_data) throw new Error(result.error || 'Stretch returned no audio');
            const blob = AudioLabCore.base64ToBlob(result.audio_data, 'audio/wav');
            pushUndo();
            const newClip = AudioDawTrack.createClip(blob, {
                name: clip.name + ` @${state.bpm}bpm`,
                startTime: clip.startTime,
                color: clip.color
            });
            await AudioDawTrack.decodeClip(newClip);
            blobStore.set(newClip.blobKey, { blob, decodedBuffer: newClip.decodedBuffer });
            newClip.gain = clip.gain;
            const idx = track.clips.indexOf(clip);
            if (idx >= 0) track.clips.splice(idx, 1, newClip);
            const entry = track.clipElements.get(clip.id);
            if (entry?.ws) entry.ws.destroy();
            if (entry?.el) entry.el.remove();
            track.clipElements.delete(clip.id);
            state.selectedClipId = newClip.id;
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') doNoticePopover('Clip conformed to project tempo', 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Conform failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover('Tempo conform failed: ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    // ===== BEAT SEQUENCER =====
    // Sample-based 16/32-step grid. Pads are one-shots (generated via audiogen_sfx,
    // imported, or taken from a clip); patterns render to BPM-locked clips on tracks.

    let beatAudition = null; // live looping source while auditioning

    function stopBeatAudition() {
        if (!beatAudition) return;
        try { beatAudition.stop(); } catch (_) {}
        try { beatAudition.disconnect(); } catch (_) {}
        beatAudition = null;
        const btn = bottomPanelEl?.querySelector('.daw-beats-audition');
        if (btn) btn.textContent = '\u25B6 Audition';
    }

    async function ensurePadBuffer(lane) {
        const stored = blobStore.get(lane.blobKey);
        if (!stored) return null;
        if (!stored.decodedBuffer) {
            stored.decodedBuffer = await AudioLabCore.decodeToBuffer(stored.blob);
        }
        return stored.decodedBuffer;
    }

    /** Render the pattern offline. Length = repeats x pattern + 1s tail, BPM-locked by construction. */
    async function renderBeatPatternBuffer(repeats = 1) {
        const p = state.beatPattern;
        const stepDur = 60 / state.bpm / 4; // 16th notes
        const patternDur = stepDur * p.steps;
        const sr = 44100;
        const ctx = new OfflineAudioContext(2, Math.ceil((patternDur * repeats + 1) * sr), sr);
        for (const lane of p.lanes) {
            const buffer = await ensurePadBuffer(lane);
            if (!buffer) continue;
            const gain = ctx.createGain();
            gain.gain.value = lane.gain ?? 1;
            gain.connect(ctx.destination);
            for (let r = 0; r < repeats; r++) {
                for (let i = 0; i < p.steps; i++) {
                    if (!lane.steps[i]) continue;
                    const swing = (i % 2 === 1) ? stepDur * (p.swing || 0) : 0;
                    const src = ctx.createBufferSource();
                    src.buffer = buffer;
                    src.connect(gain);
                    src.start(r * patternDur + i * stepDur + swing);
                }
            }
        }
        return await ctx.startRendering();
    }

    function patternHasHits() {
        return state.beatPattern.lanes.some(l => l.steps.some(Boolean));
    }

    async function toggleBeatAudition(btn) {
        if (beatAudition) { stopBeatAudition(); return; }
        if (!patternHasHits()) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Program some steps first', 'notice-pop-yellow');
            return;
        }
        btn.textContent = 'Rendering…';
        try {
            const buffer = await renderBeatPatternBuffer(1);
            const ctx = getAudioContext();
            if (ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
            const src = ctx.createBufferSource();
            src.buffer = buffer;
            src.loop = true;
            src.loopStart = 0;
            src.loopEnd = (60 / state.bpm / 4) * state.beatPattern.steps;
            src.connect(ctx.destination);
            src.start();
            beatAudition = src;
            btn.textContent = '\u25A0 Stop';
        } catch (err) {
            btn.textContent = '\u25B6 Audition';
            console.error('[AudioDaw] Beat audition failed:', err);
        }
    }

    async function renderBeatsToTrack(repeats) {
        if (!patternHasHits()) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Program some steps first', 'notice-pop-yellow');
            return;
        }
        stopBeatAudition();
        const overlay = showDawLoadingOverlay('Rendering beat...');
        try {
            const buffer = await renderBeatPatternBuffer(repeats);
            const blob = audioBufferToWav(buffer);
            pushUndo();
            const track = addTrack({ name: `Beat ${state.bpm}bpm` });
            await addClipToTrack(track, blob, { name: `beat-${state.bpm}bpm-x${repeats}`, startTime: snapTime(state.currentTime) });
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
            resyncPlayback();
            if (typeof doNoticePopover === 'function') doNoticePopover('Beat rendered to a new track', 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Beat render failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover('Beat render failed: ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    function newBeatLane(name, blobKey) {
        return { name, blobKey, gain: 1, steps: Array(state.beatPattern.steps).fill(false) };
    }

    async function addBeatPadBlob(name, blob, container) {
        const blobKey = `beatpad-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
        blobStore.set(blobKey, { blob, decodedBuffer: await AudioLabCore.decodeToBuffer(blob) });
        state.beatPattern.lanes.push(newBeatLane(name, blobKey));
        renderBeatsPanel(container);
        scheduleAutosave();
    }

    // Instrument browser: the drum machine ships today; the rest are planned
    // slots (clicking one explains what's coming). New instruments plug in by
    // flipping ready:true and rendering their UI in renderBeatsPanel.
    const DAW_INSTRUMENTS = [
        { id: 'drums', name: 'Drum Machine', desc: '16/32-step sample sequencer', ready: true },
        { id: 'keys', name: 'Piano / Keys', desc: 'Piano roll — coming soon', ready: false },
        { id: 'bass', name: 'Bass', desc: 'Coming soon', ready: false },
        { id: 'synth', name: 'Synth', desc: 'Coming soon', ready: false }
    ];
    let activeInstrument = 'drums';

    /** Instruments tab UI. Rebuilt only on structural changes (lanes add/remove, steps count). */
    function renderBeatsPanel(container) {
        container.innerHTML = '';
        stopBeatAudition();
        const p = state.beatPattern;
        const panel = createDiv(null, 'daw-beats-panel');
        container.appendChild(panel);

        // Instrument picker cards (FX-browser language)
        const browser = createDiv(null, 'daw-fx-browser daw-inst-browser');
        for (const inst of DAW_INSTRUMENTS) {
            const pick = document.createElement('button');
            pick.className = 'daw-fx-pick daw-inst-pick'
                + (inst.id === activeInstrument ? ' selected' : '')
                + (inst.ready ? '' : ' not-ready');
            pick.innerHTML = `<span class="daw-fx-pick-name">${inst.name}</span>`
                + `<span class="daw-fx-pick-desc">${inst.desc}</span>`;
            pick.addEventListener('click', () => {
                if (!inst.ready) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(`${inst.name} is on the roadmap — the Drum Machine is ready today`, 'notice-pop-yellow');
                    return;
                }
                activeInstrument = inst.id;
                renderBeatsPanel(container);
            });
            browser.appendChild(pick);
        }
        panel.appendChild(browser);

        // Selected instrument UI (drum machine is the only one wired so far)
        const instCard = createDiv(null, 'daw-fx-card daw-inst-card');
        panel.appendChild(instCard);

        // Header controls
        const head = createDiv(null, 'daw-stems-model-row');
        const stepsSel = document.createElement('select');
        stepsSel.className = 'daw-fx-select';
        for (const n of [16, 32]) {
            const opt = document.createElement('option');
            opt.value = n; opt.textContent = `${n} steps`;
            stepsSel.appendChild(opt);
        }
        stepsSel.value = p.steps;
        stepsSel.addEventListener('change', () => {
            const n = parseInt(stepsSel.value);
            p.steps = n;
            for (const lane of p.lanes) {
                lane.steps = Array.from({ length: n }, (_, i) => !!lane.steps[i]);
            }
            renderBeatsPanel(container);
        });
        head.appendChild(stepsSel);

        const swingLbl = createSpan(null, 'daw-fx-knob-label');
        swingLbl.textContent = 'Swing';
        const swing = document.createElement('input');
        swing.type = 'range';
        swing.min = '0'; swing.max = '0.6'; swing.step = '0.05';
        swing.value = p.swing || 0;
        swing.className = 'daw-beats-swing';
        swing.addEventListener('input', () => { p.swing = parseFloat(swing.value); });
        head.appendChild(swingLbl);
        head.appendChild(swing);

        const auditionBtn = document.createElement('button');
        auditionBtn.className = 'basic-button btn-sm daw-beats-audition';
        auditionBtn.textContent = '\u25B6 Audition';
        auditionBtn.addEventListener('click', () => toggleBeatAudition(auditionBtn));
        head.appendChild(auditionBtn);

        const repeatsSel = document.createElement('select');
        repeatsSel.className = 'daw-fx-select';
        for (const n of [1, 2, 4, 8]) {
            const opt = document.createElement('option');
            opt.value = n; opt.textContent = `\u00D7${n}`;
            repeatsSel.appendChild(opt);
        }
        repeatsSel.value = '4';
        repeatsSel.title = 'Pattern repeats to render';
        head.appendChild(repeatsSel);

        const renderBtn = document.createElement('button');
        renderBtn.className = 'basic-button btn-sm btn-primary daw-stems-go';
        renderBtn.textContent = 'Render to Track';
        renderBtn.addEventListener('click', () => renderBeatsToTrack(parseInt(repeatsSel.value)));
        head.appendChild(renderBtn);
        instCard.appendChild(head);

        // Lane grid
        const grid = createDiv(null, 'daw-beats-grid');
        p.lanes.forEach((lane, li) => {
            const row = createDiv(null, 'daw-beats-lane');
            const name = createSpan(null, 'daw-beats-lane-name');
            name.textContent = lane.name;
            name.title = lane.name;
            row.appendChild(name);
            const prev = document.createElement('button');
            prev.className = 'daw-fx-mini-btn';
            prev.innerHTML = '&#x25B6;';
            prev.title = 'Preview pad';
            prev.addEventListener('click', async () => {
                const buffer = await ensurePadBuffer(lane);
                if (!buffer) return;
                const ctx = getAudioContext();
                if (ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
                const s = ctx.createBufferSource();
                s.buffer = buffer;
                s.connect(ctx.destination);
                s.start();
            });
            row.appendChild(prev);
            const gainSl = document.createElement('input');
            gainSl.type = 'range';
            gainSl.min = '0'; gainSl.max = '1.5'; gainSl.step = '0.05';
            gainSl.value = lane.gain ?? 1;
            gainSl.className = 'daw-beats-lane-gain';
            gainSl.title = 'Pad gain';
            gainSl.addEventListener('input', () => { lane.gain = parseFloat(gainSl.value); });
            row.appendChild(gainSl);
            const del = document.createElement('button');
            del.className = 'daw-fx-mini-btn';
            del.innerHTML = '&#x2715;';
            del.title = 'Remove lane';
            del.addEventListener('click', () => {
                p.lanes.splice(li, 1);
                renderBeatsPanel(container);
            });
            row.appendChild(del);
            const stepsWrap = createDiv(null, 'daw-beats-steps');
            for (let i = 0; i < p.steps; i++) {
                const cell = document.createElement('button');
                cell.className = 'daw-beats-step' + (lane.steps[i] ? ' on' : '') + (i % 4 === 0 ? ' beat-head' : '');
                cell.addEventListener('click', () => {
                    lane.steps[i] = !lane.steps[i];
                    cell.classList.toggle('on', lane.steps[i]);
                    scheduleAutosave();
                });
                stepsWrap.appendChild(cell);
            }
            row.appendChild(stepsWrap);
            grid.appendChild(row);
        });
        if (!p.lanes.length) {
            const empty = createDiv(null, 'daw-stems-desc');
            empty.textContent = 'No pads yet — describe a one-shot below and hit Generate Pad, or import a sample.';
            instCard.appendChild(empty);
        }
        instCard.appendChild(grid);

        // Add-pad row
        const addRow = createDiv(null, 'daw-stems-action-row');
        const promptInput = document.createElement('input');
        promptInput.type = 'text';
        promptInput.className = 'daw-generate-reftext';
        promptInput.placeholder = 'Describe a one-shot: "punchy kick drum", "tight snare", "closed hi-hat"...';
        addRow.appendChild(promptInput);
        const genBtn = document.createElement('button');
        genBtn.className = 'basic-button btn-sm';
        genBtn.textContent = 'Generate Pad';
        genBtn.addEventListener('click', async () => {
            const prompt = promptInput.value.trim();
            if (!prompt) {
                if (typeof doNoticePopover === 'function') doNoticePopover('Describe the sound first', 'notice-pop-yellow');
                return;
            }
            genBtn.disabled = true;
            genBtn.textContent = 'Generating…';
            try {
                await ensureEnginesList().catch(() => {});
                const model = dawSwarmModelFor('audiogen_sfx');
                if (!model) throw new Error('SFX engine (AudioGen) is not installed');
                const { blob } = await dawSwarmGenerate({
                    model, prompt,
                    params: { text2audioduration: 1, seed: Math.floor(Math.random() * 1e9) }
                });
                await addBeatPadBlob(prompt.slice(0, 18), blob, container);
            } catch (err) {
                console.error('[AudioDaw] Pad generation failed:', err);
                if (typeof doNoticePopover === 'function') doNoticePopover('Pad generation failed: ' + err.message, 'notice-pop-red');
                genBtn.disabled = false;
                genBtn.textContent = 'Generate Pad';
            }
        });
        addRow.appendChild(genBtn);
        const importBtn = document.createElement('button');
        importBtn.className = 'basic-button btn-sm';
        importBtn.textContent = 'Import Pad';
        importBtn.addEventListener('click', () => {
            const inp = document.createElement('input');
            inp.type = 'file';
            inp.accept = 'audio/*';
            inp.onchange = async () => {
                if (inp.files[0]) await addBeatPadBlob(inp.files[0].name.replace(/\.[^.]+$/, ''), inp.files[0], container);
            };
            inp.click();
        });
        addRow.appendChild(importBtn);
        const clipBtn = document.createElement('button');
        clipBtn.className = 'basic-button btn-sm';
        clipBtn.textContent = 'From Selected Clip';
        clipBtn.addEventListener('click', () => {
            const sel = findClipById(state.selectedClipId);
            if (!sel) {
                if (typeof doNoticePopover === 'function') doNoticePopover('Select a clip first', 'notice-pop-yellow');
                return;
            }
            state.beatPattern.lanes.push(newBeatLane(sel.clip.name.slice(0, 18), sel.clip.blobKey));
            renderBeatsPanel(container);
        });
        addRow.appendChild(clipBtn);
        instCard.appendChild(addRow);
    }

    // ===== SOUND PALETTE =====
    // Floating dock: prompt -> N candidate sounds -> audition -> add to track / beat pad / variation.
    // Results are session-only until added (then their blob enters blobStore).

    let paletteEl = null;
    let paletteResults = []; // [{blob, url, prompt, seed, type}]

    function togglePalette(btn) {
        if (paletteEl) {
            paletteEl.remove();
            paletteEl = null;
            btn?.classList.remove('active');
            return;
        }
        btn?.classList.add('active');
        paletteEl = createDiv(null, 'daw-palette');
        const body = document.getElementById('daw_container');
        (body || document.body).appendChild(paletteEl);
        renderPalette();
    }

    function paletteEngineFor(type) {
        if (type === 'sfx') return { engineId: 'audiogen_sfx', params: (dur, seed) => ({ text2audioduration: dur, seed }) };
        // loops/music: ACE-Step with tempo/time-signature hints from the transport
        return {
            engineId: 'acestep_music',
            params: (dur, seed) => ({
                text2audioduration: dur, seed,
                text2audiobpm: state.bpm,
                text2audiotimesignature: state.timeSignature.join('/')
            })
        };
    }

    async function paletteGenerate(prompt, type, duration, count) {
        const eng = paletteEngineFor(type);
        try { await ensureEnginesList(); } catch (_) {}
        const model = dawSwarmModelFor(eng.engineId);
        if (!model) {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(`No installed engine for ${type === 'sfx' ? 'SFX (AudioGen)' : 'music (ACE-Step)'}`, 'notice-pop-yellow');
            }
            return [];
        }
        // Each candidate is a normal Swarm generation — saved to outputs + history
        const jobs = [];
        for (let i = 0; i < count; i++) {
            const seed = Math.floor(Math.random() * 1e9);
            jobs.push(dawSwarmGenerate({ model, prompt, params: eng.params(duration, seed) })
                .then(({ blob }) => ({ blob, seed }))
                .catch(err => { console.error('[AudioDaw] Palette generation failed:', err); return null; }));
        }
        const done = (await Promise.all(jobs)).filter(Boolean);
        return done.map(({ blob, seed }) => ({ blob, url: URL.createObjectURL(blob), prompt, seed, type }));
    }

    function renderPalette() {
        if (!paletteEl) return;
        paletteEl.innerHTML = '';
        const head = createDiv(null, 'daw-palette-head');
        head.innerHTML = '<strong>Sound Palette</strong>';
        const closeBtn = document.createElement('button');
        closeBtn.className = 'daw-fx-mini-btn';
        closeBtn.innerHTML = '&#x2715;';
        closeBtn.addEventListener('click', () => togglePalette(transportEl?.querySelector('.daw-btn-palette')));
        head.appendChild(closeBtn);
        paletteEl.appendChild(head);

        const promptInput = document.createElement('textarea');
        promptInput.className = 'daw-generate-text';
        promptInput.rows = 2;
        promptInput.placeholder = 'Describe a sound: "rain on a tin roof", "808 bass loop, dark trap"...';
        paletteEl.appendChild(promptInput);

        const optsRow = createDiv(null, 'daw-palette-opts');
        const typeSel = document.createElement('select');
        typeSel.className = 'daw-fx-select';
        for (const [v, l] of [['sfx', 'SFX'], ['loop', 'Loop / Music']]) {
            const opt = document.createElement('option');
            opt.value = v; opt.textContent = l;
            typeSel.appendChild(opt);
        }
        const durSel = document.createElement('select');
        durSel.className = 'daw-fx-select';
        for (const d of [1, 2, 4, 8, 16]) {
            const opt = document.createElement('option');
            opt.value = d; opt.textContent = d + 's';
            durSel.appendChild(opt);
        }
        durSel.value = '4';
        const countSel = document.createElement('select');
        countSel.className = 'daw-fx-select';
        for (const c of [1, 2, 3]) {
            const opt = document.createElement('option');
            opt.value = c; opt.textContent = '\u00D7' + c;
            countSel.appendChild(opt);
        }
        countSel.value = '2';
        const goBtn = document.createElement('button');
        goBtn.className = 'basic-button btn-sm btn-primary';
        goBtn.textContent = 'Generate';
        goBtn.addEventListener('click', async () => {
            const prompt = promptInput.value.trim();
            if (!prompt) return;
            goBtn.disabled = true;
            goBtn.textContent = 'Generating…';
            try {
                const results = await paletteGenerate(prompt, typeSel.value, parseInt(durSel.value), parseInt(countSel.value));
                if (!results.length && typeof doNoticePopover === 'function') {
                    doNoticePopover('Generation returned nothing — check the engine is installed', 'notice-pop-red');
                }
                paletteResults.unshift(...results);
                paletteResults = paletteResults.slice(0, 12);
                renderPalette();
                const pi = paletteEl.querySelector('.daw-generate-text');
                if (pi) pi.value = prompt;
            } finally {
                goBtn.disabled = false;
                goBtn.textContent = 'Generate';
            }
        });
        optsRow.appendChild(typeSel);
        optsRow.appendChild(durSel);
        optsRow.appendChild(countSel);
        optsRow.appendChild(goBtn);
        paletteEl.appendChild(optsRow);

        const list = createDiv(null, 'daw-palette-list');
        for (const res of paletteResults) {
            const card = createDiv(null, 'daw-palette-card');
            const label = createDiv(null, 'daw-palette-card-label');
            label.textContent = res.prompt.slice(0, 40);
            label.title = `${res.prompt} (seed ${res.seed})`;
            card.appendChild(label);
            const audio = document.createElement('audio');
            audio.controls = true;
            audio.src = res.url;
            audio.className = 'daw-palette-audio';
            card.appendChild(audio);
            const actions = createDiv(null, 'daw-palette-actions');
            const addBtn = document.createElement('button');
            addBtn.className = 'basic-button btn-sm';
            addBtn.textContent = '+ Track';
            addBtn.addEventListener('click', async () => {
                pushUndo();
                const track = addTrack({ name: res.prompt.slice(0, 16) });
                await addClipToTrack(track, res.blob, { name: res.prompt.slice(0, 24), startTime: snapTime(state.currentTime) });
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
            });
            actions.appendChild(addBtn);
            const padBtn = document.createElement('button');
            padBtn.className = 'basic-button btn-sm';
            padBtn.textContent = '+ Pad';
            padBtn.title = 'Add as a beat sequencer pad';
            padBtn.addEventListener('click', async () => {
                const beatsTab = bottomPanelEl?.querySelector('.daw-bottom-tab-content[data-tab="beats"]');
                if (beatsTab) {
                    await addBeatPadBlob(res.prompt.slice(0, 18), res.blob, beatsTab);
                    switchBottomTab('beats');
                }
            });
            actions.appendChild(padBtn);
            const varBtn = document.createElement('button');
            varBtn.className = 'basic-button btn-sm';
            varBtn.innerHTML = DAW_ICONS.refresh;
            varBtn.title = 'Generate a variation (same prompt, new seed)';
            varBtn.addEventListener('click', async () => {
                varBtn.disabled = true;
                const results = await paletteGenerate(res.prompt, res.type, Math.max(1, Math.round(res.blob.size / (44100 * 4 * 2))) || 4, 1);
                paletteResults.unshift(...results);
                paletteResults = paletteResults.slice(0, 12);
                renderPalette();
            });
            actions.appendChild(varBtn);
            card.appendChild(actions);
            list.appendChild(card);
        }
        if (!paletteResults.length) {
            list.innerHTML = '<div class="daw-stems-clipinfo" style="padding:0.5rem;">Generated sounds appear here — audition, then add to a track or beat pad.</div>';
        }
        paletteEl.appendChild(list);
    }

    // ===== PROJECT PERSISTENCE =====
    // IndexedDB (AudioDawStore) holds a crash-safe autosave slot; explicit saves go
    // to the server per-user via AudioLabSaveProject/LoadProject/ListProjects.

    const AUTOSAVE_SLOT = '__autosave__';
    const AUTOSAVE_MAX_AGE_MS = 7 * 24 * 3600 * 1000;
    const SERVER_SAVE_LIMIT_BYTES = 40 * 1024 * 1024;
    let autosaveTimer = null;
    let currentProjectName = null;

    function serializeProject() {
        return {
            version: 2,
            projectName: currentProjectName || null,
            bpm: state.bpm,
            masterLimiterEnabled: state.masterLimiterEnabled,
            timeSignature: state.timeSignature,
            masterVolume: state.masterVolume,
            rulerMode: state.rulerMode,
            snapEnabled: state.snapEnabled,
            beatPattern: {
                steps: state.beatPattern.steps,
                swing: state.beatPattern.swing,
                lanes: state.beatPattern.lanes.map(l => ({ name: l.name, blobKey: l.blobKey, gain: l.gain, steps: [...l.steps] }))
            },
            tracks: state.tracks.map(t => AudioDawTrack.serializeTrack(t))
        };
    }

    /** All blobs referenced by live clips, deduped by blobKey. */
    function collectProjectBlobs() {
        const blobs = new Map();
        for (const track of state.tracks) {
            for (const clip of track.clips) {
                if (!blobs.has(clip.blobKey)) {
                    const stored = blobStore.get(clip.blobKey);
                    if (stored) blobs.set(clip.blobKey, stored.blob);
                }
            }
        }
        for (const lane of state.beatPattern.lanes) {
            if (!blobs.has(lane.blobKey)) {
                const stored = blobStore.get(lane.blobKey);
                if (stored) blobs.set(lane.blobKey, stored.blob);
            }
        }
        return blobs;
    }

    /** Replace the whole arrangement with a deserialized project + its audio blobs. */
    async function restoreProject(project, blobs) {
        abortRecording();
        stopPlayback();
        for (const track of state.tracks) AudioDawTrack.destroyTrack(track);
        const keepZoom = state.zoom;
        state = getDefaultState();
        state.zoom = keepZoom;
        if (project.projectName) currentProjectName = project.projectName;
        state.bpm = project.bpm || 120;
        state.timeSignature = project.timeSignature || [4, 4];
        state.masterVolume = project.masterVolume ?? 1.0;
        state.rulerMode = project.rulerMode || 'time';
        state.snapEnabled = project.snapEnabled !== false;
        state.masterLimiterEnabled = project.masterLimiterEnabled !== false;
        if (project.beatPattern) {
            state.beatPattern = {
                steps: project.beatPattern.steps || 16,
                swing: project.beatPattern.swing || 0,
                lanes: (project.beatPattern.lanes || []).map(l => ({ name: l.name, blobKey: l.blobKey, gain: l.gain ?? 1, steps: [...(l.steps || [])] }))
            };
            for (const lane of state.beatPattern.lanes) {
                if (!blobStore.has(lane.blobKey) && blobs.has(lane.blobKey)) {
                    blobStore.set(lane.blobKey, { blob: blobs.get(lane.blobKey), decodedBuffer: null });
                }
            }
        }
        blobStore.clear();

        for (const ts of project.tracks || []) {
            const track = AudioDawTrack.createTrack({ name: ts.name, color: ts.color, height: ts.height });
            track.volume = ts.volume ?? 0.8;
            track.pan = ts.pan || 0;
            track.muted = !!ts.muted;
            track.soloed = !!ts.soloed;
            track.armed = !!ts.armed;
            track.fx = (ts.fx || []).filter(f => typeof AudioDawFx === 'undefined' || AudioDawFx.FX_DEFS[f.type])
                .map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } }));
            track.automation = {
                volume: (ts.automation?.volume || []).map(p => ({ ...p })),
                pan: (ts.automation?.pan || []).map(p => ({ ...p }))
            };
            track.automationVisible = !!ts.automationVisible;
            track.automationParam = ts.automationParam || 'volume';
            for (const cs of ts.clips || []) {
                const blob = blobs.get(cs.blobKey);
                if (!blob) continue;
                const clip = AudioDawTrack.createClip(blob, {
                    name: cs.name, startTime: cs.startTime, color: cs.color, blobKey: cs.blobKey
                });
                const stored = blobStore.get(cs.blobKey);
                if (stored?.decodedBuffer) {
                    clip.decodedBuffer = stored.decodedBuffer;
                    clip.duration = stored.decodedBuffer.duration;
                } else {
                    await AudioDawTrack.decodeClip(clip);
                    blobStore.set(cs.blobKey, { blob, decodedBuffer: clip.decodedBuffer });
                }
                clip.offset = cs.offset || 0;
                clip.trimEnd = cs.trimEnd || 0;
                clip.gain = cs.gain ?? 1;
                clip.fadeIn = cs.fadeIn || 0;
                clip.fadeOut = cs.fadeOut || 0;
                clip.muted = !!cs.muted;
                track.clips.push(clip);
            }
            state.tracks.push(track);
        }
        if (state.tracks.length === 0) addTrack();
        state.selectedTrackId = state.tracks[0]?.id || null;

        buildTransport();
        if (timeline) {
            timeline.setTempo(state.bpm, state.timeSignature);
            timeline.setMode(state.rulerMode);
        }
        updateTotalDuration();
        renderAllTracks();
        updateBottomPanel();
        updateTimeDisplay();
        updatePlayheadPosition();
    }

    function scheduleAutosave() {
        if (typeof AudioDawStore === 'undefined') return;
        if (autosaveTimer) clearTimeout(autosaveTimer);
        autosaveTimer = setTimeout(() => { autosaveTimer = null; flushAutosave(); }, 3000);
    }

    /** Snapshot synchronously, write async — safe to call right before teardown. */
    function flushAutosave() {
        if (typeof AudioDawStore === 'undefined' || !state) return;
        if (autosaveTimer) { clearTimeout(autosaveTimer); autosaveTimer = null; }
        if (!state.tracks.some(t => t.clips.length > 0)) return;
        const project = serializeProject();
        const blobs = collectProjectBlobs();
        AudioDawStore.saveProject(AUTOSAVE_SLOT, project, blobs)
            .catch(err => console.warn('[AudioDaw] Autosave failed:', err));
    }

    async function maybeOfferResume() {
        if (typeof AudioDawStore === 'undefined') return;
        try {
            const meta = await AudioDawStore.getProjectMeta(AUTOSAVE_SLOT);
            if (!meta || Date.now() - meta.savedAt > AUTOSAVE_MAX_AGE_MS) return;
            showResumeBar(meta);
        } catch (_) {}
    }

    /**
     * Start screen: when the DAW opens EMPTY (no crash autosave shown, no clips),
     * offer the user's saved projects so the tab is a real way back into their
     * work — the DAW-app "recent projects on launch" pattern.
     */
    async function maybeShowStartScreen() {
        const body = document.getElementById('daw_container');
        if (!body || body.querySelector('.daw-resume-bar, .daw-start-screen')) return;
        if (!state || state.tracks.some(t => t.clips.length > 0)) return;
        let names = [];
        try {
            const result = await AudioLabAPI.callAPI('AudioLabListProjects');
            names = (result.projects || []).filter(n => n !== AUTOSAVE_SLOT);
        } catch (_) {}
        if (!names.length) return;
        // Re-check after the await — the user may have started working meanwhile
        if (!state || state.tracks.some(t => t.clips.length > 0)) return;
        if (body.querySelector('.daw-resume-bar, .daw-start-screen')) return;

        const card = createDiv(null, 'daw-resume-bar daw-start-screen');
        const title = createDiv(null, 'daw-resume-title');
        title.textContent = 'Open a project';
        card.appendChild(title);
        const detail = createDiv(null, 'daw-resume-detail');
        detail.textContent = 'Pick up where you left off, or start fresh.';
        card.appendChild(detail);
        const list = createDiv(null, 'daw-start-projects');
        for (const n of names.slice(0, 8)) {
            quickAppendButton(list, escapeHtml(n), () => {
                card.remove();
                openProjectFromServer(n);
            }, ' basic-button', `Load project "${n}"`);
        }
        card.appendChild(list);
        const actions = createDiv(null, 'daw-resume-actions');
        quickAppendButton(actions, 'Start Empty', () => card.remove(), ' basic-button', 'Begin a fresh session');
        card.appendChild(actions);
        body.appendChild(card);
    }

    function showResumeBar(meta) {
        const body = document.getElementById('daw_container');
        if (!body || body.querySelector('.daw-resume-bar')) return;
        const bar = createDiv(null, 'daw-resume-bar');

        const title = createDiv(null, 'daw-resume-title');
        title.textContent = 'Resume your last session?';
        bar.appendChild(title);

        // Session summary from the recovered project JSON
        const proj = meta.project || {};
        const tracks = Array.isArray(proj.tracks) ? proj.tracks : [];
        const clipCount = tracks.reduce((n, t) => n + (t.clips?.length || 0), 0);
        const bits = [];
        if (proj.projectName) bits.push(`“${proj.projectName}”`);
        bits.push(`${tracks.length} track${tracks.length === 1 ? '' : 's'}`);
        bits.push(`${clipCount} clip${clipCount === 1 ? '' : 's'}`);
        if (proj.bpm) bits.push(`${proj.bpm} BPM`);
        bits.push(`autosaved ${new Date(meta.savedAt).toLocaleString()}`);
        const detail = createDiv(null, 'daw-resume-detail');
        detail.textContent = bits.join(' · ');
        bar.appendChild(detail);

        const actions = createDiv(null, 'daw-resume-actions');
        quickAppendButton(actions, 'Resume', async () => {
            bar.remove();
            const overlay = showDawLoadingOverlay('Restoring session...');
            try {
                const data = await AudioDawStore.loadProject(AUTOSAVE_SLOT);
                if (data) await restoreProject(data.project, data.blobs);
            } catch (err) {
                console.error('[AudioDaw] Resume failed:', err);
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover('Resume failed: ' + err.message, 'notice-pop-red');
                }
            }
            hideDawLoadingOverlay(overlay);
        }, ' basic-button btn-primary', 'Restore the autosaved arrangement');
        quickAppendButton(actions, 'Discard', () => {
            bar.remove();
            AudioDawStore.deleteProject(AUTOSAVE_SLOT).catch(() => {});
        }, ' basic-button', 'Delete the autosave — start fresh');
        bar.appendChild(actions);
        body.appendChild(bar);
    }

    async function saveProjectToServer(name) {
        const overlay = showDawLoadingOverlay('Saving project...');
        try {
            const project = serializeProject();
            const blobs = collectProjectBlobs();
            let total = 0;
            for (const b of blobs.values()) total += b.size;
            if (total > SERVER_SAVE_LIMIT_BYTES) {
                throw new Error(`Project audio too large for server save (${Math.round(total / 1024 / 1024)}MB, max ${SERVER_SAVE_LIMIT_BYTES / 1024 / 1024}MB)`);
            }
            const blobData = {};
            for (const [key, blob] of blobs) {
                blobData[key] = { b64: await AudioLabCore.readAsBase64(blob), type: blob.type || 'audio/wav' };
            }
            project.blobs = blobData;
            const result = await AudioLabAPI.callAPI('AudioLabSaveProject', {
                name, project_json: JSON.stringify(project)
            });
            if (!result.success) throw new Error(result.error || 'Save failed');
            currentProjectName = name;
            if (typeof doNoticePopover === 'function') doNoticePopover(`Project "${name}" saved`, 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Save project failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover('Save failed: ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    async function openProjectFromServer(name) {
        const overlay = showDawLoadingOverlay('Loading project...');
        try {
            const result = await AudioLabAPI.callAPI('AudioLabLoadProject', { name });
            if (!result.success) throw new Error(result.error || 'Load failed');
            const project = JSON.parse(result.project_json);
            const blobs = new Map();
            for (const [key, info] of Object.entries(project.blobs || {})) {
                blobs.set(key, AudioLabCore.base64ToBlob(info.b64, info.type || 'audio/wav'));
            }
            delete project.blobs;
            await restoreProject(project, blobs);
            currentProjectName = name;
            if (typeof doNoticePopover === 'function') doNoticePopover(`Project "${name}" loaded`, 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Load project failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover('Load failed: ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    function promptSaveAs() {
        const name = prompt('Project name:', currentProjectName || 'My Project');
        if (name && name.trim()) saveProjectToServer(name.trim());
    }

    function resetToEmptyProject() {
        resetState();
        currentProjectName = null;
        addTrack();
        buildTransport();
        updateTotalDuration();
        renderAllTracks();
        updateBottomPanel();
        updateTimeDisplay();
        updatePlayheadPosition();
    }

    function showProjectMenu(e) {
        dawMenu(e, [
            { label: currentProjectName ? `Save "${currentProjectName}"` : 'Save…', action: () => {
                if (currentProjectName) saveProjectToServer(currentProjectName);
                else promptSaveAs();
            }},
            { label: 'Save As…', action: promptSaveAs },
            { label: 'Open…', action: async () => {
                try {
                    const result = await AudioLabAPI.callAPI('AudioLabListProjects');
                    const names = (result.projects || []).filter(n => n !== AUTOSAVE_SLOT);
                    if (!names.length) {
                        if (typeof doNoticePopover === 'function') doNoticePopover('No saved projects yet', 'notice-pop-yellow');
                        return;
                    }
                    dawMenu(e, names.map(n => ({ label: n, action: () => openProjectFromServer(n) })));
                } catch (err) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('Failed to list projects: ' + err.message, 'notice-pop-red');
                }
            }},
            { label: 'New Project', action: () => {
                if (state.tracks.some(t => t.clips.length > 0) && !confirm('Start a new empty project? Unsaved changes are kept only in the autosave slot.')) {
                    return;
                }
                flushAutosave();
                resetToEmptyProject();
            }}
        ]);
    }

    /** Render the mixdown and store it in the user's Swarm output history. */
    async function saveMixdownToOutputs() {
        try {
            if (typeof doNoticePopover === 'function') doNoticePopover('Rendering mixdown...', 'notice-pop-blue');
            const wavBlob = await renderMixdownBlob();
            if (!wavBlob) throw new Error('Nothing to export');
            const base64 = await AudioLabCore.readAsBase64(wavBlob);
            const result = await AudioLabAPI.callAPI('AddImageToHistory', {
                image: `data:audio/wav;base64,${base64}`,
                prompt: 'AudioLab DAW mixdown'
            });
            if (result.error) throw new Error(result.error);
            if (typeof doNoticePopover === 'function') doNoticePopover('Mixdown saved to Outputs', 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Save to outputs failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover('Save to Outputs failed: ' + err.message, 'notice-pop-red');
        }
    }

    // ===== EXPORT =====

    function showExportMenu(e) {
        dawMenu(e, [
            { label: 'WAV (Lossless)', action: () => doExportMixdown('wav') },
            { label: 'MP3 (192kbps)', action: () => doExportMixdown('mp3') },
            { label: 'OGG Vorbis', action: () => doExportMixdown('ogg') },
            { label: 'FLAC (Lossless)', action: () => doExportMixdown('flac') },
            { label: 'AAC (192kbps)', action: () => doExportMixdown('aac') },
            { label: 'Save WAV to Swarm Outputs', action: saveMixdownToOutputs }
        ]);
    }

    async function doExportMixdown(format = 'wav') {
        if (state.tracks.length === 0) return;
        try {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Rendering mixdown...', 'notice-pop-blue');
            }
            const wavBlob = await renderMixdownBlob();
            if (!wavBlob) throw new Error('Nothing to export');

            if (format === 'wav') {
                downloadBlob(wavBlob, `audiolab-mixdown-${Date.now()}.wav`);
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover('WAV exported', 'notice-pop-green');
                }
            } else {
                // Convert via backend API
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover(`Converting to ${format.toUpperCase()}...`, 'notice-pop-blue');
                }
                const base64 = await AudioLabCore.readAsBase64(wavBlob);
                const result = await AudioLabAPI.callAPI('ConvertAudioFormat', {
                    audio_data: base64,
                    format: format
                });
                if (result.success && result.audio_data) {
                    const convertedBlob = AudioLabCore.base64ToBlob(result.audio_data, result.mime_type || 'audio/mpeg');
                    downloadBlob(convertedBlob, `audiolab-mixdown-${Date.now()}.${format}`);
                    if (typeof doNoticePopover === 'function') {
                        doNoticePopover(`${format.toUpperCase()} exported`, 'notice-pop-green');
                    }
                } else {
                    throw new Error(result.error || 'Conversion failed');
                }
            }
        } catch (err) {
            console.error('[AudioDaw] Export failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Export failed: ' + err.message, 'notice-pop-red');
            }
        }
    }

    function downloadBlob(blob, filename) {
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    }

    /**
     * Convert an AudioBuffer to a WAV Blob.
     * Pure JS implementation — no dependency on Crunker for export.
     */
    function audioBufferToWav(buffer) {
        const numChannels = buffer.numberOfChannels;
        const sampleRate = buffer.sampleRate;
        const format = 1; // PCM
        const bitDepth = 16;
        const bytesPerSample = bitDepth / 8;
        const blockAlign = numChannels * bytesPerSample;
        const numSamples = buffer.length;
        const dataSize = numSamples * blockAlign;
        const headerSize = 44;
        const arrayBuffer = new ArrayBuffer(headerSize + dataSize);
        const view = new DataView(arrayBuffer);

        // WAV header
        writeString(view, 0, 'RIFF');
        view.setUint32(4, headerSize + dataSize - 8, true);
        writeString(view, 8, 'WAVE');
        writeString(view, 12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, format, true);
        view.setUint16(22, numChannels, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * blockAlign, true);
        view.setUint16(32, blockAlign, true);
        view.setUint16(34, bitDepth, true);
        writeString(view, 36, 'data');
        view.setUint32(40, dataSize, true);

        // Interleave channels and write samples
        const channels = [];
        for (let ch = 0; ch < numChannels; ch++) {
            channels.push(buffer.getChannelData(ch));
        }
        let offset = 44;
        for (let i = 0; i < numSamples; i++) {
            for (let ch = 0; ch < numChannels; ch++) {
                const sample = Math.max(-1, Math.min(1, channels[ch][i]));
                view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7FFF, true);
                offset += 2;
            }
        }

        return new Blob([arrayBuffer], { type: 'audio/wav' });
    }

    function writeString(view, offset, str) {
        for (let i = 0; i < str.length; i++) {
            view.setUint8(offset + i, str.charCodeAt(i));
        }
    }

    // ===== KEYBOARD SHORTCUTS =====

    function handleKeyboard(e) {
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT' || e.target.isContentEditable) return;
        const key = e.key.toLowerCase();

        if (key === ' ') {
            e.preventDefault();
            if (recording) stopRecordingFlow();
            else togglePlayback();
        }
        else if (key === 'r' && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
            if (recording) stopRecordingFlow();
            else startRecordingFlow();
        }
        else if (key === 'z' && (e.ctrlKey || e.metaKey) && e.shiftKey) { e.preventDefault(); doRedo(); }
        else if (key === 'z' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doUndo(); }
        else if (key === 's' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            if (currentProjectName) saveProjectToServer(currentProjectName);
            else promptSaveAs();
        }
        else if (key === 'd' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            const sel = findClipById(state.selectedClipId);
            if (sel) doDuplicateClip(sel.clip, sel.track);
        }
        else if (key === 'delete' || key === 'backspace') {
            if (state.selectedClipId) {
                const track = state.tracks.find(t => t.clips.some(c => c.id === state.selectedClipId));
                const clip = track?.clips.find(c => c.id === state.selectedClipId);
                if (clip && track) doDeleteClip(clip, track);
            }
        }
        else if (key === 'm') {
            const track = getSelectedTrack();
            if (track) {
                track.muted = !track.muted;
                updatePlaybackGains();
                renderAllTracks();
            }
        }
        else if (key === 's') {
            const track = getSelectedTrack();
            if (track) {
                track.soloed = !track.soloed;
                updatePlaybackGains();
                renderAllTracks();
            }
        }
        else if (key === 'l') { toggleLoop(); }
        else if (key === 't') {
            const sel = findClipById(state.selectedClipId);
            if (sel) doSplitClip(sel.clip, sel.track);
        }
        else if (key === 'b') { switchBottomTab('beats'); }
        else if (key === 'f') { switchBottomTab('fx'); }
        else if (key === '+' || key === '=') { e.preventDefault(); setZoom(state.zoom * 1.25); }
        else if (key === '-') { e.preventDefault(); setZoom(state.zoom / 1.25); }
        else if (key === 'home') { e.preventDefault(); seekTo(0); }
        else if (key === 'end') { e.preventDefault(); seekTo(state.totalDuration); }
        else if (key === '?') { e.preventDefault(); showShortcutHelp(); }
    }

    function showShortcutHelp() {
        const existing = document.querySelector('.daw-shortcut-help');
        if (existing) { existing.remove(); return; }
        const overlay = createDiv(null, 'daw-shortcut-help');
        const rows = [
            ['Space', 'Play / Pause (stops recording)'],
            ['R', 'Start / stop recording'],
            ['L', 'Toggle loop'],
            ['M / S', 'Mute / solo selected track'],
            ['T', 'Split selected clip at playhead'],
            ['Delete', 'Delete selected clip'],
            ['Ctrl+Z / Ctrl+Shift+Z', 'Undo / redo'],
            ['Ctrl+D', 'Duplicate selected clip'],
            ['Ctrl+S', 'Save project'],
            ['+ / -', 'Zoom in / out'],
            ['Home / End', 'Jump to start / end'],
            ['?', 'Toggle this help']
        ];
        overlay.innerHTML = '<div class="daw-shortcut-help-title">Keyboard Shortcuts</div>'
            + rows.map(([k, d]) => `<div class="daw-shortcut-row"><kbd>${escapeHtml(k)}</kbd><span>${escapeHtml(d)}</span></div>`).join('')
            + '<div class="daw-shortcut-help-hint">Click anywhere to close</div>';
        overlay.addEventListener('click', () => overlay.remove());
        const body = document.getElementById('daw_container');
        (body || document.body).appendChild(overlay);
    }

    // ===== HELPERS =====

    async function fetchAsBlob(src) {
        if (src instanceof Blob) return src;
        if (src.startsWith('data:')) {
            const mimeType = src.substring(src.indexOf(':') + 1, src.indexOf(';'));
            const base64 = src.split(',')[1];
            return AudioLabCore.base64ToBlob(base64, mimeType);
        }
        const resp = await fetch(src);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        return resp.blob();
    }

    function getFilenameFromSrc(src) {
        if (src.startsWith('data:')) return 'Audio Clip';
        try {
            const url = new URL(src, window.location.origin);
            const parts = url.pathname.split('/');
            return decodeURIComponent(parts[parts.length - 1]) || 'Audio Clip';
        } catch {
            return 'Audio Clip';
        }
    }

    function formatTimePrecise(seconds) {
        if (!seconds || !isFinite(seconds) || seconds < 0) return '0:00.0';
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        const ms = Math.floor((seconds % 1) * 10);
        return `${m}:${s.toString().padStart(2, '0')}.${ms}`;
    }

    function resetState() {
        abortRecording();
        stopBeatAudition();
        stopPlayback();
        teardownPlaybackGraph();
        // Destroy existing tracks
        if (state) {
            for (const track of state.tracks) {
                AudioDawTrack.destroyTrack(track);
            }
        }
        state = getDefaultState();
        blobStore.clear();
    }

    function destroyAll() {
        abortRecording();
        stopBeatAudition();
        if (clipEditorPlayer) {
            try { clipEditorPlayer.destroy(); } catch (_) {}
            clipEditorPlayer = null;
        }
        if (paletteEl) { paletteEl.remove(); paletteEl = null; }
        for (const r of paletteResults) { try { URL.revokeObjectURL(r.url); } catch (_) {} }
        paletteResults = [];
        stopPlayback();
        teardownPlaybackGraph();
        if (state) {
            for (const track of state.tracks) {
                AudioDawTrack.destroyTrack(track);
            }
        }
        if (timeline) { timeline.destroy(); timeline = null; }
        if (audioCtx && audioCtx.state !== 'closed') {
            try { audioCtx.close(); } catch (_) {}
        }
        audioCtx = null;
        state = null;
        blobStore.clear();
    }

    // ===== TAB-MODE BOOT (once per page load) =====
    // Hides the tab until it's needed, wires the tab-shown lifecycle (lazy init +
    // mic release on switch-away), and reveals a dormant tab when a resumable
    // autosave exists. No-ops on pages without the AudioLab tab.
    function bootTabMode() {
        const container = document.getElementById('daw_container');
        if (!container || !dawTabLink()) return;
        hideDawTab();
        // Keyboard: document-level, only while the DAW tab is the visible one
        document.addEventListener('keydown', (e) => {
            if (!initialized || !state || container.offsetParent === null) return;
            handleKeyboard(e);
        });
        // Browser tabs die without warning — flush the autosave on page close too
        window.addEventListener('beforeunload', () => flushAutosave());
        window.addEventListener('resize', sizeDawContainer);
        if (typeof $ !== 'undefined') {
            $('#toptablist').on('shown.bs.tab', (e) => {
                if (e.target.id === 'maintab_audiolab') {
                    sizeDawContainer(); // pane just became measurable
                    // Covers hash-nav landing directly on #audiolab too (no re-click
                    // from inside the shown handler — just unhide + init)
                    if (!initialized) {
                        dawTabLink()?.closest('.nav-item')?.classList.remove('daw-tab-hidden');
                        ensureInitialized();
                        maybeOfferResume().then(maybeShowStartScreen);
                    }
                } else if (recording) {
                    // Never leave the microphone hot while the DAW is hidden
                    stopRecordingFlow();
                }
            });
        }
        // Dormant tab: a crash-recovery autosave only exists when a session ended
        // WITHOUT a clean Close (clean closes delete the slot) — reveal the tab
        // so the user can get back to their interrupted work.
        if (typeof AudioDawStore !== 'undefined') {
            AudioDawStore.getProjectMeta(AUTOSAVE_SLOT).then(meta => {
                if (meta && Date.now() - meta.savedAt <= AUTOSAVE_MAX_AGE_MS) {
                    dawTabLink()?.closest('.nav-item')?.classList.remove('daw-tab-hidden');
                }
            }).catch(() => {});
        }
        // Users with saved projects get the tab as their way back into the DAW —
        // clicking it opens the start screen with their project list. (Delayed so
        // the session is established before the API call.)
        setTimeout(() => {
            try {
                AudioLabAPI.callAPI('AudioLabListProjects').then(result => {
                    const names = (result?.projects || []).filter(n => n !== AUTOSAVE_SLOT);
                    if (names.length) dawTabLink()?.closest('.nav-item')?.classList.remove('daw-tab-hidden');
                }).catch(() => {});
            } catch (_) {}
        }, 1500);
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bootTabMode);
    } else {
        bootTabMode();
    }

    return { open, close };
})();
