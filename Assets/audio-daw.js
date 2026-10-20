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

    /** MM:SS with one decimal, via core's shared formatter (which also rolls over past an hour). */
    const durationStringifyColons2 = (seconds) => durationStringifyColons(seconds, 1);

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
    // Transport session; null when stopped. Sources play into the persistent mixer graph (see ensureMixer).
    // { ctx, liveClips: [{clipId, trackId, iteration, source, fadeGain, clipGain}], clicks, clickSeen,
    //   baseCtxTime, baseTimelineTime, endTime, schedulerId, loop: {nextWrapCtxTime, nextIterScheduled, iteration}|null }
    let playback = null;
    const LOOKAHEAD = 0.2;         // seconds ahead the loop scheduler pre-schedules the next iteration
    const SCHEDULER_TICK_MS = 25;  // setInterval period (rAF would stall in background tabs)
    const START_EPSILON = 0.03;    // shared start instant so no source starts "in the past"
    // Recording session; null when idle. { takes: [{track, entry, latency, stopFrame}], placeholders, punch,
    //   postRoll, startP, startCtx, stopping }
    let recording = null;
    // Guards against double-triggering a Demucs pass while one is running
    let stemsSeparating = false;
    // Clip-editor waveform preview player (destroyed on each panel re-render)
    let clipEditorPlayer = null;
    let recordSettings = loadRecordSettings();

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
            metronome: false,    // CLICK: metronome while the transport runs
            punchEnabled: false, // PUNCH: record only over the loop range
            selection: null,     // {start, end, trackIds} time range across tracks
            rulerMode: 'time', // 'time' | 'beats'
            markers: [],      // [{id, time, name, color}] sorted by time
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

    // ===== TAB =====
    // The tab is always reachable (Swarm's own tab permission still gates it); it used to stay hidden until an
    // autosave, a saved project or an audio result existed, which left a first-time user no way in.

    function dawTabLink() { return document.getElementById('maintab_audiolab'); }

    function showDawTab() {
        const link = dawTabLink();
        if (!link) return;
        link.click();
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
        const overlay = showDawLoadingOverlay(translate('Loading audio...'));
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
                doNoticePopover(translate('Failed to load audio:') + ' ' + err.message, 'notice-pop-red');
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
        title.textContent = translate('Close Audio Lab?');
        dlg.appendChild(title);
        const msg = createDiv(null, 'daw-stems-desc');
        msg.textContent = translate('Save this session as a project, or discard it. Discarded sessions are gone for good.');
        dlg.appendChild(msg);
        const row = createDiv(null, 'daw-clip-editor-actions');
        row.style.marginTop = '0.75rem';
        quickAppendButton(row, translate('Save & Close'), async () => {
            const name = prompt(translate('Project name:'), currentProjectName || 'My Project');
            if (!name || !name.trim()) return; // keep dialog open — treat as cancel
            dlg.remove();
            await saveProjectToServer(name.trim());
            finishClose();
        }, ' basic-button btn-primary', translate('Save the project to the server, then close'));
        quickAppendButton(row, translate('Discard'), () => {
            dlg.remove();
            finishClose();
        }, ' basic-button', translate('Throw this session away and close'));
        quickAppendButton(row, translate('Cancel'), () => dlg.remove(), ' basic-button', translate('Keep working'));
        dlg.appendChild(row);
        (document.getElementById('daw_container') || document.body).appendChild(dlg);
    }

    /** Full teardown: delete the crash-recovery autosave, free all memory, return to the Generate tab. */
    function finishClose() {
        if (typeof AudioDawStore !== 'undefined') {
            AudioDawStore.deleteProject(AUTOSAVE_SLOT).catch(() => {});
        }
        destroyAll();
        initialized = false;
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
        // The bottom tab strip is static markup (Audio Lab.html) carrying class="translate";
        // its labels are never reassigned, so one sweep here is safe and covers the case where
        // the tab pane is injected after core's one-time page-load sweep has already run.
        // Everything this file builds in JS bakes translate() in directly instead.
        if (typeof applyTranslations === 'function') {
            const tabs = document.getElementById('daw_bbar_tabs');
            if (tabs) applyTranslations(tabs);
        }
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
        recBtn.title = translate('Record into armed track (R)');
        recBtn.addEventListener('click', () => {
            if (recording) stopRecordingFlow(); else startRecordingFlow();
        });
        recWrap.appendChild(recBtn);
        const micBtn = document.createElement('button');
        micBtn.className = 'daw-transport-btn daw-btn-mic-settings';
        micBtn.innerHTML = DAW_ICONS.caretDown;
        micBtn.title = translate('Microphone settings');
        micBtn.addEventListener('click', (e) => showMicSettingsMenu(e));
        recWrap.appendChild(micBtn);
        transGroup.appendChild(recWrap);
        quickAppendButton(transGroup, DAW_ICONS.toStart, () => seekTo(0), ' daw-transport-btn', translate('Rewind to start'));
        const playBtn = document.createElement('button');
        playBtn.className = 'daw-transport-btn daw-btn-play';
        playBtn.innerHTML = DAW_ICONS.play;
        playBtn.title = translate('Play / Pause (Space)');
        playBtn.addEventListener('click', togglePlayback);
        transGroup.appendChild(playBtn);
        quickAppendButton(transGroup, DAW_ICONS.stop, () => {
            if (recording) { stopRecordingFlow(); return; }
            stopPlayback();
            seekTo(0);
        }, ' daw-transport-btn', translate('Stop'));
        quickAppendButton(transGroup, DAW_ICONS.toEnd, () => seekTo(state.contentDuration), ' daw-transport-btn', translate('Go to end'));
        const loopBtn = document.createElement('button');
        loopBtn.className = 'daw-transport-btn daw-btn-text daw-btn-loop' + (state.loopEnabled ? ' active' : '');
        loopBtn.textContent = translate('LOOP');
        loopBtn.title = translate('Toggle Loop (L)');
        loopBtn.addEventListener('click', toggleLoop);
        transGroup.appendChild(loopBtn);
        const clickBtn = document.createElement('button');
        clickBtn.className = 'daw-transport-btn daw-btn-text daw-btn-click' + (state.metronome ? ' active' : '');
        clickBtn.textContent = translate('CLICK');
        clickBtn.title = translate('Metronome while playing and recording (C). Count-in is in the record menu');
        clickBtn.addEventListener('click', toggleMetronome);
        transGroup.appendChild(clickBtn);
        const punchBtn = document.createElement('button');
        punchBtn.className = 'daw-transport-btn daw-btn-text daw-btn-punch' + (state.punchEnabled ? ' active' : '');
        punchBtn.textContent = translate('PUNCH');
        punchBtn.title = translate('Punch recording: Record replaces only the loop range, after a pre-roll (P)');
        punchBtn.addEventListener('click', togglePunch);
        transGroup.appendChild(punchBtn);

        // ── LCD position cluster: time + bars.beats ──
        const lcd = createDiv(null, 'daw-lcd');
        timeDisplayEl = createSpan(null, 'daw-lcd-time');
        timeDisplayEl.textContent = '0:00.0 / 0:00.0';
        const lcdBeats = createSpan(null, 'daw-lcd-beats');
        lcdBeats.textContent = '1.1.1';
        lcdBeats.title = translate('Position in bars.beats.sixteenths');
        lcd.appendChild(timeDisplayEl);
        lcd.appendChild(lcdBeats);
        transportEl.appendChild(lcd);

        const spacer = createDiv(null, 'daw-transport-spacer');
        transportEl.appendChild(spacer);

        // ── Zoom ──
        const zoomGroup = mkGroup(' daw-tgroup-fields');
        const zoomLabel = createSpan(null, 'daw-transport-label');
        zoomLabel.textContent = translate('ZOOM');
        const zoomSlider = document.createElement('input');
        zoomSlider.type = 'range';
        zoomSlider.className = 'daw-transport-zoom';
        zoomSlider.min = '10';
        zoomSlider.max = '500';
        zoomSlider.value = state.zoom;
        zoomSlider.title = translate('Timeline zoom (pixels per second)');
        zoomSlider.addEventListener('input', (e) => {
            setZoom(parseInt(e.target.value));
        });
        zoomGroup.appendChild(zoomLabel);
        zoomGroup.appendChild(zoomSlider);

        // ── Tempo: BPM + time signature ──
        const tempoGroup = mkGroup(' daw-tgroup-fields');
        const bpmLabel = createSpan(null, 'daw-transport-label');
        bpmLabel.textContent = translate('BPM');
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
            if (mixer && typeof AudioDawFx !== 'undefined') {
                for (const [id, chain] of mixer.chains) {
                    const tr = state.tracks.find(t => t.id === id);
                    if (tr) AudioDawFx.syncDelayTimes(chain.fxChain, tr.fx, state.bpm, mixer.ctx.currentTime);
                }
            }
        });
        const sigSelect = document.createElement('select');
        sigSelect.className = 'daw-transport-timesig';
        sigSelect.title = translate('Time signature');
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
        mkSeg(translate('TIME'), 'time', translate('Ruler shows minutes:seconds'));
        mkSeg(translate('BARS'), 'beats', translate('Ruler shows bars/beats at the project tempo'));

        // ── Workspace toggles: snap-to-grid, sound palette ──
        const togGroup = mkGroup();
        const snapBtn = document.createElement('button');
        snapBtn.className = 'daw-transport-btn daw-btn-text daw-btn-snap' + (state.snapEnabled ? ' active' : '');
        snapBtn.textContent = translate('SNAP');
        snapBtn.title = translate('Snap to grid');
        snapBtn.addEventListener('click', () => {
            state.snapEnabled = !state.snapEnabled;
            snapBtn.classList.toggle('active', state.snapEnabled);
        });
        togGroup.appendChild(snapBtn);
        const palBtn = document.createElement('button');
        palBtn.className = 'daw-transport-btn daw-btn-text daw-btn-palette';
        palBtn.textContent = translate('SOUNDS');
        palBtn.title = translate('Sound Palette: generate SFX/loops on demand (audition, then add)');
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
        mkFileBtn(translate('PROJECT'), translate('Save, load, or start projects'), (e) => showProjectMenu(e));
        mkFileBtn(translate('IMPORT'), translate('Add audio from your outputs or your computer'), (e) => showImportMenu(e));
        mkFileBtn(translate('EXPORT'), translate('Export the mixdown (WAV/MP3/OGG/FLAC/AAC or to Outputs)'), (e) => showExportMenu(e));
        const closeBtn = document.createElement('button');
        closeBtn.className = 'daw-transport-btn daw-btn-close';
        closeBtn.innerHTML = '&#x2715;';
        closeBtn.title = translate('Close Audio Lab');
        closeBtn.addEventListener('click', close);
        transportEl.appendChild(closeBtn);
    }

    // ===== FOOTER =====

    /** Show the unified import menu: server outputs or local files. */
    function showImportMenu(e) {
        dawMenu(e, [
            { label: translate('From Outputs…'), action: () => showOutputsPicker(e) },
            { label: translate('From Computer…'), action: () => importAudioToTrack() }
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
        // Beats + Generate + Score + Voice are built ONCE per session (not in updateBottomPanel) so
        // typed prompts, pattern edits, a half-edited score and a live call survive selection-driven panel refreshes
        renderBeatsPanel(pane('beats'));
        renderGeneratePanel(pane('generate'));
        if (typeof AudioDawScore !== 'undefined') AudioDawScore.render(pane('score'), scoreCallbacks());
        if (typeof AudioVoice !== 'undefined') AudioVoice.render(pane('voice'));
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
                const name = prompt(translate('Chain name:'), 'My Chain');
                if (!name || !name.trim()) return;
                const chains = JSON.parse(localStorage.getItem('audiolab_fx_chains') || '{}');
                chains[name.trim()] = track.fx.map(f => ({ type: f.type, enabled: f.enabled, params: { ...f.params } }));
                localStorage.setItem('audiolab_fx_chains', JSON.stringify(chains));
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Chain saved:') + ` "${name.trim()}"`, 'notice-pop-green');
            },
            onLoadChain: (track, e) => {
                const chains = JSON.parse(localStorage.getItem('audiolab_fx_chains') || '{}');
                const names = Object.keys(chains);
                if (!names.length) {
                    if (typeof doNoticePopover === 'function') doNoticePopover('No saved chains yet. Build one and hit Save Chain', 'notice-pop-yellow');
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
                const chain = mixer?.chains.get(track.id);
                if (chain?.fxChain) {
                    AudioDawFx.applyEffectParams(chain.fxChain, index, fx, mixer.ctx.currentTime,
                        { bpm: state.bpm, ctx: mixer.ctx });
                }
            },
            onToggle: (track, fx, index) => {
                pushUndo();
                const chain = mixer?.chains.get(track.id);
                if (chain?.fxChain) {
                    AudioDawFx.setEffectEnabled(chain.fxChain, index, fx, mixer.ctx.currentTime);
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
                // The master path rewires in place; sources keep playing
                if (mixer) wireMasterOut();
            }
        };
    }

    /** What the Score tab is allowed to reach. The DAW IIFE exports only { open, close }, so a sibling module
     *  gets an explicit surface rather than the closure. */
    function scoreCallbacks() {
        return {
            modelFor: (engineId) => dawSwarmModelFor(engineId),
            generate: (opts) => dawSwarmGenerate(opts),
            busy: (label, tabId) => {
                const busy = createBusyIndicator(label, tabId);
                bottomPanelEl?.querySelector('.daw-bottom-tab-content[data-tab="score"]')?.appendChild(busy);
                return busy;
            },
            showMenu: (e, items) => dawMenu(e, items),
            getTransport: () => ({ bpm: state.bpm, timeSignature: state.timeSignature, currentTime: state.currentTime }),
            /** Every clip carrying a score, which is what the version tree is drawn from. */
            listScoreClips: () => state.tracks.flatMap(t => t.clips.filter(c => c.meta?.score).map(c => ({ clip: c, track: t }))),
            /** A/B rides the mixer's own solo, so the comparison is the one the ears already trust. */
            soloOnly: (trackId) => {
                for (const t of state.tracks) t.soloed = !!trackId && t.id === trackId;
                updatePlaybackGains();
                renderAllTracks();
                updateBottomPanel();
            },
            findClip: (clipId) => findClipById(clipId),
            seek: (seconds) => seekTo(seconds),
            /** Mono 24 kHz for the transcriber: one encoder, so the DAW's export and the model hear the same bytes. */
            encodeWav: (buffer) => audioBufferToWav(buffer),
            /** The Vocals track the Stems tab made from this clip — a cleaner melody to transcribe than the mix. */
            findVocalStem: (clipId) => {
                const source = findClipById(clipId);
                if (!source) return null;
                const want = `Vocals: ${source.clip.name}`;
                for (const track of state.tracks) {
                    if (track.name !== want) continue;
                    const clip = track.clips.find(c => Math.abs((c.startTime || 0) - (source.clip.startTime || 0)) < 0.05)
                        || track.clips[0];
                    if (clip?.blob) return { clip, track };
                }
                return null;
            },
            /** Point the project's grid at what a score says. The transport controls display state, so both move. */
            setTransport: ({ bpm, timeSignature }) => {
                pushUndo();
                if (bpm > 0) state.bpm = Math.round(bpm);
                if (Array.isArray(timeSignature) && timeSignature.length === 2) state.timeSignature = timeSignature;
                if (bpmInputEl) bpmInputEl.value = state.bpm;
                const sig = transportEl?.querySelector('.daw-transport-timesig');
                if (sig) {
                    const want = state.timeSignature.join('/');
                    // A meter the transport does not offer (7/8 off a score) still has to be selectable.
                    if (![...sig.options].some(o => o.value === want)) {
                        const opt = document.createElement('option');
                        opt.value = want;
                        opt.textContent = want;
                        sig.appendChild(opt);
                    }
                    sig.value = want;
                }
                if (timeline) timeline.setTempo(state.bpm, state.timeSignature);
                updateLaneGrid();
            },
            /** A recording dropped on the Score sheet lands as a track like any other import. */
            importClip: async (file) => {
                pushUndo();
                const track = addTrack({ name: file.name.replace(/\.[^.]+$/, '') });
                let clip;
                // A file the browser cannot decode must not leave its track behind to surface on the next render.
                try { clip = await addClipToTrack(track, file, { name: file.name }); }
                catch (e) { removeTrack(track.id); renderAllTracks(); throw e; }
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
                return { clip, track };
            },
            selectClip: (clipId) => {
                const found = findClipById(clipId);
                if (!found) return;
                state.selectedTrackId = found.track.id;
                state.selectedClipId = clipId;
                updateTrackSelection();
                renderAllTracks();
                updateBottomPanel();
            },
            /** Land a rendered score as its own track, carrying the score that produced it.
             *  snapshot:false lets a batch of variants be one undo step rather than one per clip. */
            addRenderedScore: async ({ blob, metadata, label, score, snapshot = true }) => {
                if (snapshot) pushUndo();
                const track = addTrack({ name: label || 'Score' });
                // The engine re-plans nothing when a score is supplied, so the authoritative ABC is the one we
                // sent; metadata only fills in what the request did not pin (seed, resolved style).
                const planned = buildClipScoreMeta(metadata, { label }) || { score: {} };
                const clip = await addClipToTrack(track, blob, {
                    name: label || 'Score',
                    startTime: snapTime(state.currentTime),
                    meta: { score: { ...planned.score, ...score, label, created: Date.now() } }
                });
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
                return clip;
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
                metaEl.textContent = `${track.name} · ${durationStringifyColons2(clip.duration)}s · ${translate('starts at')} ${durationStringifyColons2(clip.startTime)}s`;
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
                splitBtn.textContent = translate('Split at Playhead');
                splitBtn.title = translate('Split clip at current playhead position');
                splitBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doSplitClip(sel.clip, sel.track);
                });
                actions.appendChild(splitBtn);
                const dupBtn = document.createElement('button');
                dupBtn.className = 'basic-button btn-sm';
                dupBtn.textContent = translate('Duplicate');
                dupBtn.title = translate('Duplicate this clip');
                dupBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doDuplicateClip(sel.clip, sel.track);
                });
                actions.appendChild(dupBtn);
                const delBtn = document.createElement('button');
                delBtn.className = 'basic-button btn-sm';
                delBtn.textContent = translate('Delete');
                delBtn.title = translate('Delete this clip');
                delBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const sel = getClip();
                    if (sel) doDeleteClip(sel.clip, sel.track);
                });
                actions.appendChild(delBtn);
                const muteBtn = document.createElement('button');
                muteBtn.className = 'basic-button btn-sm';
                muteBtn.textContent = clip.muted ? translate('Unmute') : translate('Mute');
                muteBtn.title = translate('Toggle clip mute');
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
                gainLabel.textContent = translate('Gain');
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

                const makeFadeInput = (label, prop, titleText) => {
                    const lbl = createSpan(null, 'daw-clip-mix-label');
                    lbl.textContent = label;
                    const input = document.createElement('input');
                    input.type = 'number';
                    input.className = 'daw-clip-fade-input';
                    input.min = '0';
                    input.step = '0.1';
                    input.value = clip[prop] || 0;
                    input.title = titleText;
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
                makeFadeInput(translate('Fade In'), 'fadeIn', translate('Fade in length in seconds'));
                makeFadeInput(translate('Fade Out'), 'fadeOut', translate('Fade out length in seconds'));
                const mixCard = createDiv(null, 'daw-fx-card daw-clip-mix-card');
                const mixTitle = createDiv(null, 'daw-fx-card-title');
                mixTitle.textContent = translate('Clip Mix');
                mixCard.appendChild(mixTitle);
                const mixNote = createDiv(null, 'daw-clip-card-meta');
                mixNote.textContent = translate('Level and fades for this clip only. Track volume lives in the Mixer.');
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
                clipEditorContent.innerHTML = `<div style="color:var(--text-soft);font-size:0.85rem;padding:0.5rem;">${translate('Select a clip to edit')}</div>`;
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

        // Selection only — the Score pane itself is built once, so an in-progress edit is never wiped.
        if (typeof AudioDawScore !== 'undefined') AudioDawScore.onSelection(findClipById(state.selectedClipId));
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
            volLbl.textContent = translate('Vol');
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
        masterLabel.textContent = translate('Master');
        masterLabel.style.fontWeight = '600';
        master.appendChild(masterLabel);
        const masterBtns = createDiv(null, 'daw-mixer-btns');
        master.appendChild(masterBtns);
        const masterVolGroup = createDiv(null, 'daw-mixer-vol-group');
        const masterVolLbl = createSpan(null, 'daw-mixer-vol-label');
        masterVolLbl.textContent = translate('Vol');
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
            split: [translate('Full Split'), translate('Every stem becomes its own track')],
            karaoke: [translate('Karaoke'), translate('Vocals + combined instrumental')],
            acapella: [translate('Acapella'), translate('Vocals only')],
            instrumental: [translate('Instrumental'), translate('Everything except vocals')],
            custom: [translate('Custom'), translate('Pick exactly which stems to keep')]
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
        header.innerHTML = `<strong>${translate('Stem Separation (Demucs)')}</strong>`;
        left.appendChild(header);
        const desc = createDiv(null, 'daw-stems-desc');
        desc.textContent = translate('AI source separation splits a mixed clip into its component parts. Each chosen stem becomes a new track in the DAW.');
        left.appendChild(desc);

        // Model picker
        const modelRow = createDiv(null, 'daw-stems-model-row');
        const modelLabel = document.createElement('label');
        modelLabel.className = 'daw-stems-ctl-label';
        modelLabel.textContent = translate('Model:');
        const modelSelect = document.createElement('select');
        modelSelect.className = 'daw-stems-select';
        for (const id of stemModelChoices()) {
            const opt = document.createElement('option');
            opt.value = id;
            opt.textContent = STEM_MODELS[id].label;
            modelSelect.appendChild(opt);
        }
        if (!generateEngines) {
            // First render before the engine list arrived: redraw once it has, so only real variants show.
            ensureEnginesList().then(() => { if (container.isConnected) renderStemsPanel(container); }).catch(() => {});
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
            hint.textContent = custom ? translate('Choose which stems become tracks:') : translate('Included stems:');
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
            actionRow.innerHTML = `<span class="daw-stems-clipinfo">${translate('Add or import a clip first. Stem separation splits one clip into new tracks')}</span>`;
        } else {
            const srcLabel = document.createElement('label');
            srcLabel.className = 'daw-stems-ctl-label';
            srcLabel.textContent = translate('Source:');
            const srcSelect = document.createElement('select');
            srcSelect.className = 'daw-stems-select';
            for (const { clip, track } of allClips) {
                const opt = document.createElement('option');
                opt.value = clip.id;
                const dur = durationStringifyColons2(clip.duration - clip.offset - clip.trimEnd);
                opt.textContent = `${track.name}: ${clip.name} (${dur}s)`;
                srcSelect.appendChild(opt);
            }
            if (state.selectedClipId && allClips.some(x => x.clip.id === state.selectedClipId)) {
                srcSelect.value = state.selectedClipId;
            }
            actionRow.appendChild(srcLabel);
            actionRow.appendChild(srcSelect);

            sepBtn = document.createElement('button');
            sepBtn.className = 'basic-button btn-sm daw-stems-go';
            sepBtn.textContent = translate('Separate Stems');
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
                    if (!confirm(translate('Stem separation requires the Demucs engine (a one-time ~2 GB download).\n\nInstall it now? Separation will start automatically when it finishes.'))) {
                        return;
                    }
                    sepBtn.disabled = true;
                    const ok = await installDemucs((msg) => {
                        sepBtn.textContent = translate('Installing Demucs…') + ' ' + msg.slice(0, 30);
                    });
                    sepBtn.disabled = false;
                    sepBtn.textContent = translate('Separate Stems');
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
        htdemucs: { label: 'HTDemucs, 4 stems', stems: ['vocals', 'drums', 'bass', 'other'] },
        htdemucs_ft: { label: 'HTDemucs Fine-tuned, 4 stems (best quality)', stems: ['vocals', 'drums', 'bass', 'other'] },
        htdemucs_6s: { label: 'HTDemucs 6-stem, adds guitar + piano', stems: ['vocals', 'drums', 'bass', 'guitar', 'piano', 'other'] }
    };

    /** Demucs variants this server can run: demucs_fx's installed models, else the two the engine ships. */
    function stemModelChoices() {
        const eng = (generateEngines || []).find(e => e.id === 'demucs_fx');
        const ids = (eng?.models || []).filter(m => m.installed !== false).map(m => m.id).filter(id => STEM_MODELS[id]);
        return ids.length ? ids : ['htdemucs', 'htdemucs_6s'];
    }

    // Track colors per stem name (+ the synthesized "instrumental" combine track).
    const STEM_COLORS = {
        vocals: '#cc5de8', drums: '#ff922b', bass: '#22b8cf',
        other: '#82c91e', guitar: '#ffd43b', piano: '#4a9eff', instrumental: '#20c997'
    };

    // Output presets. `plan(stems)` → [{name, parts}]; a `parts` list longer than 1 is summed into one track.
    const STEM_PRESETS = [
        { id: 'split', label: 'Full split: every stem as its own track', plan: (s) => s.map(x => ({ name: x, parts: [x] })) },
        { id: 'karaoke', label: 'Karaoke: vocals + combined instrumental', plan: (s) => [{ name: 'vocals', parts: ['vocals'] }, { name: 'instrumental', parts: s.filter(x => x !== 'vocals') }] },
        { id: 'acapella', label: 'Acapella: vocals only', plan: (s) => [{ name: 'vocals', parts: ['vocals'] }] },
        { id: 'instrumental', label: 'Instrumental: everything except vocals', plan: (s) => [{ name: 'instrumental', parts: s.filter(x => x !== 'vocals') }] },
        { id: 'custom', label: 'Custom: pick stems below', plan: null }
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
    let generateBackendStatus = null; // 'RUNNING', another BackendStatus, or 'NOT_FOUND' (no Audio Backend)

    /**
     * Generate through SwarmUI's core pipeline (GenerateText2ImageWS) — the same
     * endpoint the main Generate tab uses. The output is saved to the user's
     * outputs folder + history like any generation, and progress streams back.
     * @param {Object} opts - { model: Swarm model name, prompt, params: extra T2I params, onProgress(frac) }
     * @returns {Promise<{blob: Blob, src: string, metadata: Object|null}>} the audio, its View URL, and the
     *   parsed generation metadata (core sends it alongside the image; a planned score rides in
     *   metadata.sui_extra_data.yue2_score)
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
                    const metadata = parseGenMetadata(data.metadata);
                    fetch(src)
                        .then(r => { if (!r.ok) throw new Error(`Output fetch failed (${r.status})`); return r.blob(); })
                        .then(blob => { if (!settled) { settled = true; resolve({ blob, src, metadata }); } })
                        .catch(err => fail(err.message));
                }
            }, (err) => fail(err || 'Generation failed'));
        });
    }

    /** Parse core's generation-metadata string. Malformed metadata must never fail a generation. */
    function parseGenMetadata(raw) {
        if (!raw) return null;
        try {
            return typeof raw === 'string' ? JSON.parse(raw) : raw;
        }
        catch (e) {
            console.warn('[AudioLab] Could not parse generation metadata', e);
            return null;
        }
    }

    /**
     * Pull the score a planning model wrote out of a generation's metadata.
     * YuE2 writes it to sui_extra_data.yue2_score (see DynamicAudioBackend.RecordPlannedScore).
     * @returns {Object|null} { abc, truncated } or null when the generation planned no score
     */
    function scoreFromMetadata(metadata) {
        const abc = metadata?.sui_extra_data?.yue2_score;
        if (!abc || !String(abc).trim()) return null;
        return {
            abc: String(abc),
            truncated: metadata.sui_extra_data.yue2_score_truncated === true,
            budgetSeconds: metadata.sui_extra_data.yue2_budget_seconds ?? null
        };
    }

    /**
     * Build a clip's `meta` from a generation's metadata, so a rendered clip carries the score that produced it.
     * Versions form a tree via `parent`, which is what makes "render a variation of this score" traceable.
     * @returns {Object|null} null when the generation planned no score — clips stay metadata-free otherwise
     */
    function buildClipScoreMeta(metadata, { style = '', lyrics = '', model = '', engineId = '', label = '',
            parent = null, source = 'planned', cot = null } = {}) {
        const found = scoreFromMetadata(metadata);
        if (!found) return null;
        const params = metadata?.sui_image_params || {};
        return {
            score: {
                abc: found.abc,
                truncated: found.truncated,
                budgetSeconds: found.budgetSeconds,
                style: style || params.textaudiostyle || '',
                lyrics: lyrics || params.prompt || '',
                cot: cot || params.scoreplanningmode || 'full',
                seed: params.seed ?? null,
                model, engineId, label, parent, source,
                created: Date.now()
            }
        };
    }

    /** Fetch + cache the engine list (shared by the Generate tab, palette, and beat pads). */
    async function ensureEnginesList(force = false) {
        if (!generateEngines || force) {
            const result = await AudioLabAPI.callAPI('AudioLabListEngines');
            if (!result.success) throw new Error(result.error || 'Failed to list engines');
            generateEngines = result.engines || [];
            generateBackendStatus = result.backend_status || 'NOT_FOUND';
        }
        return generateEngines;
    }

    /** First installed model's Swarm-registry name for an installed engine (cached engine list). */
    function dawSwarmModelFor(engineId) {
        const eng = (generateEngines || []).find(x => x.id === engineId && x.installed);
        const m = (eng?.models || []).find(x => x.installed !== false);
        return m?.swarm_model || null;
    }

    // Engines whose Prompt carries LYRICS and Text2Audio Style the description (DynamicAudioBackend maps
    // them that way); every other generator reads its description from the Prompt.
    const LYRIC_MUSIC_ENGINES = new Set(['acestep_music', 'yue_music', 'yue2_music', 'heartlib_music', 'minimax_music3']);
    // Engines that read core's BPM / time signature params (feature audio_ace_inputs).
    const TEMPO_PARAM_ENGINES = new Set(['acestep_music']);
    // Core's Text2Audio Time Signature takes beats per bar, from this set only.
    const CORE_BEATS_PER_BAR = ['2', '3', '4', '6'];
    // Preference order when a feature (palette loops) needs "any music engine".
    const MUSIC_ENGINE_ORDER = ['acestep_music', 'musicgen_music', 'stableaudio_music', 'minimax_music3', 'heartlib_music', 'yue2_music', 'yue_music'];
    // Speech-category engines that are not speech-to-text (the Score tab drives these).
    const NON_SPEECH_STT = new Set(['sheetsage2_transcribe']);

    /**
     * Prompt + core params for a music or SFX request, routed the way the backend reads each engine.
     * Tempo hints become core BPM/meter params where the engine takes them, and a "120 BPM" tag on the
     * description otherwise (MusicGen and Stable Audio were trained on tagged descriptions).
     * @returns {{prompt: string, params: Object}}
     */
    function buildAudioGenRequest(engineId, { description, lyrics = '', duration = 0, seed = -1, tempoHints = false }) {
        const params = {};
        let prompt;
        if (LYRIC_MUSIC_ENGINES.has(engineId)) {
            params.textaudiostyle = description;
            prompt = lyrics;
        }
        else {
            prompt = description;
            if (tempoHints && !TEMPO_PARAM_ENGINES.has(engineId) && !/\bbpm\b/i.test(description)) {
                prompt += `, ${state.bpm} BPM`;
            }
        }
        if (duration > 0) params.textaudioduration = Math.max(1, Math.min(1000, duration));
        if (seed >= 0) params.seed = seed;
        if (tempoHints && TEMPO_PARAM_ENGINES.has(engineId)) {
            params.textaudiobpm = Math.max(10, Math.min(300, Math.round(state.bpm)));
            const beats = String(state.timeSignature[0]);
            if (CORE_BEATS_PER_BAR.includes(beats)) params.textaudiotimesignature = beats;
        }
        return { prompt, params };
    }

    /** First installed music engine in preference order, or null. */
    function preferredMusicEngine() {
        for (const id of MUSIC_ENGINE_ORDER) {
            if (dawSwarmModelFor(id)) return id;
        }
        return null;
    }

    // Params the DAW form sets itself, or that take media the DAW supplies from clips.
    const ENGINE_OPTION_SKIP = new Set(['textaudioduration', 'textaudiostyle', 'textaudiobpm', 'textaudiotimesignature',
        'audioformat', 'audiooutputformat', 'audioquality', 'streamchunksize', 'referenceaudio', 'referencetext']);
    const ENGINE_OPTION_SKIP_TYPES = new Set(['audio', 'video', 'image', 'audio_list', 'video_list', 'image_list', 'model']);

    /** Core param definitions that belong to an engine: any of its feature flags, minus what the DAW drives. */
    function engineOptionParams(engine) {
        if (!engine || typeof gen_param_types === 'undefined' || !gen_param_types) return [];
        const features = new Set(engine.features || []);
        return gen_param_types.filter(p => {
            if (!p.feature_flag || ENGINE_OPTION_SKIP.has(p.id) || ENGINE_OPTION_SKIP_TYPES.has(p.type)) return false;
            return p.feature_flag.split(',').some(f => features.has(f.trim()));
        });
    }

    /**
     * Render an engine's own params with core's widgets (same controls as the Generate tab).
     * @returns {{el: HTMLElement, read: function(): Object}} read() returns the values to send
     */
    function renderEngineOptions(engine, idPrefix) {
        const wrap = createDiv(null, 'daw-gen-options');
        const params = engineOptionParams(engine);
        if (!params.length || typeof getHtmlForParam !== 'function') {
            return { el: wrap, read: () => ({}) };
        }
        const details = document.createElement('details');
        details.className = 'daw-gen-options-box';
        const summary = document.createElement('summary');
        summary.textContent = translate('Engine options') + ` (${params.length})`;
        details.appendChild(summary);
        const body = createDiv(null, 'daw-gen-options-body');
        const runnables = [];
        for (const p of params) {
            const made = getHtmlForParam(p, idPrefix);
            if (!made) continue;
            const cell = createDiv(null, 'daw-gen-option');
            cell.innerHTML = made.html;
            body.appendChild(cell);
            if (made.runnable) runnables.push(made.runnable);
        }
        details.appendChild(body);
        wrap.appendChild(details);
        // Widgets that size themselves need to be in the DOM, which happens once the caller appends wrap.
        setTimeout(() => {
            for (const run of runnables) { try { run(); } catch (_) {} }
            if (typeof applyTranslations === 'function') applyTranslations(body);
        }, 0);
        const read = () => {
            const out = {};
            for (const p of params) {
                const input = document.getElementById(idPrefix + p.id);
                if (!input) continue;
                const toggle = document.getElementById(idPrefix + p.id + '_toggle');
                if (p.toggleable && toggle && !toggle.checked) continue;
                const val = getInputVal(input);
                if (val === null || val === undefined || val === '') continue;
                // An untouched control equals the param default; sending it is harmless but noisy.
                if (`${val}` === `${p.default}`) continue;
                out[p.id] = val;
            }
            return out;
        };
        return { el: wrap, read };
    }

    /** Bottom-panel tab: generate TTS/music/SFX with an installed engine straight into a track. */
    // Generate tab: category cards (TTS / Music / SFX / STT) — each shows only
    // its engines and the params that type actually needs.
    const GEN_CATEGORIES = [
        { id: 'tts', name: 'Text to Speech', desc: 'Speak any text, optionally clone a voice' },
        { id: 'music', name: 'Music', desc: 'Songs and loops from a style prompt + lyrics' },
        { id: 'sfx', name: 'Sound FX', desc: 'One-shots and foley from a description' },
        { id: 'stt', name: 'Speech to Text', desc: 'Transcribe the selected clip' }
        // name/desc are translated where they are rendered (translate() needs to run per language switch).
    ];
    let generateCategory = 'tts';
    const generateEngineChoice = {}; // category -> last engine picked, kept across panel rebuilds
    let generatePlacement = 'new'; // 'new' track or the 'selected' track

    function enginesForCategory(cat) {
        const list = (generateEngines || []).filter(e => e.installed);
        if (cat === 'tts') return list.filter(e => e.category === 'TTS');
        if (cat === 'stt') return list.filter(e => e.category === 'STT' && !NON_SPEECH_STT.has(e.id));
        const gen = list.filter(e => e.category === 'AudioGeneration');
        return cat === 'sfx' ? gen.filter(e => /sfx/i.test(e.id)) : gen.filter(e => !/sfx/i.test(e.id));
    }

    /** Card shown in place of the form when no Audio Backend exists: the model names it needs do not exist yet. */
    function buildBackendNotice(container) {
        const card = createDiv(null, 'daw-fx-card daw-gen-notice');
        const title = createDiv(null, 'daw-fx-card-title');
        title.textContent = translate('No Audio Backend on this server');
        card.appendChild(title);
        const text = createDiv(null, 'daw-stems-desc');
        text.textContent = translate('Generation runs through SwarmUI\'s Audio Backend. Add one under Server > Backends, install engines from its card, then refresh this tab.');
        card.appendChild(text);
        const row = createDiv(null, 'daw-clip-editor-actions');
        quickAppendButton(row, translate('Open Backends'), () => {
            document.getElementById('servertabbutton')?.click();
            setTimeout(() => document.getElementById('serverbackendstabbutton')?.click(), 50);
        }, ' basic-button btn-primary', translate('Go to Server > Backends'));
        quickAppendButton(row, translate('Refresh'), () => renderGeneratePanel(container, { refresh: true }),
            ' basic-button', translate('Check again for an Audio Backend and installed engines'));
        card.appendChild(row);
        return card;
    }

    async function renderGeneratePanel(container, { refresh = false } = {}) {
        container.innerHTML = '';
        const panel = createDiv(null, 'daw-generate-panel');
        container.appendChild(panel);
        const status = createDiv(null, 'daw-stems-desc');
        status.textContent = translate('Loading engines...');
        panel.appendChild(status);
        try {
            await ensureEnginesList(refresh);
        } catch (err) {
            status.textContent = translate('Failed to load engines:') + ' ' + err.message;
            quickAppendButton(panel, translate('Retry'), () => renderGeneratePanel(container, { refresh: true }), ' basic-button btn-sm');
            return;
        }
        status.remove();

        if (generateBackendStatus === 'NOT_FOUND') {
            panel.appendChild(buildBackendNotice(container));
            return;
        }

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
            pick.innerHTML = `<span class="daw-fx-pick-name">${translate(cat.name)}</span>`
                + `<span class="daw-fx-pick-desc">${count ? translate(cat.desc) : translate('No engine installed')}</span>`;
            pick.addEventListener('click', () => {
                if (!count) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate(cat.name) + ': ' + translate('No installed engine. Install one from the Audio Backend card under Server > Backends'), 'notice-pop-yellow');
                    return;
                }
                generateCategory = cat.id;
                renderGeneratePanel(container);
            });
            browser.appendChild(pick);
        }
        panel.appendChild(browser);

        if (generateBackendStatus && generateBackendStatus !== 'RUNNING') {
            const warn = createDiv(null, 'daw-stems-clipinfo daw-gen-warn');
            warn.textContent = translate('The Audio Backend is') + ` ${String(generateBackendStatus).toLowerCase()}. `
                + translate('Generations wait until it is running.');
            panel.appendChild(warn);
        }

        const engines = enginesForCategory(generateCategory);
        if (!engines.length) {
            const none = createDiv(null, 'daw-stems-desc');
            none.textContent = translate('No audio engines installed. Install one from the Audio Backend card under Server > Backends, then refresh.');
            panel.appendChild(none);
            quickAppendButton(panel, translate('Refresh'), () => renderGeneratePanel(container, { refresh: true }), ' basic-button btn-sm');
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
        engineLabel.textContent = translate('Engine:');
        const engineSelect = document.createElement('select');
        engineSelect.className = 'daw-stems-select';
        for (const eng of engines) {
            const opt = document.createElement('option');
            opt.value = eng.id;
            opt.textContent = eng.name;
            engineSelect.appendChild(opt);
        }
        const remembered = generateEngineChoice[generateCategory];
        if (remembered && engines.some(e => e.id === remembered)) engineSelect.value = remembered;
        engineRow.appendChild(engineLabel);
        engineRow.appendChild(engineSelect);
        quickAppendButton(engineRow, '&#x21bb;', () => renderGeneratePanel(container, { refresh: true }),
            ' basic-button btn-sm daw-gen-refresh', translate('Refresh the engine list'));
        mainCol.appendChild(engineRow);

        const currentEngine = () => engines.find(x => x.id === engineSelect.value) || engines[0];

        const modelRow = createDiv(null, 'daw-stems-model-row');
        const modelLabel = document.createElement('label');
        modelLabel.className = 'daw-stems-ctl-label';
        modelLabel.textContent = translate('Model:');
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

        // Engine-specific params (voices, speed, sampling), rebuilt per engine with core's own widgets
        const optionsHost = createDiv(null, 'daw-gen-options-host');
        let engineOptions = { read: () => ({}) };
        function rebuildOptions() {
            optionsHost.innerHTML = '';
            engineOptions = renderEngineOptions(currentEngine(), 'dawgen_');
            optionsHost.appendChild(engineOptions.el);
        }

        // ==== Speech to Text: transcription workflow (produces text, not audio) ====
        if (generateCategory === 'stt') {
            const resultArea = document.createElement('textarea');
            resultArea.className = 'daw-generate-text';
            resultArea.rows = 4;
            resultArea.placeholder = translate('Transcription appears here…');
            mainCol.appendChild(resultArea);
            mainCol.appendChild(optionsHost);

            const actionRow = createDiv(null, 'daw-stems-action-row');
            const hint = createSpan(null, 'daw-stems-clipinfo');
            hint.textContent = translate('Transcribes the selected clip with the chosen engine.');
            actionRow.appendChild(hint);
            sideCol.appendChild(actionRow);
            const goBtn = document.createElement('button');
            goBtn.className = 'basic-button btn-sm daw-stems-go';
            goBtn.textContent = translate('Transcribe Selected Clip');
            goBtn.addEventListener('click', async () => {
                const sel = findClipById(state.selectedClipId);
                if (!sel) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Select a clip to transcribe'), 'notice-pop-yellow');
                    return;
                }
                goBtn.disabled = true;
                const busy = createBusyIndicator(translate('Transcribing…'), 'generate');
                sideCol.appendChild(busy);
                try {
                    const b64 = await AudioLabCore.readAsBase64(await clipAudioBlob(sel.clip));
                    const result = await AudioLabAPI.processSTT(b64, { providerId: currentEngine().id, model: modelSelect.value || null });
                    if (!result.success || !result.transcription) throw new Error(result.error || 'No transcription returned');
                    resultArea.value = result.transcription.trim();
                } catch (err) {
                    console.error('[AudioDaw] Transcription failed:', err);
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Transcription failed:') + ' ' + err.message, 'notice-pop-red');
                } finally {
                    busy.done();
                    goBtn.disabled = false;
                }
            });
            sideCol.appendChild(goBtn);
            const copyRow = createDiv(null, 'daw-stems-model-row');
            const copyBtn = document.createElement('button');
            copyBtn.className = 'basic-button btn-sm';
            copyBtn.textContent = translate('Copy Text');
            copyBtn.addEventListener('click', () => {
                if (!resultArea.value) return;
                navigator.clipboard?.writeText(resultArea.value);
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Copied to clipboard'), 'notice-pop-green');
            });
            copyRow.appendChild(copyBtn);
            sideCol.appendChild(copyRow);
            engineSelect.addEventListener('change', () => {
                generateEngineChoice[generateCategory] = engineSelect.value;
                rebuildModels();
                rebuildOptions();
            });
            rebuildModels();
            rebuildOptions();
            return;
        }

        // ==== TTS / Music / SFX generation form ====
        const isTts = generateCategory === 'tts';
        const isMusicCat = generateCategory === 'music';

        const promptArea = document.createElement('textarea');
        promptArea.className = 'daw-generate-text';
        promptArea.rows = 2;
        promptArea.placeholder = isTts ? translate('Text to speak...')
            : isMusicCat ? translate('Describe the music (style, mood, instruments)...')
            : translate('Describe the sound: "punchy kick drum", "rain on a tin roof"...');
        mainCol.appendChild(promptArea);

        // Lyrics only exist for engines that sing them; the rest would silently ignore the box.
        let lyricsArea = null;
        if (isMusicCat) {
            lyricsArea = document.createElement('textarea');
            lyricsArea.className = 'daw-generate-text';
            lyricsArea.rows = 3;
            lyricsArea.placeholder = translate('Lyrics (optional, leave empty for instrumental)');
            mainCol.appendChild(lyricsArea);
        }
        mainCol.appendChild(optionsHost);

        // Options: duration + seed (music/SFX), tempo hints (music)
        let durationInput = null, seedInput = null, tempoCheck = null;
        if (!isTts) {
            const optsRow = createDiv(null, 'daw-stems-model-row');
            const durLabel = document.createElement('label');
            durLabel.className = 'daw-stems-ctl-label';
            durLabel.textContent = translate('Duration (s):');
            durationInput = document.createElement('input');
            durationInput.type = 'number';
            durationInput.className = 'daw-clip-fade-input';
            durationInput.min = '1';
            durationInput.max = '1000';
            durationInput.value = isMusicCat ? '20' : '3';
            const seedLabel = document.createElement('label');
            seedLabel.className = 'daw-stems-ctl-label';
            seedLabel.textContent = translate('Seed:');
            seedInput = document.createElement('input');
            seedInput.type = 'number';
            seedInput.className = 'daw-clip-fade-input';
            seedInput.value = '-1';
            seedInput.title = translate('-1 = random');
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
                tempoLbl.textContent = translate('Match project tempo') + ` (${state.bpm} BPM, ${state.timeSignature.join('/')})`;
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
            refLabel.textContent = translate('Use selected clip as voice reference');
            refTextInput = document.createElement('input');
            refTextInput.type = 'text';
            refTextInput.className = 'daw-generate-reftext';
            refTextInput.placeholder = translate('Reference transcript (optional)');
            // Auto-fill the transcript by running the selected clip through STT
            const sttBtn = document.createElement('button');
            sttBtn.type = 'button';
            sttBtn.className = 'basic-button btn-sm daw-reftext-stt';
            sttBtn.textContent = translate('Transcribe');
            sttBtn.title = translate('Fill the transcript automatically by running the selected clip through speech-to-text');
            sttBtn.style.display = enginesForCategory('stt').length ? '' : 'none';
            sttBtn.addEventListener('click', async () => {
                const sel = findClipById(state.selectedClipId);
                if (!sel) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Select a clip to transcribe'), 'notice-pop-yellow');
                    return;
                }
                sttBtn.disabled = true;
                sttBtn.textContent = translate('Transcribing…');
                try {
                    const b64 = await AudioLabCore.readAsBase64(await clipAudioBlob(sel.clip));
                    const result = await AudioLabAPI.processSTT(b64, { providerId: enginesForCategory('stt')[0]?.id });
                    if (!result.success || !result.transcription) throw new Error(result.error || 'No transcription returned');
                    refTextInput.value = result.transcription.trim();
                    refCheck.checked = true;
                } catch (err) {
                    console.error('[AudioDaw] Transcription failed:', err);
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Transcription failed:') + ' ' + err.message, 'notice-pop-red');
                } finally {
                    sttBtn.disabled = false;
                    sttBtn.textContent = translate('Transcribe');
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
            refNote.textContent = translate('This engine uses its built-in voice. Voice cloning is not supported.');
            sideCol.appendChild(refNote);
        }

        const supportsVoiceRef = () => (currentEngine()?.features || []).includes('tts_voice_ref');

        // Where the result lands: a new track, or the selected track (at the playhead either way)
        const placeRow = createDiv(null, 'daw-stems-model-row');
        const placeLabel = document.createElement('label');
        placeLabel.className = 'daw-stems-ctl-label';
        placeLabel.textContent = translate('Place on:');
        const placeSelect = document.createElement('select');
        placeSelect.className = 'daw-stems-select';
        for (const [v, l] of [['new', translate('New track')], ['selected', translate('Selected track')]]) {
            const opt = document.createElement('option');
            opt.value = v;
            opt.textContent = l;
            placeSelect.appendChild(opt);
        }
        placeSelect.value = generatePlacement;
        placeSelect.addEventListener('change', () => { generatePlacement = placeSelect.value; });
        placeRow.appendChild(placeLabel);
        placeRow.appendChild(placeSelect);
        sideCol.appendChild(placeRow);

        // Action row: hint above a full-width Generate button
        const actionRow = createDiv(null, 'daw-stems-action-row');
        const hint = createSpan(null, 'daw-stems-clipinfo');
        hint.textContent = translate('Result lands at the playhead (and is saved to your outputs).');
        actionRow.appendChild(hint);
        const goBtn = document.createElement('button');
        goBtn.className = 'basic-button btn-sm daw-stems-go';
        goBtn.textContent = translate('Generate');
        actionRow.appendChild(goBtn);
        sideCol.appendChild(actionRow);

        function refreshVisibility() {
            const eng = currentEngine();
            if (isTts) {
                refRow.style.display = supportsVoiceRef() ? '' : 'none';
                refNote.style.display = supportsVoiceRef() ? 'none' : '';
            }
            if (lyricsArea) lyricsArea.style.display = LYRIC_MUSIC_ENGINES.has(eng?.id) ? '' : 'none';
            rebuildModels();
            rebuildOptions();
        }
        engineSelect.addEventListener('change', () => {
            generateEngineChoice[generateCategory] = engineSelect.value;
            refreshVisibility();
        });
        refreshVisibility();

        goBtn.addEventListener('click', async () => {
            const eng = currentEngine();
            if (!eng) return;
            const models = (eng.models || []).filter(m => m.installed !== false);
            const modelDef = models.find(m => m.id === modelSelect.value) || models[0];
            if (!modelDef?.swarm_model) {
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('No installed model for this engine'), 'notice-pop-yellow');
                return;
            }
            const promptText = promptArea.value.trim();
            if (!promptText) {
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover(isTts ? translate('Enter text to speak first') : translate('Enter a prompt first'), 'notice-pop-yellow');
                }
                return;
            }
            const lyricsText = (isMusicCat && LYRIC_MUSIC_ENGINES.has(eng.id)) ? (lyricsArea?.value.trim() || '') : '';
            // Core T2I params: same pipeline + param ids as the main Generate tab
            let request;
            if (isTts) {
                request = { prompt: promptText, params: {} };
                if (refCheck.checked && supportsVoiceRef()) {
                    const sel = findClipById(state.selectedClipId);
                    if (!sel) {
                        if (typeof doNoticePopover === 'function') doNoticePopover(translate('Select a clip to use as voice reference'), 'notice-pop-yellow');
                        return;
                    }
                    const refBlob = await clipAudioBlob(sel.clip);
                    const b64 = await AudioLabCore.readAsBase64(refBlob);
                    request.params.referenceaudio = `data:${refBlob.type || 'audio/wav'};base64,${b64}`;
                    if (refTextInput.value.trim()) request.params.referencetext = refTextInput.value.trim();
                }
            }
            else {
                const seed = parseInt(seedInput.value);
                request = buildAudioGenRequest(eng.id, {
                    description: promptText,
                    lyrics: lyricsText,
                    duration: parseFloat(durationInput.value) || (isMusicCat ? 20 : 3),
                    seed: isNaN(seed) ? -1 : seed,
                    tempoHints: !!tempoCheck?.checked
                });
            }
            Object.assign(request.params, engineOptions.read());

            // Non-blocking: only the Generate button locks; the rest of the DAW
            // stays usable while the engine works (progress on the tab too).
            goBtn.disabled = true;
            const busy = createBusyIndicator(translate('Generating with') + ` ${eng.name}…`, 'generate');
            sideCol.appendChild(busy);
            try {
                const { blob, metadata } = await dawSwarmGenerate({
                    model: modelDef.swarm_model,
                    prompt: request.prompt,
                    params: request.params,
                    onProgress: (frac) => busy.setProgress(frac)
                });
                pushUndo();
                const target = generatePlacement === 'selected' ? getSelectedTrack() : null;
                const track = target || addTrack({ name: eng.name });
                const label = promptText.slice(0, 28) || eng.name;
                const clip = await addClipToTrack(track, blob, {
                    name: label,
                    startTime: snapTime(state.currentTime),
                    meta: buildClipScoreMeta(metadata, {
                        style: isMusicCat ? promptText : '',
                        lyrics: lyricsText,
                        model: modelDef.swarm_model,
                        engineId: eng.id,
                        label
                    })
                });
                state.selectedTrackId = track.id;
                state.selectedClipId = clip.id;
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Generated clip added (also saved to your outputs)'), 'notice-pop-green');
            } catch (err) {
                console.error('[AudioDaw] Generate failed:', err);
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Generate failed:') + ' ' + err.message, 'notice-pop-red');
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
                scheduleClip(offlineCtx, chain.input, clip,
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

    /**
     * The audio a clip actually plays (its trimmed window) as a Blob. Untrimmed clips hand back their source
     * blob; trimmed or split clips share a source with other clips, so their window is rendered to WAV.
     */
    async function clipAudioBlob(clip) {
        if ((clip.offset || 0) <= 1e-6 && (clip.trimEnd || 0) <= 1e-6) return clip.blob;
        const buf = clip.decodedBuffer || await AudioDawTrack.decodeClip(clip);
        const sr = buf.sampleRate;
        const start = Math.max(0, Math.floor((clip.offset || 0) * sr));
        const visible = clip.duration - (clip.offset || 0) - (clip.trimEnd || 0);
        const len = Math.max(1, Math.min(buf.length - start, Math.round(visible * sr)));
        const out = new AudioBuffer({ numberOfChannels: buf.numberOfChannels, length: len, sampleRate: sr });
        for (let ch = 0; ch < buf.numberOfChannels; ch++) {
            out.copyToChannel(buf.getChannelData(ch).subarray(start, start + len), ch);
        }
        return audioBufferToWav(out);
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
        closeTrackInput(track.id);
        dropChain(track.id);
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
            onArm: (track) => toggleArm(track),
            onMonitor: (track) => toggleMonitor(track),
            onInputMenu: (track, e) => showTrackInputMenu(track, e),
            onOpenFx: (track) => {
                state.selectedTrackId = track.id;
                updateTrackSelection();
                updateBottomPanel();
                switchBottomTab('fx');
            },
            onTrackMenu: (track, e) => showTrackMenu(track, e),
            inputLabel: (track) => deviceLabels.get(track.input?.deviceId) || null,
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
            /** The Score sheet takes a clip dropped on it; its own drag is a pointer gesture, not HTML5 dnd. */
            onClipDragOver: (x, y) => typeof AudioDawScore !== 'undefined' && AudioDawScore.clipDragOver(x, y),
            onClipDropOutside: (clip, track, x, y) =>
                typeof AudioDawScore !== 'undefined' && AudioDawScore.clipDropped(clip, x, y),
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

        for (const [index, track] of state.tracks.entries()) {
            const header = AudioDawTrack.buildTrackHeader(track, trackCallbacks, index);
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
                    opt.textContent = p === 'volume' ? translate('Volume') : translate('Pan');
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
        addBtn.innerHTML = `+ <span>${escapeHtml(translate('Add Track'))}</span>`;
        addBtn.title = translate('Add an empty track');
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
        updateTimeDisplay();
    }

    // ===== PLAYBACK ENGINE (Web Audio API) =====

    function getAudioContext() {
        if (!audioCtx || audioCtx.state === 'closed') {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)({ latencyHint: 'interactive' });
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

    /**
     * Create a track's persistent node chain, in pro signal-flow order:
     * input (clips, live input) -> inserts (pre-fader) -> trackGain (fader, mute/solo) -> volAuto -> trackPan
     * -> masterGain, plus an analyser tap for meters. volAuto carries the volume envelope so live fader moves
     * and automation ramps never fight over one AudioParam.
     */
    function buildTrackChain(ctx, track, masterGain) {
        const input = ctx.createGain();
        const trackGain = ctx.createGain();
        trackGain.gain.value = computeTrackGain(track);
        const fxChain = (typeof AudioDawFx !== 'undefined')
            ? AudioDawFx.buildFxChain(ctx, track.fx, { bpm: state.bpm }) : null;
        if (fxChain) {
            input.connect(fxChain.input);
            fxChain.output.connect(trackGain);
        } else {
            input.connect(trackGain);
        }
        const volAuto = ctx.createGain();
        volAuto.gain.value = 1;
        trackGain.connect(volAuto);
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
        return { input, trackGain, fxChain, volAuto, trackPan, analysers, meterSplitter };
    }

    /** Fully disconnect one track chain (incl. FX feedback loops, which otherwise persist). */
    function disposeChain(chain) {
        try { chain.input.disconnect(); } catch (_) {}
        try { chain.trackGain.disconnect(); } catch (_) {}
        if (typeof AudioDawFx !== 'undefined') AudioDawFx.disposeFxChain(chain.fxChain);
        try { chain.volAuto.disconnect(); } catch (_) {}
        if (chain.trackPan) { try { chain.trackPan.disconnect(); } catch (_) {} }
        if (chain.meterSplitter) { try { chain.meterSplitter.disconnect(); } catch (_) {} }
        for (const a of chain.analysers || []) { try { a.disconnect(); } catch (_) {} }
    }

    // ===== MIXER GRAPH =====
    // Lives while the DAW is open, independent of the transport: a master bus plus one chain per track.
    // Playback only schedules sources into chain inputs, so monitored inputs keep sounding (and meters keep
    // moving) while stopped, and effect tails ring out after Stop the way a hardware desk does.

    let mixer = null; // { ctx, masterGain, masterLimiter, masterSplitter, masterAnalysers, clickBus, chains: Map }

    function ensureMixer() {
        const ctx = getAudioContext();
        if (mixer && mixer.ctx === ctx) return mixer;
        const masterGain = ctx.createGain();
        masterGain.gain.value = state.masterVolume;
        const masterSplitter = ctx.createChannelSplitter(2);
        const masterAnalysers = [ctx.createAnalyser(), ctx.createAnalyser()];
        masterAnalysers.forEach((a, i) => { a.fftSize = 512; masterSplitter.connect(a, i); });
        // The metronome goes straight to the speakers: never through the master, never into an export
        const clickBus = ctx.createGain();
        clickBus.gain.value = recordSettings.clickVolume;
        clickBus.connect(ctx.destination);
        mixer = { ctx, masterGain, masterLimiter: null, masterSplitter, masterAnalysers, clickBus, chains: new Map() };
        wireMasterOut();
        return mixer;
    }

    /** (Re)wire master -> optional limiter -> speakers and meter tap. Runs at creation and on a limiter toggle. */
    function wireMasterOut() {
        const m = mixer;
        try { m.masterGain.disconnect(); } catch (_) {}
        if (m.masterLimiter) {
            try { m.masterLimiter.disconnect(); } catch (_) {}
            m.masterLimiter = null;
        }
        let out = m.masterGain;
        if (state.masterLimiterEnabled && typeof AudioDawFx !== 'undefined') {
            m.masterLimiter = AudioDawFx.buildMasterLimiter(m.ctx);
            m.masterGain.connect(m.masterLimiter);
            out = m.masterLimiter;
        }
        out.connect(m.ctx.destination);
        out.connect(m.masterSplitter);
    }

    /** A track's chain, built on first use. A new chain picks up the track's live input if it is monitored. */
    function chainFor(track) {
        const m = ensureMixer();
        let chain = m.chains.get(track.id);
        if (!chain) {
            chain = buildTrackChain(m.ctx, track, m.masterGain);
            m.chains.set(track.id, chain);
            if (!playback) holdTrackAutomation(chain, track, state.currentTime);
            const entry = trackInputs.get(track.id);
            if (entry) applyMonitor(track, entry);
        }
        return chain;
    }

    function dropChain(trackId) {
        const chain = mixer?.chains.get(trackId);
        if (!chain) return;
        const entry = trackInputs.get(trackId);
        if (entry?.monitorChain === chain) {
            try { entry.route.output.disconnect(chain.input); } catch (_) {}
            entry.monitorChain = null;
        }
        disposeChain(chain);
        mixer.chains.delete(trackId);
    }

    /** Drop chains whose track is gone. */
    function pruneChains() {
        if (!mixer) return;
        const live = new Set(state.tracks.map(t => t.id));
        for (const id of [...mixer.chains.keys()]) {
            if (!live.has(id)) dropChain(id);
        }
    }

    function destroyMixer() {
        if (!mixer) return;
        for (const id of [...mixer.chains.keys()]) dropChain(id);
        for (const node of [mixer.masterGain, mixer.masterLimiter, mixer.masterSplitter, mixer.clickBus, ...mixer.masterAnalysers]) {
            if (node) { try { node.disconnect(); } catch (_) {} }
        }
        mixer = null;
    }

    /** Topology change on a track's FX (add/remove/reorder): rebuild its chain live. */
    function rebuildTrackFx(track) {
        if (!mixer) return;
        dropChain(track.id);
        if (playback) resyncPlayback(); // recreates the chain while rescheduling
        else if (trackInputs.has(track.id)) chainFor(track); // a monitored input needs its chain back now
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

    /** Stopped transport: hold each envelope at its value under the playhead, so monitoring hears that level. */
    function holdTrackAutomation(chain, track, at) {
        const now = chain.volAuto.context.currentTime;
        const hasFx = typeof AudioDawFx !== 'undefined';
        const vol = track.automation?.volume;
        try { chain.volAuto.gain.cancelScheduledValues(now); } catch (_) {}
        chain.volAuto.gain.setValueAtTime(vol?.length && hasFx ? AudioDawFx.envelopeValueAt(vol, at) : 1, now);
        if (chain.trackPan) {
            const pan = track.automation?.pan;
            const base = track.pan || 0;
            try { chain.trackPan.pan.cancelScheduledValues(now); } catch (_) {}
            chain.trackPan.pan.setValueAtTime(pan?.length && hasFx
                ? Math.max(-1, Math.min(1, base + AudioDawFx.envelopeValueAt(pan, at))) : base, now);
        }
    }

    /** Live envelope edit: re-anchor + re-ramp this track's automation from the current position. */
    function rescheduleTrackAutomation(track) {
        if (typeof AudioDawFx === 'undefined') return;
        const chain = mixer?.chains.get(track.id);
        if (!chain) return;
        if (!playback) {
            holdTrackAutomation(chain, track, state.currentTime);
            return;
        }
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

    // ===== LIVE INPUTS (arm + monitor) =====
    // A track's input is open while the track is armed (R) or monitoring (I): the device stream (shared per
    // device), a channel route, and a capture node that always meters the input and records only while the
    // transport records. Monitoring connects the route into the track's own chain, so the input is heard
    // through the track's inserts, fader and pan, like a channel on a desk.

    const trackInputs = new Map(); // trackId -> { key, handle, route, capture, latency, label, level, channels, monitorChain }
    const inputPending = new Map(); // trackId -> Promise, so rapid toggles apply in order

    function inputKey(track) {
        const inp = track.input || {};
        return `${inp.deviceId || ''}|${inp.channels ?? 'mono'}|${recordSettings.voiceMode ? 1 : 0}`;
    }

    /** Open, re-route or close a track's input to match its arm/monitor state. Serialized per track. */
    function syncTrackInput(track) {
        const prev = inputPending.get(track.id) || Promise.resolve();
        const next = prev.catch(() => {}).then(() => syncTrackInputNow(track));
        inputPending.set(track.id, next);
        next.catch(() => {}).finally(() => {
            if (inputPending.get(track.id) === next) inputPending.delete(track.id);
        });
        return next;
    }

    async function syncTrackInputNow(track) {
        if (!state || !state.tracks.includes(track) || !(track.armed || track.monitor)) {
            closeTrackInput(track.id);
            return;
        }
        let entry = trackInputs.get(track.id);
        const key = inputKey(track);
        if (entry && entry.key !== key) {
            closeTrackInput(track.id);
            entry = null;
        }
        if (!entry) {
            const support = AudioDawInput.support();
            if (!support.mic) throw new Error(translate('Audio input needs HTTPS (or localhost) in this browser'));
            if (!support.worklet) throw new Error(translate('This browser cannot capture audio (no AudioWorklet)'));
            const ctx = getAudioContext();
            if (ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
            ensureMixer();
            const handle = await AudioDawInput.openStream(track.input?.deviceId || null, { voiceMode: recordSettings.voiceMode });
            let route = null, capture = null;
            try {
                route = AudioDawInput.createRoute(ctx, handle.stream, track.input?.channels ?? 'mono');
                capture = await AudioDawInput.createCapture(ctx, route.output, route.captureChannels);
            }
            catch (err) {
                route?.dispose();
                handle.release();
                throw err;
            }
            // The track may have been disarmed or removed while the device opened
            if (!state || !state.tracks.includes(track) || !(track.armed || track.monitor)) {
                capture.dispose();
                route.dispose();
                handle.release();
                return;
            }
            entry = {
                key, handle, route, capture, level: 0, monitorChain: null,
                channels: route.captureChannels,
                latency: AudioDawInput.streamLatency(handle.stream),
                label: AudioDawInput.streamLabel(handle.stream)
            };
            capture.onPeak = (v) => {
                entry.level = v;
                if (v >= 0.985) entry.clipped = true;
            };
            trackInputs.set(track.id, entry);
            kickMeters();
        }
        applyMonitor(track, entry);
        AudioDawTrack.syncHeaderControls(track);
    }

    /** Connect or disconnect a live input to its track's chain, following the track's I button. */
    function applyMonitor(track, entry) {
        const want = track.monitor ? chainFor(track) : null;
        if (entry.monitorChain && entry.monitorChain !== want) {
            try { entry.route.output.disconnect(entry.monitorChain.input); } catch (_) {}
            entry.monitorChain = null;
        }
        if (want && entry.monitorChain !== want) {
            entry.route.output.connect(want.input);
            entry.monitorChain = want;
        }
    }

    function closeTrackInput(trackId) {
        const entry = trackInputs.get(trackId);
        if (!entry) return;
        trackInputs.delete(trackId);
        if (entry.monitorChain) {
            try { entry.route.output.disconnect(entry.monitorChain.input); } catch (_) {}
        }
        entry.capture.dispose();
        entry.route.dispose();
        entry.handle.release();
    }

    function closeAllInputs() {
        for (const id of [...trackInputs.keys()]) closeTrackInput(id);
    }

    function reportInputError(track, err) {
        console.error('[AudioDaw] Input failed for', track?.name, err);
        if (typeof doNoticePopover === 'function') {
            doNoticePopover(`${track?.name || translate('Track')}: ` + translate('input failed:') + ' ' + (err?.message || err), 'notice-pop-red');
        }
    }

    /** Re-open every live input, e.g. after the voice-processing setting changed. */
    function resyncAllInputs() {
        for (const t of state.tracks) {
            if (t.armed || t.monitor) syncTrackInput(t).catch(err => reportInputError(t, err));
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

    /**
     * Push live levels + peak-hold ticks into header meters, mixer strips and the master meter.
     * An armed track shows its INPUT level unless it is playing back (what a record-armed channel shows).
     * @returns {boolean} whether any meter is still showing signal
     */
    function updateMeters() {
        if (!mixer || !state) return false;
        let active = false;
        for (const track of state.tracks) {
            const input = trackInputs.get(track.id);
            let peaks;
            if (input && track.armed && (!state.isPlaying || recording)) {
                peaks = [input.level, input.level];
                track.headerEl?.classList.toggle('input-clipped', !!input.clipped);
            }
            else {
                const chain = mixer.chains.get(track.id);
                peaks = chain?.analysers ? readChanPeaks(chain.analysers) : [0, 0];
            }
            const strip = AudioDawMixer.getMeterEl?.(track.id);
            for (let ch = 0; ch < 2; ch++) {
                const pct = levelToPct(peaks[ch]);
                const held = heldPeak(`${track.id}:${ch}`, pct);
                if (pct > 0 || held > 0) active = true;
                setMeterChan(track.meterChans?.[ch], pct, held, false);
                setMeterChan(strip?.chans?.[ch], pct, held, true);
            }
        }
        const peaks = readChanPeaks(mixer.masterAnalysers);
        const strip = AudioDawMixer.getMeterEl?.('__master__');
        for (let ch = 0; ch < 2; ch++) {
            const pct = levelToPct(peaks[ch]);
            const held = heldPeak(`__master__:${ch}`, pct);
            if (pct > 0 || held > 0) active = true;
            setMeterChan(strip?.chans?.[ch], pct, held, true);
        }
        // ~Loudness readout (slow RMS EMA; not true K-weighted LUFS but tracks it usefully)
        const rms = readRms(mixer.masterAnalysers[0]);
        loudnessEma = loudnessEma * 0.95 + rms * 0.05;
        const lufsEl = document.querySelector('.daw-master-lufs');
        if (lufsEl) {
            const db = loudnessEma > 1e-5 ? (20 * Math.log10(loudnessEma)).toFixed(1) : '-∞';
            lufsEl.textContent = db + ' LU';
            lufsEl.title = translate('Approximate loudness (RMS). Streaming targets sit around -14.');
        }
        // Clip latch: lights when the master pins, click to reset
        if (Math.max(peaks[0], peaks[1]) >= 0.985) {
            const clipDot = document.querySelector('.daw-master-clip');
            if (clipDot) clipDot.classList.add('lit');
        }
        return active;
    }

    /** Zero out every meter and held peak. */
    function resetMeters() {
        meterPeaks.clear();
        for (const track of state?.tracks || []) {
            for (const chan of track.meterChans || []) setMeterChan(chan, 0, 0, false);
        }
        if (AudioDawMixer.resetMeters) AudioDawMixer.resetMeters();
    }

    // Meters run on their own rAF loop: while playing, while any input is live, and for a moment after both
    // stop so held peaks and effect tails fall away instead of freezing.
    let meterRaf = null;
    let meterBusyAt = 0;

    function kickMeters() {
        meterBusyAt = performance.now();
        if (!meterRaf) meterRaf = requestAnimationFrame(meterLoop);
    }

    function meterLoop() {
        meterRaf = null;
        if (!state || !mixer) return;
        const active = updateMeters();
        const now = performance.now();
        if (active || state.isPlaying || trackInputs.size) meterBusyAt = now;
        if (now - meterBusyAt < 1500) {
            meterRaf = requestAnimationFrame(meterLoop);
        }
        else {
            resetMeters();
        }
    }

    /**
     * Schedule one clip into a context — the single source of truth for placement
     * math, used identically by live playback, loop iterations, and offline export.
     * Node chain per clip: source -> fadeGain (fade ramps only) -> clipGain (mute/gain) -> destNode (track input).
     * @param {BaseAudioContext} ctx
     * @param {AudioNode} destNode - the track chain's input (ahead of the inserts)
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
     * ALL clips are scheduled (muted ones at gain 0), so mute/solo/unmute during
     * playback is always a pure AudioParam write, never a missing source.
     */
    function scheduleTransportFrom(P, { t0Ctx, windowEnd = Infinity, iteration = 0 }) {
        const ctx = playback.ctx;
        for (const track of state.tracks) {
            const chain = chainFor(track);
            scheduleTrackAutomation(chain, track, { t0Ctx, t0Timeline: P, windowEnd, fresh: iteration === 0 });
            for (const clip of track.clips) {
                const nodes = scheduleClip(ctx, chain.input, clip, { t0Ctx, t0Timeline: P, windowEnd });
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

    /** Param-only update of the whole gain topology (mute/solo/volume/pan/master), live or stopped. */
    function updatePlaybackGains() {
        if (!mixer) return;
        const now = mixer.ctx.currentTime;
        mixer.masterGain.gain.setTargetAtTime(state.masterVolume, now, 0.015);
        for (const track of state.tracks) {
            const chain = mixer.chains.get(track.id);
            if (!chain) continue;
            chain.trackGain.gain.setTargetAtTime(computeTrackGain(track), now, 0.015);
            const panEnv = track.automation?.pan;
            if (chain.trackPan && !(panEnv?.length)) {
                chain.trackPan.pan.setTargetAtTime(track.pan || 0, now, 0.015);
            }
            else if (chain.trackPan && panEnv?.length && typeof AudioDawFx !== 'undefined') {
                if (!playback) {
                    holdTrackAutomation(chain, track, state.currentTime);
                    continue;
                }
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
        // First enable with no region set: the selection if there is one, else the whole arrangement
        if (state.loopEnabled && state.loopEnd <= state.loopStart) {
            const sel = state.selection;
            if (sel && sel.end > sel.start) {
                state.loopStart = sel.start;
                state.loopEnd = sel.end;
            }
            else {
                state.loopStart = 0;
                state.loopEnd = state.contentDuration || state.totalDuration;
            }
        }
        const btn = transportEl?.querySelector('.daw-btn-loop');
        if (btn) btn.classList.toggle('active', state.loopEnabled);
        if (timeline) timeline.setLoop(state.loopEnabled, state.loopStart, state.loopEnd);
        updateLoopShade();
        resyncPlayback();
    }

    /**
     * Start the transport at state.currentTime.
     * @param {Object} [opts] - { delay: seconds before the timeline starts moving (count-in), from: timeline
     *   position override }
     * @returns {Promise<Object|null>} the playback session, or null when stopped before it began
     */
    async function startPlayback({ delay = 0 } = {}) {
        if (state.isPlaying) return playback;
        state.isPlaying = true;
        updatePlayButton(true);

        const ctx = getAudioContext();
        if (ctx.state === 'suspended') {
            try { await ctx.resume(); } catch (_) {}
        }
        if (!state.isPlaying) return null; // user hit stop while we awaited the resume
        ensureMixer();

        const P = state.currentTime;
        const t0Ctx = ctx.currentTime + START_EPSILON + delay;
        // Loop is suppressed while recording (a take shouldn't wrap onto itself)
        const loopArmed = !recording && state.loopEnabled && state.loopEnd > state.loopStart && P < state.loopEnd;
        playback = {
            ctx,
            liveClips: [],
            clicks: [],
            clickSeen: null,
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
        schedulerTick(); // first clicks now, not a tick later
        animatePlayhead();
        kickMeters();
        return playback;
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
        } else if (recording?.punch && !recording.stopping
            && currentTimelineTime() >= recording.punch.out + recording.postRoll) {
            // Punch recording runs out its post-roll, then stops itself
            stopRecordingFlow();
            return;
        } else if (!recording && currentTimelineTime() >= playback.endTime) {
            // While recording, playback runs past the end so the take can extend the song
            stopPlayback();
            state.currentTime = 0;
            updatePlayheadPosition();
            updateTimeDisplay();
            return;
        }
        scheduleClicks();
    }

    /**
     * Metronome: schedule clicks for beats inside the next ~200 ms of audio time. Beats count from timeline
     * zero, accented on each bar's downbeat. With a loop armed the next pass is mapped onto its own start, so
     * the first click after a wrap is on time; instants already scheduled are skipped.
     */
    function scheduleClicks() {
        if (!playback || !mixer || !state.metronome) return;
        const ctx = playback.ctx;
        const beat = 60 / state.bpm;
        const perBar = state.timeSignature[0] || 4;
        const now = ctx.currentTime;
        const horizon = now + 0.2;
        const passes = [{ baseCtx: playback.baseCtxTime, baseTl: playback.baseTimelineTime, end: playback.loop ? state.loopEnd : Infinity }];
        if (playback.loop) passes.push({ baseCtx: playback.loop.nextWrapCtxTime, baseTl: state.loopStart, end: state.loopEnd });
        if (!playback.clickSeen) playback.clickSeen = new Set();
        for (const pass of passes) {
            const tlFrom = pass.baseTl + Math.max(0, now - pass.baseCtx);
            const tlTo = Math.min(pass.end, pass.baseTl + (horizon - pass.baseCtx));
            for (let k = Math.ceil((tlFrom - 1e-6) / beat); k * beat <= tlTo + 1e-9 && k * beat < pass.end - 1e-6; k++) {
                const when = pass.baseCtx + (k * beat - pass.baseTl);
                if (when < now - 0.005) continue;
                const key = Math.round(when * 1000);
                if (playback.clickSeen.has(key)) continue;
                playback.clickSeen.add(key);
                playback.clicks.push(AudioDawInput.scheduleClick(ctx, mixer.clickBus, when, k % perBar === 0));
            }
        }
        if (playback.clickSeen.size > 256) {
            for (const key of playback.clickSeen) {
                if (key < (now - 1) * 1000) playback.clickSeen.delete(key);
            }
        }
        if (playback.clicks.length > 64) playback.clicks.splice(0, playback.clicks.length - 64);
    }

    /** Stop every scheduled source and pending click; the mixer graph stays up. */
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
        for (const osc of playback.clicks || []) {
            try { osc.stop(); } catch (_) {}
        }
        playback = null;
        if (mixer && state) {
            for (const track of state.tracks) {
                const chain = mixer.chains.get(track.id);
                if (chain) holdTrackAutomation(chain, track, state.currentTime);
            }
        }
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
        kickMeters(); // let tails and held peaks fall
    }

    /**
     * Re-schedule playback from the current position after a topology change
     * (clip add/move/delete/trim, track add/remove, undo/redo, loop change).
     * Keeps chains for surviving tracks, the scheduler, and the rAF loop:
     * no play-button churn, no UI flicker. No-op when stopped.
     */
    function resyncPlayback() {
        pruneChains();
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
        for (const osc of playback.clicks) {
            try { osc.stop(); } catch (_) {}
        }
        playback.clicks = [];
        playback.clickSeen = null;
        const t0Ctx = ctx.currentTime + START_EPSILON;
        playback.baseCtxTime = t0Ctx;
        playback.baseTimelineTime = P;
        playback.endTime = P < state.contentDuration ? state.contentDuration : state.totalDuration;
        const loopArmed = !recording && state.loopEnabled && state.loopEnd > state.loopStart && P < state.loopEnd;
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
        if (!state.isPlaying && mixer) {
            for (const track of state.tracks) {
                const chain = mixer.chains.get(track.id);
                if (chain) holdTrackAutomation(chain, track, state.currentTime);
            }
        }
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
            updateRecordingPlaceholders();
            if (state.currentTime + 5 > state.totalDuration) {
                state.totalDuration = state.currentTime + 5;
                if (timeline) timeline.setDuration(state.totalDuration);
                updateClipLanesWidth();
            }
        }
        updatePlayheadPosition();
        updateTimeDisplay();
        // Follow the transport on the staff, when the selected clip is the one the open score produced.
        if (typeof AudioDawScore !== 'undefined') AudioDawScore.syncTime(state.currentTime);
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
        const current = durationStringifyColons2(state.currentTime);
        const total = durationStringifyColons2(state.totalDuration);
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
        const visible = (state.loopEnabled || state.punchEnabled) && state.loopEnd > state.loopStart;
        shade.style.display = visible ? '' : 'none';
        shade.classList.toggle('punch', !!state.punchEnabled);
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
            const overlay = showDawLoadingOverlay(translate('Importing audio...'));
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
     * items: [{ label, action, checked?, disabled?, hint? } | { heading }]: `checked` renders a ✓ column,
     * and a heading item is an unclickable section title.
     * Auto-clamps to the viewport; closes on outside click. Returns the menu element.
     */
    function dawMenu(e, items) {
        const existing = document.querySelector('.daw-context-menu');
        if (existing) existing.remove();
        const menu = createDiv(null, 'daw-context-menu');
        menu.style.position = 'fixed';
        menu.style.zIndex = '99999';
        for (const item of items) {
            if (item.heading !== undefined) {
                const head = createDiv(null, 'daw-context-heading');
                head.textContent = item.heading;
                menu.appendChild(head);
                continue;
            }
            const btn = document.createElement('button');
            btn.className = 'daw-context-item';
            if (item.hint) btn.title = item.hint;
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
                    const overlay = showDawLoadingOverlay(translate('Loading output...'));
                    try {
                        // ListImages paths are relative to the user's output root.
                        const prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
                        const url = f.startsWith('View/') || f.startsWith('Output/') ? '/' + f : `/${prefix}/${f}`;
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
                        if (typeof doNoticePopover === 'function') doNoticePopover(translate('Failed to load output:') + ' ' + err.message, 'notice-pop-red');
                    }
                    hideDawLoadingOverlay(overlay);
                }
            })));
        } catch (err) {
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Failed to list outputs:') + ' ' + err.message, 'notice-pop-red');
        }
    }

    // ===== TRACK ARM / MONITOR / INPUT MENUS =====

    const deviceLabels = new Map(); // deviceId -> label, filled whenever the device list is read
    let monitorHintShown = false;

    async function refreshDeviceLabels() {
        const devices = await AudioDawInput.listDevices();
        for (const d of devices) {
            if (d.label) deviceLabels.set(d.deviceId, d.label);
        }
        return devices;
    }

    /** R: arm or disarm a track. Arming opens and meters its input. */
    function toggleArm(track) {
        if (recording) {
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Stop recording before changing which tracks are armed'), 'notice-pop-yellow');
            return;
        }
        track.armed = !track.armed;
        AudioDawTrack.syncHeaderControls(track);
        syncTrackInput(track).then(() => refreshDeviceLabels().then(() => AudioDawTrack.syncHeaderControls(track)))
            .catch(err => {
                track.armed = false;
                AudioDawTrack.syncHeaderControls(track);
                reportInputError(track, err);
            });
    }

    /** I: hear the live input through the track (inserts, fader, pan). */
    function toggleMonitor(track) {
        track.monitor = !track.monitor;
        if (track.monitor && !monitorHintShown) {
            monitorHintShown = true;
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Input monitoring is on. Use headphones: speakers feed back into a microphone'), 'notice-pop-blue');
        }
        AudioDawTrack.syncHeaderControls(track);
        syncTrackInput(track).catch(err => {
            track.monitor = false;
            AudioDawTrack.syncHeaderControls(track);
            reportInputError(track, err);
        });
    }

    /** A track's input: device, channels, arm and monitor. */
    async function showTrackInputMenu(track, e) {
        const items = [];
        const support = AudioDawInput.support();
        if (!support.mic) {
            dawMenu(e, [{ label: translate('Audio input needs HTTPS (or localhost)'), disabled: true }]);
            return;
        }
        if (recording && track.armed) {
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Stop recording before changing this track\'s input'), 'notice-pop-yellow');
            return;
        }
        const devices = await refreshDeviceLabels();
        const labelsHidden = !devices.length || devices.every(d => !d.label);
        const setInput = (patch) => {
            track.input = { deviceId: track.input?.deviceId || null, channels: track.input?.channels ?? 'mono', ...patch };
            AudioDawTrack.syncHeaderControls(track);
            if (track.armed || track.monitor) syncTrackInput(track).catch(err => reportInputError(track, err));
        };
        items.push({ heading: translate('Input device') });
        if (labelsHidden) {
            items.push({
                label: translate('Allow microphone access to list inputs…'),
                action: async () => {
                    try {
                        await AudioDawInput.requestAccess();
                        showTrackInputMenu(track, e);
                    }
                    catch (err) {
                        reportInputError(track, err);
                    }
                }
            });
        }
        items.push({ label: translate('System default input'), checked: !track.input?.deviceId, action: () => setInput({ deviceId: null }) });
        if (!labelsHidden) {
            devices.forEach((d, i) => items.push({
                label: d.label || (translate('Input') + ` ${i + 1}`),
                checked: track.input?.deviceId === d.deviceId,
                action: () => setInput({ deviceId: d.deviceId })
            }));
        }
        const open = trackInputs.get(track.id);
        const available = open ? AudioDawInput.streamChannels(open.handle.stream) : 2;
        const ch = track.input?.channels ?? 'mono';
        items.push({ heading: translate('Channels') });
        items.push({ label: translate('Mono (all channels summed)'), checked: ch === 'mono', action: () => setInput({ channels: 'mono' }) });
        items.push({ label: translate('Stereo (1 + 2)'), checked: ch === 'stereo', action: () => setInput({ channels: 'stereo' }) });
        for (let n = 1; n <= Math.max(2, available); n++) {
            items.push({ label: `${translate('Input')} ${n} ${translate('only')}`, checked: ch === n, action: () => setInput({ channels: n }) });
        }
        items.push({ heading: translate('This track') });
        items.push({ label: translate('Record arm (Shift+R)'), checked: !!track.armed, action: () => toggleArm(track) });
        items.push({ label: translate('Monitor input (I)'), checked: !!track.monitor, action: () => toggleMonitor(track) });
        if (open) {
            items.push({
                label: `${open.label || translate('Input')}: ${open.channels === 2 ? translate('stereo') : translate('mono')}, ${Math.round(open.latency * 1000)} ms ${translate('input latency')}`,
                disabled: true
            });
        }
        dawMenu(e, items);
    }

    /** Right-click on a track header. */
    function showTrackMenu(track, e) {
        const heights = [[translate('Small'), 64], [translate('Medium'), 100], [translate('Large'), 160]];
        dawMenu(e, [
            {
                label: translate('Rename…'),
                action: () => {
                    const name = prompt(translate('Track name:'), track.name);
                    if (!name || !name.trim() || name.trim() === track.name) return;
                    pushUndo();
                    track.name = name.trim();
                    renderAllTracks();
                    updateBottomPanel();
                }
            },
            { label: translate('Duplicate track'), action: () => duplicateTrack(track) },
            { label: translate('Color…'), action: () => showTrackColorMenu(track, e) },
            { label: translate('Input…'), action: () => showTrackInputMenu(track, e) },
            { heading: translate('Height') },
            ...heights.map(([label, h]) => ({ label, checked: track.height === h, action: () => setTrackHeight(track, h) })),
            { heading: '' },
            {
                label: translate('Delete track'),
                disabled: state.tracks.length <= 1,
                action: () => {
                    pushUndo();
                    removeTrack(track.id);
                    updateTotalDuration();
                    renderAllTracks();
                    updateBottomPanel();
                    resyncPlayback();
                }
            }
        ]);
    }

    /** Waveforms are drawn at the lane height, so a height change re-creates the track's clip views. */
    function setTrackHeight(track, h) {
        track.height = h;
        for (const [, entry] of track.clipElements) {
            if (entry.ws) entry.ws.destroy();
            entry.el.remove();
        }
        track.clipElements.clear();
        renderAllTracks();
    }

    /** A copy of a track right below it: same clips (shared audio), inserts, automation and mix. */
    function duplicateTrack(track) {
        pushUndo();
        const ts = AudioDawTrack.serializeTrack(track);
        const copy = AudioDawTrack.createTrack({ name: nextCopyName(track.name), color: track.color, height: track.height });
        copy.volume = ts.volume;
        copy.pan = ts.pan;
        copy.muted = ts.muted;
        copy.input = { ...ts.input };
        copy.fx = ts.fx;
        copy.automation = ts.automation;
        copy.automationVisible = ts.automationVisible;
        copy.automationParam = ts.automationParam;
        for (const c of track.clips) {
            const nc = AudioDawTrack.createClip(c.blob, { name: c.name, startTime: c.startTime, color: c.color, blobKey: c.blobKey });
            nc.decodedBuffer = c.decodedBuffer;
            nc.duration = c.duration;
            nc.offset = c.offset;
            nc.trimEnd = c.trimEnd;
            nc.gain = c.gain;
            nc.fadeIn = c.fadeIn;
            nc.fadeOut = c.fadeOut;
            nc.muted = c.muted;
            copy.clips.push(nc);
        }
        state.tracks.splice(state.tracks.indexOf(track) + 1, 0, copy);
        state.selectedTrackId = copy.id;
        renderAllTracks();
        updateBottomPanel();
        resyncPlayback();
    }

    /** CLICK: metronome on/off. Turning it off silences clicks already queued. */
    function toggleMetronome() {
        state.metronome = !state.metronome;
        transportEl?.querySelector('.daw-btn-click')?.classList.toggle('active', state.metronome);
        if (!state.metronome && playback) {
            for (const osc of playback.clicks) {
                try { osc.stop(); } catch (_) {}
            }
            playback.clicks = [];
            playback.clickSeen = null;
        }
    }

    /** PUNCH: Record replaces only the loop range (set by the loop handles, or the selection). */
    function togglePunch() {
        state.punchEnabled = !state.punchEnabled;
        if (state.punchEnabled && !(state.loopEnd > state.loopStart)) {
            const sel = state.selection;
            if (sel && sel.end > sel.start) {
                state.loopStart = sel.start;
                state.loopEnd = sel.end;
                if (timeline) timeline.setLoop(state.loopEnabled, state.loopStart, state.loopEnd);
            }
            else if (typeof doNoticePopover === 'function') {
                doNoticePopover(translate('Punch records over the loop range. Turn on LOOP and drag its handles, or select a range first'), 'notice-pop-yellow');
            }
        }
        transportEl?.querySelector('.daw-btn-punch')?.classList.toggle('active', state.punchEnabled);
        if (timeline?.setPunch) timeline.setPunch(state.punchEnabled);
        updateLoopShade();
    }

    // ===== CLIP CONTEXT MENU =====

    function showClipContextMenu(e, clip, track) {
        dawMenu(e, [
            { label: translate('Split at Playhead'), action: () => doSplitClip(clip, track) },
            { label: translate('Duplicate'), action: () => doDuplicateClip(clip, track) },
            { label: translate('Delete'), action: () => doDeleteClip(clip, track) },
            { label: clip.muted ? translate('Unmute Clip') : translate('Mute Clip'), action: () => {
                clip.muted = !clip.muted;
                applyClipGain(clip);
                renderAllTracks();
            }},
            { label: translate('Separate Stems… (Demucs)'), action: () => openStemsForClip(clip, track) },
            { label: translate('Conform to') + ` ${state.bpm} BPM…`, action: () => conformClipToBpm(clip, track) }
        ]);
    }

    // ===== CLIP OPERATIONS =====

    /**
     * Split a clip at the playhead without touching audio: both halves keep the shared source blob and differ
     * only in their window (offset/trimEnd), so the split is instant, lossless, and undo is a plain snapshot.
     */
    function doSplitClip(clip, track, at = state.currentTime) {
        const splitTime = at - clip.startTime;
        const visibleDur = clip.duration - clip.offset - clip.trimEnd;
        if (splitTime <= 0.001 || splitTime >= visibleDur - 0.001) {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(translate('Move the playhead over the clip to split it'), 'notice-pop-yellow');
            }
            return null;
        }
        const idx = track.clips.indexOf(clip);
        if (idx === -1) return null;
        pushUndo();
        const pieces = splitClipAt(clip, splitTime);
        track.clips.splice(idx, 1, ...pieces);
        const oldEntry = track.clipElements.get(clip.id);
        if (oldEntry?.ws) oldEntry.ws.destroy();
        if (oldEntry?.el) oldEntry.el.remove();
        track.clipElements.delete(clip.id);
        state.selectedClipId = pieces[1].id;
        updateTotalDuration();
        renderAllTracks();
        updateBottomPanel();
        resyncPlayback();
        return pieces;
    }

    /** Two clips covering `clip` split `splitTime` seconds into its visible window (no undo, no render). */
    function splitClipAt(clip, splitTime) {
        const base = { color: clip.color, blobKey: clip.blobKey };
        const a = AudioDawTrack.createClip(clip.blob, { ...base, name: clip.name, startTime: clip.startTime, meta: clip.meta });
        const b = AudioDawTrack.createClip(clip.blob, { ...base, name: clip.name, startTime: clip.startTime + splitTime });
        for (const c of [a, b]) {
            c.decodedBuffer = clip.decodedBuffer;
            c.duration = clip.duration;
            c.gain = clip.gain;
            c.muted = clip.muted;
        }
        a.offset = clip.offset;
        a.trimEnd = clip.duration - clip.offset - splitTime;
        a.fadeIn = Math.min(clip.fadeIn || 0, splitTime);
        b.offset = clip.offset + splitTime;
        b.trimEnd = clip.trimEnd;
        b.fadeOut = Math.min(clip.fadeOut || 0, clip.duration - b.offset - b.trimEnd);
        return [a, b];
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
                doNoticePopover(translate('Duplicate failed:') + ' ' + err.message, 'notice-pop-red');
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
                doNoticePopover(translate('Delete failed:') + ' ' + err.message, 'notice-pop-red');
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
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Demucs install failed:') + ' ' + data.error, 'notice-pop-red');
                    resolve(false);
                }
            }, 0, e => {
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Demucs install failed:') + ' ' + e, 'notice-pop-red');
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

    function showDawLoadingOverlay(message = translate('Processing...')) {
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
        const busy = createBusyIndicator(translate('Separating stems…'), 'stems');
        bottomPanelEl?.querySelector('.daw-bottom-tab-content[data-tab="stems"]')?.appendChild(busy);

        try {
            const base64 = await AudioLabCore.readAsBase64(await clipAudioBlob(clip));
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
                    name: `${capStem(b.name)}: ${clip.name}`,
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
                doNoticePopover(translate('Separated into') + ` ${built.length} `
                    + (built.length > 1 ? translate('tracks') : translate('track')), 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Stem separation failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(translate('Stem separation failed:') + ' ' + err.message, 'notice-pop-red');
            }
        } finally {
            busy.done();
            stemsSeparating = false;
        }
    }

    // ===== RECORDING =====
    // Takes are captured by AudioDawInput's worklet on the AudioContext clock (lossless PCM), one per armed
    // track, and placed with latency compensation, so what was played in time with the playback lands in time
    // with it. Count-in, pre-roll, punch in/out (over the loop range) and the metronome are transport settings.

    /** Saved record settings (per browser): default input, voice processing, count-in, pre-roll, offset. */
    function loadRecordSettings() {
        const base = { deviceId: null, voiceMode: false, countInBars: 0, preRollBars: 1, offsetMs: 0, clickVolume: 0.8 };
        try {
            return { ...base, ...JSON.parse(localStorage.getItem('audiolab_daw_record') || '{}') };
        }
        catch (_) {
            return base;
        }
    }

    function saveRecordSettings() {
        try { localStorage.setItem('audiolab_daw_record', JSON.stringify(recordSettings)); } catch (_) {}
    }

    function updateRecordButton(active) {
        const btn = transportEl?.querySelector('.daw-btn-rec');
        if (btn) btn.classList.toggle('active', active);
    }

    /** Latency between scheduling a sample and the performer hearing it (context buffer + output device). */
    function outputLatency(ctx) {
        return (ctx.baseLatency || 0) + (ctx.outputLatency || 0);
    }

    /** Record settings menu (the caret beside Record). */
    async function showMicSettingsMenu(e) {
        const items = [];
        const support = AudioDawInput.support();
        if (!support.mic) {
            items.push({ label: translate('Audio input needs HTTPS (or localhost)'), disabled: true });
        }
        else {
            const devices = await AudioDawInput.listDevices();
            const labelsHidden = !devices.length || devices.every(d => !d.label);
            const perm = await AudioDawInput.permissionState();
            items.push({ heading: translate('Default input for new tracks') });
            if (labelsHidden && perm === 'denied') {
                items.push({ label: translate('Microphone blocked. Allow it in the browser site settings'), disabled: true });
            }
            else if (labelsHidden) {
                items.push({
                    label: translate('Allow microphone access to list inputs…'),
                    action: async () => {
                        try {
                            await AudioDawInput.requestAccess();
                            showMicSettingsMenu(e);
                        }
                        catch (err) {
                            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Microphone access denied:') + ' ' + err.message, 'notice-pop-red');
                        }
                    }
                });
            }
            else {
                items.push({ label: translate('System default input'), checked: !recordSettings.deviceId,
                    action: () => { recordSettings.deviceId = null; saveRecordSettings(); } });
                devices.forEach((d, i) => items.push({
                    label: d.label || (translate('Input') + ` ${i + 1}`),
                    checked: recordSettings.deviceId === d.deviceId,
                    action: () => { recordSettings.deviceId = d.deviceId; saveRecordSettings(); }
                }));
            }
            items.push({
                label: translate('Voice processing (echo and noise reduction)'),
                checked: recordSettings.voiceMode,
                action: () => {
                    recordSettings.voiceMode = !recordSettings.voiceMode;
                    saveRecordSettings();
                    resyncAllInputs();
                }
            });
        }
        items.push({ heading: translate('Count-in') });
        for (const bars of [0, 1, 2]) {
            items.push({
                label: bars ? `${bars} ${bars === 1 ? translate('bar') : translate('bars')}` : translate('Off'),
                checked: recordSettings.countInBars === bars,
                action: () => { recordSettings.countInBars = bars; saveRecordSettings(); }
            });
        }
        items.push({ heading: translate('Pre-roll before a punch-in') });
        for (const bars of [0, 1, 2]) {
            items.push({
                label: bars ? `${bars} ${bars === 1 ? translate('bar') : translate('bars')}` : translate('Off'),
                checked: recordSettings.preRollBars === bars,
                action: () => { recordSettings.preRollBars = bars; saveRecordSettings(); }
            });
        }
        items.push({ heading: translate('Latency compensation') });
        const ctx = audioCtx && audioCtx.state !== 'closed' ? audioCtx : null;
        const inLat = Math.max(0, ...[...trackInputs.values()].map(x => x.latency), 0);
        const outLat = ctx ? outputLatency(ctx) : 0;
        items.push({
            label: translate('Measured') + `: ${translate('in')} ${Math.round(inLat * 1000)} ms + ${translate('out')} ${Math.round(outLat * 1000)} ms`,
            disabled: true
        });
        items.push({
            label: translate('Manual offset') + `: ${recordSettings.offsetMs} ms…`,
            action: () => {
                const val = prompt(translate('Extra recording offset in milliseconds. Positive moves takes earlier, negative later. Leave 0 unless takes land consistently late or early.'), String(recordSettings.offsetMs));
                if (val === null) return;
                const ms = parseFloat(val);
                if (!isNaN(ms) && Math.abs(ms) <= 2000) {
                    recordSettings.offsetMs = Math.round(ms);
                    saveRecordSettings();
                }
            }
        });
        dawMenu(e, items);
    }

    /** "Audio 3": the next unused default name for a recording track. */
    function nextAudioTrackName() {
        let n = 1;
        while (state.tracks.some(t => t.name === `Audio ${n}`)) n++;
        return `Audio ${n}`;
    }

    /** Count-in clicks ending at ctx time t0 (the downbeat recording starts on). */
    function scheduleCountIn(ctx, t0, seconds) {
        if (!mixer || seconds <= 0) return;
        const beat = 60 / state.bpm;
        const perBar = state.timeSignature[0] || 4;
        const beats = Math.round(seconds / beat);
        for (let i = 0; i < beats; i++) {
            const when = t0 - (beats - i) * beat;
            if (when < ctx.currentTime) continue;
            playback?.clicks.push(AudioDawInput.scheduleClick(ctx, mixer.clickBus, when, i % perBar === 0));
        }
    }

    async function startRecordingFlow() {
        if (recording) return;
        const support = AudioDawInput.support();
        if (!support.mic || !support.worklet) {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(!support.mic
                    ? translate('Recording needs HTTPS (or localhost): browsers block audio input on plain HTTP.')
                    : translate('This browser cannot capture audio (no AudioWorklet).'), 'notice-pop-red');
            }
            return;
        }
        let armed = state.tracks.filter(t => t.armed);
        if (!armed.length) {
            pushUndo();
            const track = addTrack({ name: nextAudioTrackName() });
            track.armed = true;
            track.input = { deviceId: recordSettings.deviceId || null, channels: 'mono' };
            armed = [track];
            state.selectedTrackId = track.id;
            renderAllTracks();
        }
        // Every armed input opens before the transport moves: a refused or slow permission prompt must not
        // leave playback running with nothing capturing.
        const opened = await Promise.allSettled(armed.map(t => syncTrackInput(t)));
        opened.forEach((r, i) => { if (r.status === 'rejected') reportInputError(armed[i], r.reason); });
        const inputs = armed.map(t => ({ track: t, entry: trackInputs.get(t.id) })).filter(x => x.entry);
        if (!inputs.length || recording) return;

        const ctx = getAudioContext();
        if (ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
        const sr = ctx.sampleRate;
        const barDur = (60 / state.bpm) * (state.timeSignature[0] || 4);
        const punch = state.punchEnabled && state.loopEnd > state.loopStart ? { in: state.loopStart, out: state.loopEnd } : null;
        const rec = { takes: [], placeholders: [], punch, postRoll: punch ? barDur / 2 : 0, stopping: false, startP: 0, startCtx: 0 };
        recording = rec;

        if (state.isPlaying && playback) {
            // Punch in on the fly: capture from right now (a loop pass, if armed, is dropped for the take)
            if (playback.loop) resyncPlayback();
            rec.startP = punch ? Math.max(punch.in, currentTimelineTime()) : currentTimelineTime();
            rec.startCtx = playback.baseCtxTime + (rec.startP - playback.baseTimelineTime);
        }
        else {
            rec.startP = punch ? punch.in : state.currentTime;
            const preRoll = punch ? Math.min(rec.startP, recordSettings.preRollBars * barDur) : 0;
            const countIn = recordSettings.countInBars > 0 ? recordSettings.countInBars * barDur : 0;
            state.currentTime = rec.startP - preRoll;
            const pb = await startPlayback({ delay: countIn });
            if (!pb || recording !== rec) {
                if (recording === rec) recording = null;
                return;
            }
            rec.startCtx = pb.baseCtxTime + preRoll;
            if (countIn > 0) scheduleCountIn(ctx, pb.baseCtxTime, countIn);
        }

        const startFrame = Math.round(rec.startCtx * sr);
        for (const { track, entry } of inputs) {
            const latency = Math.max(0, entry.latency + outputLatency(ctx) + recordSettings.offsetMs / 1000);
            entry.capture.start(startFrame);
            const take = { track, entry, latency, stopFrame: undefined };
            // A punch stops itself at the out point (plus the latency the take is shifted by)
            if (punch) {
                take.stopFrame = Math.round((rec.startCtx + (punch.out - rec.startP) + latency) * sr);
                entry.capture.stop(take.stopFrame);
            }
            rec.takes.push(take);
            rec.placeholders.push(createRecordingPlaceholder(track, rec.startP));
        }
        state.isRecording = true;
        updateRecordButton(true);
        kickMeters();
    }

    async function stopRecordingFlow() {
        const rec = recording;
        if (!rec || rec.stopping) return;
        rec.stopping = true;
        state.isRecording = false;
        updateRecordButton(false);
        const ctx = getAudioContext();
        const sr = ctx.sampleRate;
        const nowFrame = Math.round(ctx.currentTime * sr);
        // Keep capturing for the latency the take will be shifted by, so the last notes played are kept
        const results = await Promise.all(rec.takes.map(t => {
            const want = nowFrame + Math.round(t.latency * sr);
            // A punch never runs past its own out point, even when Stop comes during the post-roll
            return t.entry.capture.stop(t.stopFrame !== undefined ? Math.min(t.stopFrame, want) : want).catch(() => null);
        }));
        stopPlayback();
        if (recording === rec) recording = null;
        removeRecordingPlaceholders(rec);
        const made = [];
        rec.takes.forEach((take, i) => {
            const res = results[i];
            if (!res || !res.channels.length || res.firstFrame < 0) return;
            const drop = Math.round(take.latency * res.sampleRate);
            const len = res.channels[0].length - drop;
            if (len < res.sampleRate * 0.05) return; // under 50 ms: nothing was really recorded
            const buf = new AudioBuffer({ numberOfChannels: res.channels.length, length: len, sampleRate: res.sampleRate });
            res.channels.forEach((c, ch) => buf.copyToChannel(c.subarray(drop, drop + len), ch));
            // The worklet may have begun a few frames after the planned start (message latency): place the take
            // where its first kept sample really falls
            const start = rec.startP + (res.firstFrame / res.sampleRate - rec.startCtx);
            made.push({ track: take.track, blob: audioBufferToWav(buf, { bitDepth: 32 }), start, peak: maxAbs(res.channels) });
        });
        if (!made.length) {
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Nothing was recorded'), 'notice-pop-yellow');
            return;
        }
        pushUndo();
        for (const m of made) {
            m.track.takeCount = (m.track.takeCount || 0) + 1;
            const clip = await addClipToTrack(m.track, m.blob, {
                name: `${m.track.name} ${translate('take')} ${m.track.takeCount}`,
                startTime: Math.max(0, m.start)
            });
            state.selectedClipId = clip.id;
            state.selectedTrackId = m.track.id;
        }
        updateTotalDuration();
        renderAllTracks();
        updateBottomPanel();
        resyncPlayback();
        const silent = made.filter(m => m.peak < 0.001).length;
        if (typeof doNoticePopover === 'function') {
            doNoticePopover(silent === made.length
                ? translate('Recorded, but the input was silent. Check the track input and the device level')
                : `${translate('Recorded')} ${made.length} ${made.length === 1 ? translate('take') : translate('takes')}`,
                silent === made.length ? 'notice-pop-yellow' : 'notice-pop-green');
        }
    }

    function maxAbs(channels) {
        let peak = 0;
        for (const c of channels) {
            for (let i = 0; i < c.length; i += 16) {
                const v = c[i] < 0 ? -c[i] : c[i];
                if (v > peak) peak = v;
            }
        }
        return peak;
    }

    /** Abort an in-flight recording without keeping audio (teardown, project switch). */
    function abortRecording() {
        if (!recording) return;
        const rec = recording;
        recording = null;
        for (const take of rec.takes) {
            try { take.entry.capture.stop(); } catch (_) {}
        }
        removeRecordingPlaceholders(rec);
        if (state) state.isRecording = false;
        updateRecordButton(false);
    }

    /** A growing red clip on the lane with the take's live waveform, drawn from the input's level. */
    function createRecordingPlaceholder(track, startP) {
        const el = createDiv(null, 'daw-clip daw-clip-recording');
        el.style.left = (startP * state.zoom) + 'px';
        el.style.width = '2px';
        const label = createSpan(null, 'daw-clip-label');
        label.textContent = translate('Recording');
        el.appendChild(label);
        const strip = createDiv(null, 'daw-rec-strip');
        el.appendChild(strip);
        track.laneEl?.appendChild(el);
        return { track, el, strip, tiles: [], lastX: 0, startP };
    }

    function updateRecordingPlaceholders() {
        if (!recording) return;
        const t = state.currentTime;
        for (const ph of recording.placeholders) {
            // A lane rebuilt mid-take (track list re-render) gets its placeholder back
            if (ph.track.laneEl && ph.el.parentElement !== ph.track.laneEl) ph.track.laneEl.appendChild(ph.el);
            ph.el.style.left = (ph.startP * state.zoom) + 'px';
            if (t <= ph.startP) continue; // count-in or pre-roll: nothing captured yet
            const x = (t - ph.startP) * state.zoom;
            ph.el.style.width = Math.max(2, x) + 'px';
            const entry = trackInputs.get(ph.track.id);
            drawPlaceholderLevel(ph, x, entry ? entry.level : 0);
        }
    }

    function drawPlaceholderLevel(ph, x, level) {
        const TILE = 1024;
        const h = Math.max(8, (ph.track.height || 100) - 8);
        while (ph.tiles.length * TILE < x + 1) {
            const c = document.createElement('canvas');
            c.width = TILE;
            c.height = h;
            c.className = 'daw-rec-tile';
            c.style.left = (ph.tiles.length * TILE) + 'px';
            ph.strip.appendChild(c);
            ph.tiles.push(c);
        }
        const amp = Math.max(1, levelToPct(level) * h * 0.48);
        for (let px = Math.floor(ph.lastX); px <= Math.floor(x); px++) {
            const tile = ph.tiles[Math.floor(px / TILE)];
            if (!tile) continue;
            const g = tile.getContext('2d');
            g.fillStyle = 'rgba(255, 255, 255, 0.75)';
            g.fillRect(px % TILE, h / 2 - amp, 1, amp * 2);
        }
        ph.lastX = x;
    }

    function removeRecordingPlaceholders(rec) {
        for (const ph of rec.placeholders) ph.el.remove();
        rec.placeholders = [];
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
        // Drum pads hold their audio in the same store
        for (const lane of state.beatPattern.lanes) referenced.add(lane.blobKey);
        for (const snap of [...state.undoStack, ...state.redoStack]) {
            for (const ts of snap.tracks) {
                for (const cs of ts.clips) referenced.add(cs.blobKey);
            }
            for (const lane of snap.beatPattern?.lanes || []) referenced.add(lane.blobKey);
        }
        for (const key of [...blobStore.keys()]) {
            if (!referenced.has(key)) blobStore.delete(key);
        }
    }

    /** Everything undo restores, deep-copied so later edits never alias the snapshot. */
    function takeSnapshot() {
        return {
            tracks: state.tracks.map(t => AudioDawTrack.serializeTrack(t)),
            selectedTrackId: state.selectedTrackId,
            selectedClipId: state.selectedClipId,
            masterVolume: state.masterVolume,
            bpm: state.bpm,
            timeSignature: [...state.timeSignature],
            markers: (state.markers || []).map(m => ({ ...m })),
            beatPattern: {
                steps: state.beatPattern.steps,
                swing: state.beatPattern.swing,
                lanes: state.beatPattern.lanes.map(l => ({ ...l, steps: [...l.steps] }))
            }
        };
    }

    function pushUndo() {
        const snapshot = takeSnapshot();
        state.undoStack.push(snapshot);
        if (state.undoStack.length > MAX_UNDO) state.undoStack.shift();
        state.redoStack = []; // clear redo on new action
        garbageCollectBlobs();
        scheduleAutosave(); // every undoable mutation marks the session dirty
    }

    async function doUndo() {
        if (state.undoStack.length === 0) return;
        // Save current state to redo
        const currentSnapshot = takeSnapshot();
        state.redoStack.push(currentSnapshot);

        const snapshot = state.undoStack.pop();
        await restoreSnapshot(snapshot);
        garbageCollectBlobs();
    }

    async function doRedo() {
        if (state.redoStack.length === 0) return;
        const currentSnapshot = takeSnapshot();
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
        if (snapshot.timeSignature) {
            state.timeSignature = [...snapshot.timeSignature];
            const sig = transportEl?.querySelector('.daw-transport-timesig');
            if (sig) sig.value = state.timeSignature.join('/');
        }
        if (timeline) timeline.setTempo(state.bpm, state.timeSignature);
        if (snapshot.markers) state.markers = snapshot.markers.map(m => ({ ...m }));
        if (snapshot.beatPattern) {
            state.beatPattern = {
                steps: snapshot.beatPattern.steps,
                swing: snapshot.beatPattern.swing,
                lanes: snapshot.beatPattern.lanes.map(l => ({ ...l, steps: [...l.steps] }))
            };
            const beatsPane = bottomPanelEl?.querySelector('.daw-bottom-tab-content[data-tab="beats"]');
            if (beatsPane) renderBeatsPanel(beatsPane);
        }

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
            track.monitor = !!ts.monitor;
            track.input = { deviceId: ts.input?.deviceId || null, channels: ts.input?.channels ?? 'mono' };
            track.takeCount = ts.takeCount || 0;
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
                    id: cs.id,
                    name: cs.name,
                    startTime: cs.startTime,
                    color: cs.color,
                    blobKey: cs.blobKey,
                    meta: cs.meta || null
                });
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
        // Inputs follow the restored arm/monitor state; tracks that no longer exist lose theirs
        const live = new Set(state.tracks.map(t => t.id));
        for (const id of [...trackInputs.keys()]) {
            if (!live.has(id)) closeTrackInput(id);
        }
        for (const t of state.tracks) {
            if (t.armed || t.monitor || trackInputs.has(t.id)) syncTrackInput(t).catch(err => reportInputError(t, err));
        }
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
            translate('Source BPM of this clip? It will be stretched to the project tempo (pitch preserved).') +
            `\n\n"${clip.name}" → ${state.bpm} BPM` +
            (guess ? `\n\n${translate('Detected:')} ~${guess} BPM` : ''),
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
        const overlay = showDawLoadingOverlay(translate('Stretching') + ` ${srcBpm} → ${state.bpm} BPM...`);
        try {
            const base64 = await AudioLabCore.readAsBase64(await clipAudioBlob(clip));
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
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Tempo conform failed:') + ' ' + err.message, 'notice-pop-red');
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
        if (btn) btn.textContent = '\u25B6 ' + translate('Audition');
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
        btn.textContent = translate('Rendering…');
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
            btn.textContent = '\u25A0 ' + translate('Stop');
        } catch (err) {
            btn.textContent = '\u25B6 ' + translate('Audition');
            console.error('[AudioDaw] Beat audition failed:', err);
        }
    }

    async function renderBeatsToTrack(repeats) {
        if (!patternHasHits()) {
            if (typeof doNoticePopover === 'function') doNoticePopover('Program some steps first', 'notice-pop-yellow');
            return;
        }
        stopBeatAudition();
        const overlay = showDawLoadingOverlay(translate('Rendering beat...'));
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
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Beat render failed:') + ' ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    function newBeatLane(name, blobKey) {
        return { name, blobKey, gain: 1, steps: Array(state.beatPattern.steps).fill(false) };
    }

    async function addBeatPadBlob(name, blob, container) {
        const blobKey = `beatpad-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
        const decodedBuffer = await AudioLabCore.decodeToBuffer(blob);
        pushUndo();
        blobStore.set(blobKey, { blob, decodedBuffer });
        state.beatPattern.lanes.push(newBeatLane(name, blobKey));
        renderBeatsPanel(container);
    }

    // ===== PIANO / KEYS =====
    // Plays into the Score tab rather than rendering audio: the score is the artifact YuE2 can act on, so a
    // captured phrase becomes part of the plan instead of another clip to mix.

    let keysState = { voice: 'Vocal', octave: 4, recording: null, midiAccess: null };

    /** Web MIDI needs a secure context, and this server is usually reached over a LAN IP on plain HTTP.
     *  The on-screen keyboard is therefore the path that always works; MIDI is the bonus when it exists. */
    function keysMidiPossible() {
        return !!(window.isSecureContext && navigator.requestMIDIAccess);
    }

    function keysBeep(midi) {
        const ctx = getAudioContext();
        if (ctx.state === 'suspended') { try { ctx.resume(); } catch (_) {} }
        const osc = ctx.createOscillator(), gain = ctx.createGain();
        osc.type = 'triangle';
        osc.frequency.value = 440 * Math.pow(2, (midi - 69) / 12);
        gain.gain.setValueAtTime(0.0001, ctx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.35);
        osc.connect(gain); gain.connect(ctx.destination);
        osc.start(); osc.stop(ctx.currentTime + 0.4);
    }

    function keysDown(midi) {
        keysBeep(midi);
        const rec = keysState.recording;
        if (rec && !rec.held.has(midi)) rec.held.set(midi, performance.now());
    }

    function keysUp(midi) {
        const rec = keysState.recording;
        if (!rec || !rec.held.has(midi)) return;
        rec.events.push({ midi, start: rec.held.get(midi), end: performance.now() });
        rec.held.delete(midi);
    }

    /** Turn what was played into a run of notes and rests on the score's own grid. */
    function keysQuantize(events, grid) {
        const t0 = Math.min(...events.map(e => e.start));
        const unit = (ms) => Math.round((ms / 1000) / grid.secondsPerUnit);
        const out = [];
        let cursor = 0;
        for (const e of [...events].sort((a, b) => a.start - b.start)) {
            const start = Math.max(cursor, unit(e.start - t0));
            const units = Math.max(1, unit(e.end - t0) - start);
            if (start > cursor) out.push({ midi: null, units: start - cursor });
            out.push({ midi: e.midi, units });
            cursor = start + units;
        }
        return out;
    }

    function keysStopRecording(panelContainer) {
        const rec = keysState.recording;
        keysState.recording = null;
        if (!rec || !rec.events.length) {
            renderBeatsPanel(panelContainer);
            return;
        }
        const grid = AudioDawScore.getGrid();
        const result = AudioDawScore.insertNotes({
            voice: keysState.voice,
            barIndex: rec.bar,
            offsetUnits: 0,
            notes: keysQuantize(rec.events, grid)
        });
        if (typeof doNoticePopover === 'function') {
            doNoticePopover(result.ok
                ? translate('Wrote notes into the score:')
                    + ` ${rec.events.length} → ${keysState.voice}, ${translate('bar')} ${rec.bar + 1}`
                : (result.issues?.[0]?.message || 'Those notes would not fit the score'),
                result.ok ? 'notice-pop-green' : 'notice-pop-yellow');
        }
        if (result.ok) switchBottomTab('score');
        renderBeatsPanel(panelContainer);
    }

    function renderKeysInstrument(card, panelContainer) {
        const head = createDiv(null, 'daw-stems-action-row');
        const voiceWrap = createDiv(null, 'daw-score-field');
        const voiceLbl = createSpan(null, 'daw-stems-ctl-label');
        voiceLbl.textContent = translate('Voice');
        const voiceSel = document.createElement('select');
        voiceSel.className = 'daw-fx-select';
        for (const v of ['Vocal', 'Ins']) {
            const o = document.createElement('option');
            o.value = v; o.textContent = v;
            voiceSel.appendChild(o);
        }
        voiceSel.value = keysState.voice;
        voiceSel.addEventListener('change', () => { keysState.voice = voiceSel.value; });
        voiceWrap.appendChild(voiceLbl); voiceWrap.appendChild(voiceSel);
        head.appendChild(voiceWrap);

        const octWrap = createDiv(null, 'daw-score-field');
        const octLbl = createSpan(null, 'daw-stems-ctl-label');
        octLbl.textContent = translate('Octave');
        const octSel = document.createElement('select');
        octSel.className = 'daw-fx-select';
        for (const n of [2, 3, 4, 5, 6]) {
            const o = document.createElement('option');
            o.value = String(n); o.textContent = 'C' + n;
            octSel.appendChild(o);
        }
        octSel.value = String(keysState.octave);
        octSel.addEventListener('change', () => { keysState.octave = parseInt(octSel.value, 10); renderBeatsPanel(panelContainer); });
        octWrap.appendChild(octLbl); octWrap.appendChild(octSel);
        head.appendChild(octWrap);

        const recBtn = document.createElement('button');
        recBtn.className = 'basic-button btn-sm btn-primary daw-stems-go';
        recBtn.textContent = keysState.recording ? translate('Stop and write') : translate('Record into score');
        recBtn.title = translate('Play the keys below; what you play lands in the score at the playhead bar');
        recBtn.addEventListener('click', () => {
            if (keysState.recording) { keysStopRecording(panelContainer); return; }
            keysState.recording = { bar: AudioDawScore.barAtTime(state.currentTime), events: [], held: new Map() };
            renderBeatsPanel(panelContainer);
        });
        head.appendChild(recBtn);
        card.appendChild(head);

        const grid = AudioDawScore.getGrid();
        const key = AudioDawScore.getKey();
        const info = createDiv(null, 'daw-stems-desc');
        info.textContent = keysState.recording
            ? translate('Recording. Stop and write puts it in the score.')
                + ` (${keysState.voice}, ${translate('bar')} ${keysState.recording.bar + 1})`
            : translate('Writes into the Score tab at the playhead bar.')
                + ` ${key.key}${key.mode} · ${grid.unitLength} · ${Math.round(grid.bpm)} BPM`;
        card.appendChild(info);

        // Two rows: the black keys sit above the white ones, with gaps where a piano has none.
        const BLACK = [1, 1, 0, 1, 1, 1, 0];
        const WHITE = [0, 2, 4, 5, 7, 9, 11];
        const base = (keysState.octave + 1) * 12;
        const blackRow = createDiv(null, 'daw-keys-row');
        const whiteRow = createDiv(null, 'daw-keys-row');
        const NAMES = ['C', 'D', 'E', 'F', 'G', 'A', 'B'];
        for (let o = 0; o < 2; o++) {
            for (let i = 0; i < 7; i++) {
                const white = base + o * 12 + WHITE[i];
                whiteRow.appendChild(keyButton(`${NAMES[i]}${keysState.octave + o}`, white));
                if (BLACK[i]) blackRow.appendChild(keyButton(`${NAMES[i]}♯`, white + 1));
                else blackRow.appendChild(keySpacer());
            }
        }
        whiteRow.appendChild(keyButton(`C${keysState.octave + 2}`, base + 24));
        blackRow.appendChild(keySpacer());
        card.appendChild(blackRow);
        card.appendChild(whiteRow);

        const midiRow = createDiv(null, 'daw-stems-desc');
        if (!keysMidiPossible()) {
            midiRow.textContent = translate('A MIDI keyboard needs a page served over HTTPS or from localhost; over a LAN '
                + 'address the browser will not offer it. The keys above work either way.');
        }
        else if (keysState.midiAccess) {
            const names = [...keysState.midiAccess.inputs.values()].map(i => i.name);
            midiRow.textContent = names.length
                ? translate('MIDI in:') + ' ' + names.join(', ')
                : translate('MIDI is on, but no input is connected.');
        }
        else {
            const btn = document.createElement('button');
            btn.className = 'basic-button btn-sm';
            btn.textContent = translate('Connect MIDI keyboard');
            btn.addEventListener('click', () => connectKeysMidi(panelContainer));
            midiRow.appendChild(btn);
        }
        card.appendChild(midiRow);
    }

    function keyButton(label, midi) {
        const b = document.createElement('button');
        b.className = 'basic-button btn-sm daw-keys-key';
        b.textContent = label;
        b.dataset.midi = String(midi);
        b.addEventListener('pointerdown', (e) => { e.preventDefault(); keysDown(midi); });
        b.addEventListener('pointerup', () => keysUp(midi));
        b.addEventListener('pointerleave', () => keysUp(midi));
        return b;
    }

    function keySpacer() {
        const s = createDiv(null, 'daw-keys-key daw-keys-gap');
        return s;
    }

    async function connectKeysMidi(panelContainer) {
        try {
            keysState.midiAccess = await navigator.requestMIDIAccess();
            for (const input of keysState.midiAccess.inputs.values()) {
                input.onmidimessage = (ev) => {
                    const [status, note, velocity] = ev.data;
                    const cmd = status & 0xf0;
                    if (cmd === 0x90 && velocity > 0) keysDown(note);
                    else if (cmd === 0x80 || (cmd === 0x90 && velocity === 0)) keysUp(note);
                };
            }
            renderBeatsPanel(panelContainer);
        }
        catch (err) {
            console.error('[AudioDaw] MIDI access denied:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('MIDI access was refused:') + ' ' + err.message, 'notice-pop-yellow');
        }
    }

    // Instrument browser: the drum machine ships today; the rest are planned
    // slots (clicking one explains what's coming). New instruments plug in by
    // flipping ready:true and rendering their UI in renderBeatsPanel.
    const DAW_INSTRUMENTS = [
        { id: 'drums', name: 'Drum Machine', desc: '16/32-step sample sequencer', ready: true },
        { id: 'keys', name: 'Piano / Keys', desc: 'Play a phrase into the score', ready: true },
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
            pick.innerHTML = `<span class="daw-fx-pick-name">${translate(inst.name)}</span>`
                + `<span class="daw-fx-pick-desc">${translate(inst.desc)}</span>`;
            pick.addEventListener('click', () => {
                if (!inst.ready) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate(inst.name) + ': ' + translate('on the roadmap. The Drum Machine is ready today'), 'notice-pop-yellow');
                    return;
                }
                activeInstrument = inst.id;
                renderBeatsPanel(container);
            });
            browser.appendChild(pick);
        }
        panel.appendChild(browser);

        const instCard = createDiv(null, 'daw-fx-card daw-inst-card');
        panel.appendChild(instCard);
        if (activeInstrument === 'keys') {
            if (typeof AudioDawScore === 'undefined') {
                const missing = createDiv(null, 'daw-stems-desc');
                missing.textContent = translate('The Score tab did not load, so there is nothing to play into.');
                instCard.appendChild(missing);
                return;
            }
            renderKeysInstrument(instCard, container);
            return;
        }

        // Header controls
        const head = createDiv(null, 'daw-stems-model-row');
        const stepsSel = document.createElement('select');
        stepsSel.className = 'daw-fx-select';
        for (const n of [16, 32]) {
            const opt = document.createElement('option');
            opt.value = n; opt.textContent = `${n} ` + translate('steps');
            stepsSel.appendChild(opt);
        }
        stepsSel.value = p.steps;
        stepsSel.addEventListener('change', () => {
            pushUndo();
            const n = parseInt(stepsSel.value);
            p.steps = n;
            for (const lane of p.lanes) {
                lane.steps = Array.from({ length: n }, (_, i) => !!lane.steps[i]);
            }
            renderBeatsPanel(container);
        });
        head.appendChild(stepsSel);

        const swingLbl = createSpan(null, 'daw-fx-knob-label');
        swingLbl.textContent = translate('Swing');
        const swing = document.createElement('input');
        swing.type = 'range';
        swing.min = '0'; swing.max = '0.6'; swing.step = '0.05';
        swing.value = p.swing || 0;
        swing.className = 'daw-beats-swing';
        let swingGesture = false;
        swing.addEventListener('input', () => {
            if (!swingGesture) { swingGesture = true; pushUndo(); }
            p.swing = parseFloat(swing.value);
        });
        swing.addEventListener('change', () => { swingGesture = false; });
        head.appendChild(swingLbl);
        head.appendChild(swing);

        const auditionBtn = document.createElement('button');
        auditionBtn.className = 'basic-button btn-sm daw-beats-audition';
        auditionBtn.textContent = '\u25B6 ' + translate('Audition');
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
        repeatsSel.title = translate('Pattern repeats to render');
        head.appendChild(repeatsSel);

        const renderBtn = document.createElement('button');
        renderBtn.className = 'basic-button btn-sm btn-primary daw-stems-go';
        renderBtn.textContent = translate('Render to Track');
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
            prev.title = translate('Preview pad');
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
            gainSl.title = translate('Pad gain');
            let gainGesture = false;
            gainSl.addEventListener('input', () => {
                if (!gainGesture) { gainGesture = true; pushUndo(); }
                lane.gain = parseFloat(gainSl.value);
            });
            gainSl.addEventListener('change', () => { gainGesture = false; });
            row.appendChild(gainSl);
            const del = document.createElement('button');
            del.className = 'daw-fx-mini-btn';
            del.innerHTML = '&#x2715;';
            del.title = translate('Remove lane');
            del.addEventListener('click', () => {
                pushUndo();
                p.lanes.splice(li, 1);
                renderBeatsPanel(container);
            });
            row.appendChild(del);
            const stepsWrap = createDiv(null, 'daw-beats-steps');
            for (let i = 0; i < p.steps; i++) {
                const cell = document.createElement('button');
                cell.className = 'daw-beats-step' + (lane.steps[i] ? ' on' : '') + (i % 4 === 0 ? ' beat-head' : '');
                cell.addEventListener('click', () => {
                    pushUndo();
                    lane.steps[i] = !lane.steps[i];
                    cell.classList.toggle('on', lane.steps[i]);
                });
                stepsWrap.appendChild(cell);
            }
            row.appendChild(stepsWrap);
            grid.appendChild(row);
        });
        if (!p.lanes.length) {
            const empty = createDiv(null, 'daw-stems-desc');
            empty.textContent = translate('No pads yet. Describe a one-shot below and hit Generate Pad, or import a sample.');
            instCard.appendChild(empty);
        }
        instCard.appendChild(grid);

        // Add-pad row
        const addRow = createDiv(null, 'daw-stems-action-row');
        const promptInput = document.createElement('input');
        promptInput.type = 'text';
        promptInput.className = 'daw-generate-reftext';
        promptInput.placeholder = translate('Describe a one-shot: "punchy kick drum", "tight snare", "closed hi-hat"...');
        addRow.appendChild(promptInput);
        const genBtn = document.createElement('button');
        genBtn.className = 'basic-button btn-sm';
        genBtn.textContent = translate('Generate Pad');
        genBtn.addEventListener('click', async () => {
            const prompt = promptInput.value.trim();
            if (!prompt) {
                if (typeof doNoticePopover === 'function') doNoticePopover('Describe the sound first', 'notice-pop-yellow');
                return;
            }
            genBtn.disabled = true;
            genBtn.textContent = translate('Generating…');
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
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Pad generation failed:') + ' ' + err.message, 'notice-pop-red');
                genBtn.disabled = false;
                genBtn.textContent = translate('Generate Pad');
            }
        });
        addRow.appendChild(genBtn);
        const importBtn = document.createElement('button');
        importBtn.className = 'basic-button btn-sm';
        importBtn.textContent = translate('Import Pad');
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
        clipBtn.textContent = translate('From Selected Clip');
        clipBtn.addEventListener('click', async () => {
            const sel = findClipById(state.selectedClipId);
            if (!sel) {
                if (typeof doNoticePopover === 'function') doNoticePopover(translate('Select a clip first'), 'notice-pop-yellow');
                return;
            }
            // A trimmed or split clip shares its source blob, so the pad gets just the part that plays.
            const trimmed = (sel.clip.offset || 0) > 1e-6 || (sel.clip.trimEnd || 0) > 1e-6;
            if (trimmed) {
                await addBeatPadBlob(sel.clip.name.slice(0, 18), await clipAudioBlob(sel.clip), container);
                return;
            }
            pushUndo();
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

    /** Engine a palette request runs on: AudioGen for SFX, the first installed music engine for loops. */
    function paletteEngineFor(type) {
        return type === 'sfx' ? 'audiogen_sfx' : preferredMusicEngine();
    }

    async function paletteGenerate(prompt, type, duration, count) {
        try { await ensureEnginesList(); } catch (_) {}
        const engineId = paletteEngineFor(type);
        const model = engineId ? dawSwarmModelFor(engineId) : null;
        if (!model) {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(translate('No installed engine for') + ' '
                    + (type === 'sfx' ? translate('sound effects (AudioGen)') : translate('music')), 'notice-pop-yellow');
            }
            return [];
        }
        // Each candidate is a normal Swarm generation, saved to outputs + history. Loops are instrumental, so
        // the description is the style and lyric engines get no lyrics.
        const errors = [];
        const jobs = [];
        for (let i = 0; i < count; i++) {
            const seed = Math.floor(Math.random() * 1e9);
            const req = buildAudioGenRequest(engineId, { description: prompt, duration, seed, tempoHints: type !== 'sfx' });
            jobs.push(dawSwarmGenerate({ model, prompt: req.prompt, params: req.params })
                .then(async ({ blob, metadata }) => {
                    let length = duration;
                    try { length = (await AudioLabCore.decodeToBuffer(blob)).duration; } catch (_) {}
                    return { blob, seed, metadata, length };
                })
                .catch(err => { console.error('[AudioDaw] Palette generation failed:', err); errors.push(err.message); return null; }));
        }
        const done = (await Promise.all(jobs)).filter(Boolean);
        if (!done.length && errors.length && typeof doNoticePopover === 'function') {
            doNoticePopover(translate('Generation failed:') + ' ' + errors[0], 'notice-pop-red');
        }
        return done.map(({ blob, seed, metadata, length }) => ({
            blob, url: URL.createObjectURL(blob), prompt, seed, type, duration: length,
            meta: buildClipScoreMeta(metadata, { style: prompt, model, engineId, label: prompt.slice(0, 24) })
        }));
    }

    function renderPalette() {
        if (!paletteEl) return;
        paletteEl.innerHTML = '';
        const head = createDiv(null, 'daw-palette-head');
        head.innerHTML = `<strong>${translate('Sound Palette')}</strong>`;
        const closeBtn = document.createElement('button');
        closeBtn.className = 'daw-fx-mini-btn';
        closeBtn.innerHTML = '&#x2715;';
        closeBtn.addEventListener('click', () => togglePalette(transportEl?.querySelector('.daw-btn-palette')));
        head.appendChild(closeBtn);
        paletteEl.appendChild(head);

        const promptInput = document.createElement('textarea');
        promptInput.className = 'daw-generate-text';
        promptInput.rows = 2;
        promptInput.placeholder = translate('Describe a sound: "rain on a tin roof", "808 bass loop, dark trap"...');
        paletteEl.appendChild(promptInput);

        const optsRow = createDiv(null, 'daw-palette-opts');
        const typeSel = document.createElement('select');
        typeSel.className = 'daw-fx-select';
        for (const [v, l] of [['sfx', 'SFX'], ['loop', 'Loop / Music']]) {
            const opt = document.createElement('option');
            opt.value = v; opt.textContent = translate(l);
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
        goBtn.textContent = translate('Generate');
        goBtn.addEventListener('click', async () => {
            const prompt = promptInput.value.trim();
            if (!prompt) return;
            goBtn.disabled = true;
            goBtn.textContent = translate('Generating…');
            try {
                const results = await paletteGenerate(prompt, typeSel.value, parseInt(durSel.value), parseInt(countSel.value));
                paletteResults.unshift(...results);
                paletteResults = paletteResults.slice(0, 12);
                renderPalette();
                const pi = paletteEl.querySelector('.daw-generate-text');
                if (pi) pi.value = prompt;
            } finally {
                goBtn.disabled = false;
                goBtn.textContent = translate('Generate');
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
            label.title = `${res.prompt} (${translate('seed')} ${res.seed})`;
            card.appendChild(label);
            const audio = document.createElement('audio');
            audio.controls = true;
            audio.src = res.url;
            audio.className = 'daw-palette-audio';
            card.appendChild(audio);
            const actions = createDiv(null, 'daw-palette-actions');
            const addBtn = document.createElement('button');
            addBtn.className = 'basic-button btn-sm';
            addBtn.textContent = '+ ' + translate('Track');
            addBtn.addEventListener('click', async () => {
                pushUndo();
                const track = addTrack({ name: res.prompt.slice(0, 16) });
                await addClipToTrack(track, res.blob, { name: res.prompt.slice(0, 24), startTime: snapTime(state.currentTime), meta: res.meta || null });
                updateTotalDuration();
                renderAllTracks();
                updateBottomPanel();
                resyncPlayback();
            });
            actions.appendChild(addBtn);
            const padBtn = document.createElement('button');
            padBtn.className = 'basic-button btn-sm';
            padBtn.textContent = '+ ' + translate('Pad');
            padBtn.title = translate('Add as a beat sequencer pad');
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
            varBtn.title = translate('Generate a variation (same prompt, new seed)');
            varBtn.addEventListener('click', async () => {
                varBtn.disabled = true;
                const results = await paletteGenerate(res.prompt, res.type, Math.max(1, Math.round(res.duration || 4)), 1);
                paletteResults.unshift(...results);
                paletteResults = paletteResults.slice(0, 12);
                renderPalette();
            });
            actions.appendChild(varBtn);
            card.appendChild(actions);
            list.appendChild(card);
        }
        if (!paletteResults.length) {
            list.innerHTML = `<div class="daw-stems-clipinfo" style="padding:0.5rem;">${translate('Generated sounds appear here. Audition, then add to a track or beat pad.')}</div>`;
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
            // 3 added clip.meta (the Score tab's per-clip score). Version 2 projects load unchanged — a clip
            // without meta simply has none.
            version: 3,
            projectName: currentProjectName || null,
            bpm: state.bpm,
            masterLimiterEnabled: state.masterLimiterEnabled,
            timeSignature: state.timeSignature,
            masterVolume: state.masterVolume,
            rulerMode: state.rulerMode,
            snapEnabled: state.snapEnabled,
            metronome: state.metronome,
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
        closeAllInputs();
        destroyMixer();
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
        state.metronome = !!project.metronome;
        // Clear before anything is stored: clearing after the pad loop below wiped every pad's audio.
        blobStore.clear();
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

        for (const ts of project.tracks || []) {
            const track = AudioDawTrack.createTrack({ name: ts.name, color: ts.color, height: ts.height });
            track.volume = ts.volume ?? 0.8;
            track.pan = ts.pan || 0;
            track.muted = !!ts.muted;
            track.soloed = !!ts.soloed;
            // Opening a project never opens a microphone: arm and monitor start off, the input choice is kept
            track.armed = false;
            track.monitor = false;
            track.input = { deviceId: ts.input?.deviceId || null, channels: ts.input?.channels ?? 'mono' };
            track.takeCount = ts.takeCount || 0;
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
                    id: cs.id, name: cs.name, startTime: cs.startTime, color: cs.color, blobKey: cs.blobKey,
                    meta: cs.meta || null
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
        title.textContent = translate('Open a project');
        card.appendChild(title);
        const detail = createDiv(null, 'daw-resume-detail');
        detail.textContent = translate('Pick up where you left off, or start fresh.');
        card.appendChild(detail);
        const list = createDiv(null, 'daw-start-projects');
        for (const n of names.slice(0, 8)) {
            quickAppendButton(list, escapeHtml(n), () => {
                card.remove();
                openProjectFromServer(n);
            }, ' basic-button', translate('Load project') + ` "${n}"`);
        }
        card.appendChild(list);
        const actions = createDiv(null, 'daw-resume-actions');
        quickAppendButton(actions, translate('Start Empty'), () => card.remove(), ' basic-button', translate('Begin a fresh session'));
        card.appendChild(actions);
        body.appendChild(card);
    }

    function showResumeBar(meta) {
        const body = document.getElementById('daw_container');
        if (!body || body.querySelector('.daw-resume-bar')) return;
        const bar = createDiv(null, 'daw-resume-bar');

        const title = createDiv(null, 'daw-resume-title');
        title.textContent = translate('Resume your last session?');
        bar.appendChild(title);

        // Session summary from the recovered project JSON
        const proj = meta.project || {};
        const tracks = Array.isArray(proj.tracks) ? proj.tracks : [];
        const clipCount = tracks.reduce((n, t) => n + (t.clips?.length || 0), 0);
        const bits = [];
        if (proj.projectName) bits.push(`“${proj.projectName}”`);
        bits.push(`${tracks.length} ` + (tracks.length === 1 ? translate('track') : translate('tracks')));
        bits.push(`${clipCount} ` + (clipCount === 1 ? translate('clip') : translate('clips')));
        if (proj.bpm) bits.push(`${proj.bpm} BPM`);
        bits.push(translate('autosaved') + ' ' + new Date(meta.savedAt).toLocaleString());
        const detail = createDiv(null, 'daw-resume-detail');
        detail.textContent = bits.join(' · ');
        bar.appendChild(detail);

        const actions = createDiv(null, 'daw-resume-actions');
        quickAppendButton(actions, translate('Resume'), async () => {
            bar.remove();
            const overlay = showDawLoadingOverlay(translate('Restoring session...'));
            try {
                const data = await AudioDawStore.loadProject(AUTOSAVE_SLOT);
                if (data) await restoreProject(data.project, data.blobs);
            } catch (err) {
                console.error('[AudioDaw] Resume failed:', err);
                if (typeof doNoticePopover === 'function') {
                    doNoticePopover(translate('Resume failed:') + ' ' + err.message, 'notice-pop-red');
                }
            }
            hideDawLoadingOverlay(overlay);
        }, ' basic-button btn-primary', translate('Restore the autosaved arrangement'));
        quickAppendButton(actions, translate('Discard'), () => {
            bar.remove();
            AudioDawStore.deleteProject(AUTOSAVE_SLOT).catch(() => {});
        }, ' basic-button', translate('Delete the autosave and start fresh'));
        bar.appendChild(actions);
        body.appendChild(bar);
    }

    async function saveProjectToServer(name) {
        const overlay = showDawLoadingOverlay(translate('Saving project...'));
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
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Project saved:') + ` "${name}"`, 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Save project failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Save failed:') + ' ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    async function openProjectFromServer(name) {
        const overlay = showDawLoadingOverlay(translate('Loading project...'));
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
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Project loaded:') + ` "${name}"`, 'notice-pop-green');
        } catch (err) {
            console.error('[AudioDaw] Load project failed:', err);
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Load failed:') + ' ' + err.message, 'notice-pop-red');
        }
        hideDawLoadingOverlay(overlay);
    }

    function promptSaveAs() {
        const name = prompt(translate('Project name:'), currentProjectName || 'My Project');
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
            { label: currentProjectName ? translate('Save') + ` "${currentProjectName}"` : translate('Save…'), action: () => {
                if (currentProjectName) saveProjectToServer(currentProjectName);
                else promptSaveAs();
            }},
            { label: translate('Save As…'), action: promptSaveAs },
            { label: translate('Open…'), action: async () => {
                try {
                    const result = await AudioLabAPI.callAPI('AudioLabListProjects');
                    const names = (result.projects || []).filter(n => n !== AUTOSAVE_SLOT);
                    if (!names.length) {
                        if (typeof doNoticePopover === 'function') doNoticePopover('No saved projects yet', 'notice-pop-yellow');
                        return;
                    }
                    dawMenu(e, names.map(n => ({ label: n, action: () => openProjectFromServer(n) })));
                } catch (err) {
                    if (typeof doNoticePopover === 'function') doNoticePopover(translate('Failed to list projects:') + ' ' + err.message, 'notice-pop-red');
                }
            }},
            { label: translate('New Project'), action: () => {
                if (state.tracks.some(t => t.clips.length > 0) && !confirm(translate('Start a new empty project? Unsaved changes are kept only in the autosave slot.'))) {
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
            if (typeof doNoticePopover === 'function') doNoticePopover(translate('Save to Outputs failed:') + ' ' + err.message, 'notice-pop-red');
        }
    }

    // ===== EXPORT =====

    function showExportMenu(e) {
        dawMenu(e, [
            { label: translate('WAV (Lossless)'), action: () => doExportMixdown('wav') },
            { label: translate('MP3 (192kbps)'), action: () => doExportMixdown('mp3') },
            { label: translate('OGG Vorbis'), action: () => doExportMixdown('ogg') },
            { label: translate('FLAC (Lossless)'), action: () => doExportMixdown('flac') },
            { label: translate('AAC (192kbps)'), action: () => doExportMixdown('aac') },
            { label: translate('Save WAV to Swarm Outputs'), action: saveMixdownToOutputs }
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
                    doNoticePopover(translate('Converting to') + ` ${format.toUpperCase()}...`, 'notice-pop-blue');
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
                        doNoticePopover(`${format.toUpperCase()} ` + translate('exported'), 'notice-pop-green');
                    }
                } else {
                    throw new Error(result.error || 'Conversion failed');
                }
            }
        } catch (err) {
            console.error('[AudioDaw] Export failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(translate('Export failed:') + ' ' + err.message, 'notice-pop-red');
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
     * Convert an AudioBuffer to a WAV Blob. Pure JS, no dependency on Crunker for export.
     * @param {Object} [opts] - { bitDepth: 16 (PCM, default), 24 (PCM) or 32 (IEEE float, lossless) }
     */
    function audioBufferToWav(buffer, { bitDepth = 16 } = {}) {
        const numChannels = buffer.numberOfChannels;
        const sampleRate = buffer.sampleRate;
        const isFloat = bitDepth === 32;
        const format = isFloat ? 3 : 1; // 3 = IEEE float, 1 = PCM
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
                const raw = channels[ch][i];
                if (isFloat) {
                    view.setFloat32(offset, raw, true);
                }
                else {
                    const sample = Math.max(-1, Math.min(1, raw));
                    if (bitDepth === 24) {
                        const v = Math.round(sample < 0 ? sample * 0x800000 : sample * 0x7FFFFF);
                        view.setUint8(offset, v & 0xFF);
                        view.setUint8(offset + 1, (v >> 8) & 0xFF);
                        view.setUint8(offset + 2, (v >> 16) & 0xFF);
                    }
                    else {
                        view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7FFF, true);
                    }
                }
                offset += bytesPerSample;
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
        else if (key === 'r' && e.shiftKey && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
            const track = getSelectedTrack();
            if (track) toggleArm(track);
        }
        else if (key === 'i' && !e.ctrlKey && !e.metaKey) {
            const track = getSelectedTrack();
            if (track) toggleMonitor(track);
        }
        else if (key === 'c' && !e.ctrlKey && !e.metaKey) { toggleMetronome(); }
        else if (key === 'p' && !e.ctrlKey && !e.metaKey) { togglePunch(); }
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

    /** Key releases: the computer-keyboard instrument ends its notes here. */
    function handleKeyboardUp(e) {
        if (typeof AudioDawInstruments !== 'undefined' && AudioDawInstruments.handleKeyUp) AudioDawInstruments.handleKeyUp(e);
    }

    function showShortcutHelp() {
        const existing = document.querySelector('.daw-shortcut-help');
        if (existing) { existing.remove(); return; }
        const overlay = createDiv(null, 'daw-shortcut-help');
        const rows = [
            ['Space', 'Play / Pause (stops recording)'],
            ['R', 'Start / stop recording'],
            ['L', 'Toggle loop'],
            ['C / P', 'Metronome / punch recording'],
            ['Shift+R / I', 'Arm / monitor the selected track'],
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
        // Key names stay as-is (they name physical keys); only the descriptions are translated.
        overlay.innerHTML = `<div class="daw-shortcut-help-title">${escapeHtml(translate('Keyboard Shortcuts'))}</div>`
            + rows.map(([k, d]) => `<div class="daw-shortcut-row"><kbd>${escapeHtml(k)}</kbd><span>${escapeHtml(translate(d))}</span></div>`).join('')
            + `<div class="daw-shortcut-help-hint">${escapeHtml(translate('Click anywhere to close'))}</div>`;
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

    function resetState() {
        abortRecording();
        stopBeatAudition();
        stopPlayback();
        teardownPlaybackGraph();
        closeAllInputs();
        destroyMixer();
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
        closeAllInputs();
        AudioDawInput.releaseAll();
        destroyMixer();
        if (meterRaf) { cancelAnimationFrame(meterRaf); meterRaf = null; }
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
    // Wires the tab-shown lifecycle (lazy init + mic release on switch-away), keyboard, and autosave flush.
    // No-ops on pages without the AudioLab tab.
    function bootTabMode() {
        const container = document.getElementById('daw_container');
        if (!container || !dawTabLink()) return;
        // Keyboard: document-level, only while the DAW tab is the visible one
        document.addEventListener('keydown', (e) => {
            if (!initialized || !state || container.offsetParent === null) return;
            handleKeyboard(e);
        });
        document.addEventListener('keyup', (e) => {
            if (!initialized || !state || container.offsetParent === null) return;
            handleKeyboardUp(e);
        });
        // Browser tabs die without warning: flush the autosave on page close too
        window.addEventListener('beforeunload', () => flushAutosave());
        window.addEventListener('resize', sizeDawContainer);
        if (typeof $ !== 'undefined') {
            $('#toptablist').on('shown.bs.tab', (e) => {
                if (e.target.id === 'maintab_audiolab') {
                    sizeDawContainer(); // pane just became measurable
                    // Covers hash-nav landing directly on #audiolab too
                    if (!initialized) {
                        ensureInitialized();
                        maybeOfferResume().then(maybeShowStartScreen);
                    }
                } else if (recording) {
                    // Never leave the microphone hot while the DAW is hidden
                    stopRecordingFlow();
                }
            });
        }
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bootTabMode);
    } else {
        bootTabMode();
    }

    return { open, close };
})();
