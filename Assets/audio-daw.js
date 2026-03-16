/**
 * AudioDaw — Multi-track DAW shell for AudioLab.
 * Manages the fullscreen modal, transport bar, playback engine (Web Audio API),
 * track/clip arrangement, scroll sync, undo/redo, and export.
 * Reuses SwarmUI: modalHeader/Footer, quickAppendButton, createDiv, doNoticePopover, escapeHtml.
 * Depends on: AudioDawTimeline, AudioDawTrack, AudioLabCore, AudioLabPlayer.
 */
const AudioDaw = (() => {
    'use strict';

    const MODAL_ID = 'audiolab_modal';
    const MAX_UNDO = 30;

    // ===== DAW STATE =====
    let state = null;
    let modalEl = null;

    // DOM references
    let transportEl = null;
    let rulerContainer = null;
    let trackHeadersEl = null;
    let clipLanesEl = null;
    let playheadEl = null;
    let bottomPanelEl = null;
    let footerEl = null;
    let timeDisplayEl = null;
    let bpmInputEl = null;

    // Runtime
    let timeline = null;
    let audioCtx = null;
    let activeSourceNodes = [];
    let rafId = null;
    let playStartCtxTime = 0;
    let playStartOffset = 0;
    let blobStore = new Map(); // blobKey -> { blob, decodedBuffer }

    function getDefaultState() {
        return {
            tracks: [],
            masterVolume: 1.0,
            bpm: 120,
            timeSignature: [4, 4],
            currentTime: 0,
            isPlaying: false,
            isRecording: false,
            loopEnabled: false,
            loopStart: 0,
            loopEnd: 0,
            zoom: 100,        // pixels per second
            scrollLeft: 0,
            totalDuration: 10,
            selectedTrackId: null,
            selectedClipId: null,
            undoStack: [],
            redoStack: []
        };
    }

    // ===== PUBLIC API =====

    /**
     * Open the DAW with an initial audio source.
     * @param {string} audioSrc - URL or data: URI of audio file
     */
    async function open(audioSrc) {
        if (!modalEl) buildModal();
        resetState();
        $(modalEl).modal('show');
        // Allow modal to fully render before initializing layout
        await sleep(150);
        initLayout();
        renderAllTracks();
        updateTimeDisplay();
        // Show loading spinner while audio decodes
        const overlay = showDawLoadingOverlay('Loading audio...');
        // Create first track and load audio in background
        const track = addTrack();
        try {
            const blob = await fetchAsBlob(audioSrc);
            await addClipToTrack(track, blob, { name: getFilenameFromSrc(audioSrc) });
            updateTotalDuration();
            renderAllTracks();
            updateTimeDisplay();
        } catch (err) {
            console.error('[AudioDaw] Failed to load audio:', err);
        }
        hideDawLoadingOverlay(overlay);
    }

    function close() {
        stopPlayback();
        if (modalEl) $(modalEl).modal('hide');
        destroyAll();
    }

    // ===== MODAL BUILDING =====

    function buildModal() {
        const existing = document.getElementById(MODAL_ID);
        if (existing) existing.remove();

        const bodyHtml = `
        <div class="modal-body daw-body">
            <div class="daw-transport" id="daw_transport"></div>
            <div class="daw-main" id="daw_main">
                <div class="daw-ruler-corner"></div>
                <div class="daw-ruler" id="daw_ruler"></div>
                <div class="daw-track-headers" id="daw_track_headers"></div>
                <div class="daw-header-splitter" id="daw_header_splitter"></div>
                <div class="daw-clip-lanes" id="daw_clip_lanes">
                    <div class="daw-playhead" id="daw_playhead"></div>
                </div>
            </div>
            <div class="daw-split-bar" id="daw_split_bar"></div>
            <div class="daw-bottom-panel" id="daw_bottom_panel"></div>
        </div>`;

        const footerHtml = `
        <div class="modal-footer daw-footer" id="daw_footer"></div>`;

        const html = modalHeader(MODAL_ID, 'Audio Lab') + bodyHtml + footerHtml + modalFooter();
        const wrapper = document.createElement('div');
        wrapper.innerHTML = html;
        document.body.appendChild(wrapper.firstElementChild);

        modalEl = document.getElementById(MODAL_ID);
        modalEl.classList.add('daw-mode');

        // Wire close on backdrop click / escape
        modalEl.addEventListener('keydown', handleKeyboard);
    }

    function initLayout() {
        transportEl = document.getElementById('daw_transport');
        rulerContainer = document.getElementById('daw_ruler');
        trackHeadersEl = document.getElementById('daw_track_headers');
        clipLanesEl = document.getElementById('daw_clip_lanes');
        playheadEl = document.getElementById('daw_playhead');
        bottomPanelEl = document.getElementById('daw_bottom_panel');
        footerEl = document.getElementById('daw_footer');

        buildTransport();
        buildFooter();
        initTimeline();
        buildBottomPanel();
        setupScrollSync();
        initBottomSplitter();
        initHeaderSplitter();
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

    function initBottomSplitter() {
        const splitBar = document.getElementById('daw_split_bar');
        if (!splitBar || !bottomPanelEl) return;
        let dragging = false;
        const savedHeight = localStorage.getItem('daw_bottom_panel_height');
        if (savedHeight) {
            bottomPanelEl.style.height = savedHeight + 'px';
        }
        splitBar.addEventListener('mousedown', (e) => {
            dragging = true;
            e.preventDefault();
        });
        splitBar.addEventListener('touchstart', (e) => {
            dragging = true;
            e.preventDefault();
        }, { passive: false });
        const onMove = (clientY) => {
            if (!dragging) return;
            const bodyRect = bottomPanelEl.parentElement.getBoundingClientRect();
            const footerHeight = footerEl ? footerEl.offsetHeight : 0;
            const newHeight = Math.max(80, Math.min(bodyRect.bottom - clientY - footerHeight, bodyRect.height * 0.7));
            bottomPanelEl.style.height = newHeight + 'px';
        };
        document.addEventListener('mousemove', (e) => onMove(e.clientY));
        document.addEventListener('touchmove', (e) => onMove(e.touches[0].clientY));
        const onUp = () => {
            if (dragging) {
                dragging = false;
                localStorage.setItem('daw_bottom_panel_height', bottomPanelEl.offsetHeight);
            }
        };
        document.addEventListener('mouseup', onUp);
        document.addEventListener('touchend', onUp);
    }

    // ===== TRANSPORT BAR =====

    function buildTransport() {
        transportEl.innerHTML = '';

        // Record
        const recBtn = document.createElement('button');
        recBtn.className = 'daw-transport-btn daw-btn-rec';
        recBtn.innerHTML = '&#x25CF;';
        recBtn.title = 'Record (future)';
        recBtn.disabled = true;
        transportEl.appendChild(recBtn);

        // Rewind
        quickAppendButton(transportEl, '&#x23EE;', () => seekTo(0), ' daw-transport-btn', 'Rewind to start');

        // Play/Pause
        const playBtn = document.createElement('button');
        playBtn.className = 'daw-transport-btn daw-btn-play';
        playBtn.innerHTML = '&#x25B6;';
        playBtn.title = 'Play / Pause (Space)';
        playBtn.addEventListener('click', togglePlayback);
        transportEl.appendChild(playBtn);

        // Stop
        quickAppendButton(transportEl, '&#x25A0;', () => { stopPlayback(); seekTo(0); }, ' daw-transport-btn', 'Stop');

        // Fast Forward
        quickAppendButton(transportEl, '&#x23ED;', () => seekTo(state.totalDuration), ' daw-transport-btn', 'Go to end');

        // Separator
        const sep1 = createDiv(null, 'alp-separator');
        transportEl.appendChild(sep1);

        // Loop toggle
        const loopBtn = document.createElement('button');
        loopBtn.className = 'daw-transport-btn daw-btn-loop';
        loopBtn.innerHTML = '&#x1F501;';
        loopBtn.title = 'Toggle Loop';
        loopBtn.addEventListener('click', () => {
            state.loopEnabled = !state.loopEnabled;
            loopBtn.classList.toggle('active', state.loopEnabled);
            if (timeline) timeline.setLoop(state.loopEnabled, state.loopStart, state.loopEnd);
        });
        transportEl.appendChild(loopBtn);

        // Separator
        const sep2 = createDiv(null, 'alp-separator');
        transportEl.appendChild(sep2);

        // Time display
        timeDisplayEl = createSpan(null, 'daw-transport-time');
        timeDisplayEl.textContent = '0:00.0 / 0:00.0';
        transportEl.appendChild(timeDisplayEl);

        // Spacer
        const spacer = createDiv(null, 'daw-transport-spacer');
        transportEl.appendChild(spacer);

        // BPM
        const bpmLabel = createSpan(null, 'daw-transport-bpm-label');
        bpmLabel.textContent = 'BPM';
        bpmInputEl = document.createElement('input');
        bpmInputEl.type = 'number';
        bpmInputEl.className = 'daw-transport-bpm';
        bpmInputEl.value = state.bpm;
        bpmInputEl.min = 20;
        bpmInputEl.max = 300;
        bpmInputEl.addEventListener('change', (e) => {
            state.bpm = parseInt(e.target.value) || 120;
        });
        const bpmGroup = createDiv(null, 'daw-transport-bpm-group');
        bpmGroup.appendChild(bpmInputEl);
        bpmGroup.appendChild(bpmLabel);
        transportEl.appendChild(bpmGroup);

        // Zoom slider
        const zoomGroup = createDiv(null, 'daw-transport-zoom-group');
        const zoomLabel = createSpan(null, 'daw-transport-zoom-label');
        zoomLabel.textContent = 'Zoom';
        const zoomSlider = document.createElement('input');
        zoomSlider.type = 'range';
        zoomSlider.className = 'daw-transport-zoom';
        zoomSlider.min = '10';
        zoomSlider.max = '500';
        zoomSlider.value = state.zoom;
        zoomSlider.addEventListener('input', (e) => {
            setZoom(parseInt(e.target.value));
        });
        zoomGroup.appendChild(zoomLabel);
        zoomGroup.appendChild(zoomSlider);
        transportEl.appendChild(zoomGroup);
    }

    // ===== FOOTER =====

    function buildFooter() {
        footerEl.innerHTML = '';
        quickAppendButton(footerEl, 'Import Audio', importAudioToTrack, ' basic-button', 'Import audio files (each file gets its own track)');

        // Spacer
        const spacer = createDiv(null, 'daw-footer-spacer');
        footerEl.appendChild(spacer);

        // Export dropdown
        const exportGroup = createDiv(null, 'daw-export-group');
        exportGroup.style.cssText = 'display:flex;gap:0;';
        quickAppendButton(exportGroup, 'Export WAV', () => doExportMixdown('wav'), ' btn btn-primary basic-button', 'Export all tracks as WAV');
        const exportDropdown = document.createElement('button');
        exportDropdown.className = 'btn btn-primary basic-button';
        exportDropdown.innerHTML = '&#x25BC;';
        exportDropdown.title = 'Export format options';
        exportDropdown.style.cssText = 'padding:0.3rem;border-left:1px solid var(--shadow);';
        exportDropdown.addEventListener('click', (e) => showExportMenu(e));
        exportGroup.appendChild(exportDropdown);
        footerEl.appendChild(exportGroup);

        quickAppendButton(footerEl, 'Close', close, ' btn btn-secondary basic-button', 'Close DAW');
    }

    // ===== TIMELINE =====

    function initTimeline() {
        rulerContainer.innerHTML = '';
        timeline = AudioDawTimeline.create(rulerContainer, {
            zoom: state.zoom,
            height: 30,
            totalDuration: state.totalDuration
        });
        timeline.on('seek', (time) => {
            seekTo(time);
        });
        timeline.on('loopChange', (start, end) => {
            state.loopStart = start;
            state.loopEnd = end;
        });
    }

    // ===== BOTTOM PANEL =====

    let activeBottomTab = 'clip-editor';

    function buildBottomPanel() {
        if (!bottomPanelEl) return;
        bottomPanelEl.innerHTML = '';

        // Tab bar
        const tabBar = createDiv(null, 'daw-bottom-tabs');
        const tabs = [
            { id: 'clip-editor', label: 'Clip Editor' },
            { id: 'mixer', label: 'Mixer' },
            { id: 'stems', label: 'Stems' },
            { id: 'apply-model', label: 'Apply to Model' }
        ];
        for (const tab of tabs) {
            const btn = document.createElement('button');
            btn.className = 'daw-bottom-tab' + (tab.id === activeBottomTab ? ' active' : '');
            btn.textContent = tab.label;
            btn.dataset.tab = tab.id;
            btn.addEventListener('click', () => {
                activeBottomTab = tab.id;
                tabBar.querySelectorAll('.daw-bottom-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab.id));
                bottomPanelEl.querySelectorAll('.daw-bottom-tab-content').forEach(c => c.hidden = c.dataset.tab !== tab.id);
            });
            tabBar.appendChild(btn);
        }
        bottomPanelEl.appendChild(tabBar);

        // Clip Editor tab content
        const clipEditorContent = createDiv(null, 'daw-bottom-tab-content');
        clipEditorContent.dataset.tab = 'clip-editor';
        clipEditorContent.hidden = activeBottomTab !== 'clip-editor';
        bottomPanelEl.appendChild(clipEditorContent);

        // Mixer tab content
        const mixerContent = createDiv(null, 'daw-bottom-tab-content');
        mixerContent.dataset.tab = 'mixer';
        mixerContent.hidden = activeBottomTab !== 'mixer';
        bottomPanelEl.appendChild(mixerContent);

        // Stems tab content
        const stemsContent = createDiv(null, 'daw-bottom-tab-content');
        stemsContent.dataset.tab = 'stems';
        stemsContent.hidden = activeBottomTab !== 'stems';
        bottomPanelEl.appendChild(stemsContent);

        // Apply to Model tab content
        const applyContent = createDiv(null, 'daw-bottom-tab-content');
        applyContent.dataset.tab = 'apply-model';
        applyContent.hidden = activeBottomTab !== 'apply-model';
        bottomPanelEl.appendChild(applyContent);

        updateBottomPanel();
    }

    function updateBottomPanel() {
        if (!bottomPanelEl) return;

        // Update Clip Editor tab
        const clipEditorContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="clip-editor"]');
        if (clipEditorContent) {
            clipEditorContent.innerHTML = '';
            const selectedClip = findClipById(state.selectedClipId);
            if (selectedClip) {
                const { clip, track } = selectedClip;
                const info = createDiv(null, 'daw-clip-editor');

                const infoRow = createDiv(null, 'daw-clip-editor-info');
                infoRow.innerHTML = `<strong>${escapeHtml(clip.name)}</strong> | Track: ${escapeHtml(track.name)} | Duration: ${formatTimePrecise(clip.duration)}s | Start: ${formatTimePrecise(clip.startTime)}s`;
                info.appendChild(infoRow);

                // Re-lookup clip at click time to avoid stale closures
                const getClip = () => findClipById(state.selectedClipId);
                const actions = createDiv(null, 'daw-clip-editor-actions');
                const splitBtn = document.createElement('button');
                splitBtn.className = 'basic-button btn-sm';
                splitBtn.textContent = 'Split at Playhead';
                splitBtn.title = 'Split clip at current playhead position';
                splitBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    console.log('[AudioDaw] Split button clicked, selectedClipId:', state.selectedClipId);
                    const sel = getClip();
                    if (sel) {
                        doSplitClip(sel.clip, sel.track);
                    } else {
                        console.warn('[AudioDaw] No clip found for split');
                    }
                });
                actions.appendChild(splitBtn);
                const dupBtn = document.createElement('button');
                dupBtn.className = 'basic-button btn-sm';
                dupBtn.textContent = 'Duplicate';
                dupBtn.title = 'Duplicate this clip';
                dupBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    console.log('[AudioDaw] Duplicate button clicked, selectedClipId:', state.selectedClipId);
                    const sel = getClip();
                    if (sel) {
                        doDuplicateClip(sel.clip, sel.track);
                    } else {
                        console.warn('[AudioDaw] No clip found for duplicate');
                    }
                });
                actions.appendChild(dupBtn);
                const delBtn = document.createElement('button');
                delBtn.className = 'basic-button btn-sm';
                delBtn.textContent = 'Delete';
                delBtn.title = 'Delete this clip';
                delBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    console.log('[AudioDaw] Delete button clicked, selectedClipId:', state.selectedClipId);
                    const sel = getClip();
                    if (sel) {
                        doDeleteClip(sel.clip, sel.track);
                    } else {
                        console.warn('[AudioDaw] No clip found for delete');
                    }
                });
                actions.appendChild(delBtn);
                const muteBtn = document.createElement('button');
                muteBtn.className = 'basic-button btn-sm';
                muteBtn.textContent = clip.muted ? 'Unmute' : 'Mute';
                muteBtn.title = 'Toggle clip mute';
                muteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    console.log('[AudioDaw] Mute button clicked, selectedClipId:', state.selectedClipId);
                    const sel = getClip();
                    if (sel) {
                        sel.clip.muted = !sel.clip.muted;
                        renderAllTracks();
                        updateBottomPanel();
                        if (typeof doNoticePopover === 'function') {
                            doNoticePopover(sel.clip.muted ? 'Clip muted' : 'Clip unmuted', 'notice-pop-green');
                        }
                    } else {
                        console.warn('[AudioDaw] No clip found for mute');
                    }
                });
                actions.appendChild(muteBtn);
                info.appendChild(actions);

                clipEditorContent.appendChild(info);
            } else {
                clipEditorContent.innerHTML = '<div style="color:var(--text-soft);font-size:0.8rem;padding:0.5rem;">Select a clip to edit</div>';
            }
        }

        // Update Mixer tab
        const mixerContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="mixer"]');
        if (mixerContent) {
            if (typeof AudioDawMixer !== 'undefined') {
                AudioDawMixer.render(mixerContent, state, (prop, val) => {
                    if (prop === 'masterVolume') { state.masterVolume = val; updatePlaybackGains(); }
                    else if (prop === 'mute' || prop === 'solo' || prop === 'volume') { updatePlaybackGains(); }
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

        // Update Apply to Model tab
        const applyContent = bottomPanelEl.querySelector('.daw-bottom-tab-content[data-tab="apply-model"]');
        if (applyContent) {
            applyContent.innerHTML = '';
            renderApplyToModel(applyContent);
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

    /** Render the Apply to Model tab content. */
    function renderApplyToModel(container) {
        const section = createDiv(null, 'daw-clip-editor');

        const selectedClip = findClipById(state.selectedClipId);
        const clipLabel = selectedClip ? escapeHtml(selectedClip.clip.name) : 'No clip selected';
        const clipDuration = selectedClip ? selectedClip.clip.duration : 0;

        // Info row
        const infoRow = createDiv(null, 'daw-clip-editor-info');
        infoRow.innerHTML = `Source: <strong>${clipLabel}</strong>`;
        if (clipDuration > 0) {
            infoRow.innerHTML += ` | ${formatTimePrecise(clipDuration)}s`;
            const durationHint = clipDuration >= 3 && clipDuration <= 15
                ? ' <span style="color:var(--green,#5a5);">(good for voice ref)</span>'
                : clipDuration > 15
                    ? ' <span style="color:var(--yellow,#a85);">(long — consider trimming)</span>'
                    : ' <span style="color:var(--yellow,#a85);">(short — may affect quality)</span>';
            infoRow.innerHTML += durationHint;
        }
        section.appendChild(infoRow);

        // Reference text input
        const refRow = createDiv(null, 'daw-clip-editor-info');
        refRow.style.marginTop = '0.3rem';
        const refLabel = document.createElement('label');
        refLabel.style.cssText = 'font-size:0.8rem;color:var(--text);white-space:nowrap;';
        refLabel.textContent = 'Reference Text:';
        const refInput = document.createElement('input');
        refInput.type = 'text';
        refInput.placeholder = 'Transcript of the audio (optional, improves quality)';
        refInput.style.cssText = 'flex:1;padding:0.2rem 0.4rem;border:1px solid var(--shadow);border-radius:0.25rem;background:var(--background);color:var(--text);font-size:0.8rem;';
        refRow.appendChild(refLabel);
        refRow.appendChild(refInput);
        section.appendChild(refRow);

        // Action buttons
        const actions = createDiv(null, 'daw-clip-editor-actions');
        actions.style.marginTop = '0.3rem';

        quickAppendButton(actions, 'Apply Selected Clip as Voice Ref', async () => {
            if (!selectedClip) {
                if (typeof doNoticePopover === 'function') doNoticePopover('Select a clip first', 'notice-pop-yellow');
                return;
            }
            await applyClipToParam(selectedClip.clip, 'referenceaudio', refInput.value);
        }, ' basic-button btn-sm', 'Set selected clip as voice reference for current model');

        quickAppendButton(actions, 'Apply Mixdown as Voice Ref', async () => {
            const mixBlob = await renderMixdownBlob();
            if (!mixBlob) return;
            const tmpClip = { blob: mixBlob, name: 'Mixdown' };
            await applyClipToParam(tmpClip, 'referenceaudio', refInput.value);
        }, ' basic-button btn-sm', 'Render and set mixdown as voice reference');

        section.appendChild(actions);
        container.appendChild(section);
    }

    /** Render the Stems (Demucs) tab content. */
    function renderStemsPanel(container) {
        const section = createDiv(null, 'daw-stems-panel');

        // Header with explanation
        const header = createDiv(null, 'daw-stems-header');
        header.innerHTML = '<strong>Stem Separation (Demucs)</strong>';
        section.appendChild(header);

        const desc = createDiv(null, 'daw-stems-desc');
        desc.textContent = 'Separate audio into individual tracks — vocals, drums, bass, and more. Uses AI-powered source separation to split a mixed audio clip into its component parts. Each stem becomes a new track in the DAW.';
        section.appendChild(desc);

        // Always show controls — the actual API call will report if Demucs isn't available
        renderStemsControls(section);

        container.appendChild(section);
    }

    /** Render the Stems controls (model picker + separate button) when Demucs is installed. */
    function renderStemsControls(section) {
        const controls = createDiv(null, 'daw-stems-controls');

        // Model picker
        const modelRow = createDiv(null, 'daw-stems-model-row');
        const modelLabel = document.createElement('label');
        modelLabel.style.cssText = 'font-size:0.8rem;color:var(--text);white-space:nowrap;';
        modelLabel.textContent = 'Model:';
        const modelSelect = document.createElement('select');
        modelSelect.style.cssText = 'padding:0.2rem 0.4rem;border:1px solid var(--shadow);border-radius:0.25rem;background:var(--background);color:var(--text);font-size:0.8rem;';
        const models = [
            { id: 'htdemucs', label: 'HTDemucs — 4 stems (vocals, drums, bass, other)' },
            { id: 'htdemucs_ft', label: 'HTDemucs Fine-tuned — 4 stems (best quality)' },
            { id: 'htdemucs_6s', label: 'HTDemucs 6-stem — vocals, drums, bass, guitar, piano, other' }
        ];
        for (const m of models) {
            const opt = document.createElement('option');
            opt.value = m.id;
            opt.textContent = m.label;
            modelSelect.appendChild(opt);
        }
        modelRow.appendChild(modelLabel);
        modelRow.appendChild(modelSelect);
        controls.appendChild(modelRow);

        // Selected clip info + separate button
        const selectedClip = findClipById(state.selectedClipId);
        const actionRow = createDiv(null, 'daw-stems-action-row');

        if (selectedClip) {
            const clipInfo = createSpan(null);
            clipInfo.style.cssText = 'font-size:0.8rem;color:var(--text-soft);';
            clipInfo.innerHTML = `Selected: <strong style="color:var(--text);">${escapeHtml(selectedClip.clip.name)}</strong> (${formatTimePrecise(selectedClip.clip.duration)}s)`;
            actionRow.appendChild(clipInfo);

            const sepBtn = document.createElement('button');
            sepBtn.className = 'basic-button btn-sm';
            sepBtn.textContent = 'Separate Stems';
            sepBtn.addEventListener('click', () => {
                doSeparateStems(selectedClip.clip, selectedClip.track, modelSelect.value);
            });
            actionRow.appendChild(sepBtn);
        } else {
            actionRow.innerHTML = '<span style="color:var(--text-soft);font-size:0.8rem;">Select a clip to separate into stems</span>';
        }

        controls.appendChild(actionRow);
        section.appendChild(controls);
    }

    /** Apply a clip's audio data to a SwarmUI model parameter input. */
    async function applyClipToParam(clip, paramId, refText) {
        try {
            const base64 = await AudioLabCore.readAsBase64(clip.blob);
            const inputEl = document.getElementById(`input_${paramId}`);
            if (inputEl) {
                inputEl.value = `data:audio/wav;base64,${base64}`;
                triggerChangeFor(inputEl);
            }
            // Also set reference text if provided
            if (refText) {
                const refTextEl = document.getElementById('input_referencetext');
                if (refTextEl) {
                    refTextEl.value = refText;
                    triggerChangeFor(refTextEl);
                }
            }
            if (typeof doNoticePopover === 'function') {
                doNoticePopover(`Applied "${clip.name}" to model`, 'notice-pop-green');
            }
        } catch (err) {
            console.error('[AudioDaw] Apply to model failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Failed to apply: ' + err.message, 'notice-pop-red');
            }
        }
    }

    /** Render mixdown to a Blob (for Apply to Model). */
    async function renderMixdownBlob() {
        if (state.tracks.length === 0) return null;
        const sampleRate = 44100;
        const totalSamples = Math.ceil(state.totalDuration * sampleRate);
        const offlineCtx = new OfflineAudioContext(2, totalSamples, sampleRate);
        const soloActive = hasSoloTracks();

        for (const track of state.tracks) {
            if (track.muted) continue;
            if (soloActive && !track.soloed) continue;
            const gainNode = offlineCtx.createGain();
            gainNode.gain.value = track.volume * state.masterVolume;
            gainNode.connect(offlineCtx.destination);
            for (const clip of track.clips) {
                if (clip.muted || !clip.decodedBuffer) continue;
                const source = offlineCtx.createBufferSource();
                source.buffer = clip.decodedBuffer;
                source.connect(gainNode);
                source.start(clip.startTime + clip.offset, clip.offset,
                    clip.duration - clip.offset - clip.trimEnd);
            }
        }

        try {
            const rendered = await offlineCtx.startRendering();
            return audioBufferToWav(rendered);
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
        clipLanesEl.addEventListener('scroll', () => {
            state.scrollLeft = clipLanesEl.scrollLeft;
            // Sync ruler horizontal scroll
            if (rulerContainer) rulerContainer.scrollLeft = clipLanesEl.scrollLeft;
            // Sync track headers vertical scroll
            if (trackHeadersEl) trackHeadersEl.scrollTop = clipLanesEl.scrollTop;
        });

        // Click on empty clip lane area to seek
        clipLanesEl.addEventListener('click', (e) => {
            if (e.target === clipLanesEl || e.target.classList.contains('daw-track-lane')) {
                const rect = clipLanesEl.getBoundingClientRect();
                const x = e.clientX - rect.left + clipLanesEl.scrollLeft;
                const time = x / state.zoom;
                seekTo(Math.max(0, Math.min(time, state.totalDuration)));
            }
        });
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
        // Clean up clip blob refs
        for (const clip of track.clips) {
            // Only remove from store if no other clip references the same blobKey
            const otherRefs = state.tracks.some(t =>
                t.id !== trackId && t.clips.some(c => c.blobKey === clip.blobKey)
            );
            if (!otherRefs) blobStore.delete(clip.blobKey);
        }
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
            onMute: () => { updatePlaybackGains(); },
            onSolo: () => { updatePlaybackGains(); },
            onVolume: () => { updatePlaybackGains(); },
            onArm: () => {},
            onSelect: (track) => {
                state.selectedTrackId = track.id;
                updateTrackSelection();
                updateBottomPanel();
            },
            onRename: () => {},
            onRemove: (track) => {
                if (state.tracks.length <= 1) return; // keep at least one track
                pushUndo();
                removeTrack(track.id);
                updateTotalDuration();
                renderAllTracks();
            }
        };

        const clipCallbacks = {
            onClipSelect: (clip, track) => {
                state.selectedClipId = clip.id;
                state.selectedTrackId = track.id;
                updateTrackSelection();
                updateBottomPanel();
                renderAllTracks(); // re-render to update selection highlight
            },
            onClipMove: () => {
                updateTotalDuration();
            },
            onClipContext: (e, clip, track) => {
                showClipContextMenu(e, clip, track);
            },
            onClipCrossTrack: (clip, srcTrack, targetTrackId) => {
                const targetTrack = state.tracks.find(t => t.id === targetTrackId);
                if (!targetTrack) return;
                pushUndo();
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
            }
        };

        for (const track of state.tracks) {
            const header = AudioDawTrack.buildTrackHeader(track, trackCallbacks);
            trackHeadersEl.appendChild(header);

            const lane = AudioDawTrack.buildClipLane(track, state.zoom, clipCallbacks);
            clipLanesEl.appendChild(lane);

            AudioDawTrack.renderClips(track, state.zoom, clipCallbacks, state.selectedClipId);
        }

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
            clipLanesEl.querySelectorAll('.daw-track-lane').forEach(lane => {
                lane.style.minWidth = minWidth + 'px';
            });
        }
    }

    function updateTotalDuration() {
        let max = 10; // minimum 10 seconds
        for (const track of state.tracks) {
            const td = AudioDawTrack.getTrackDuration(track);
            if (td > max) max = td;
        }
        state.totalDuration = max + 5; // 5 second padding
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

    /** Update gain nodes for all active sources to reflect current mute/solo state. */
    function updatePlaybackGains() {
        if (!state.isPlaying || activeSourceNodes.length === 0) return;
        const soloActive = hasSoloTracks();
        for (const node of activeSourceNodes) {
            const t = node.track;
            const audible = !t.muted && (!soloActive || t.soloed);
            node.gainNode.gain.value = audible ? t.volume * state.masterVolume : 0;
        }
    }

    function togglePlayback() {
        if (state.isPlaying) {
            stopPlayback();
        } else {
            startPlayback();
        }
    }

    function startPlayback() {
        if (state.isPlaying) return;
        state.isPlaying = true;
        updatePlayButton(true);

        const ctx = getAudioContext();
        if (ctx.state === 'suspended') ctx.resume();

        playStartCtxTime = ctx.currentTime;
        playStartOffset = state.currentTime;
        const soloActive = hasSoloTracks();

        // Schedule all clips
        for (const track of state.tracks) {
            if (track.muted) continue;
            if (soloActive && !track.soloed) continue;

            const gainNode = ctx.createGain();
            gainNode.gain.value = track.volume * state.masterVolume;
            gainNode.connect(ctx.destination);

            for (const clip of track.clips) {
                if (clip.muted || !clip.decodedBuffer) continue;

                const source = ctx.createBufferSource();
                source.buffer = clip.decodedBuffer;

                const clipStart = clip.startTime;
                const clipDuration = clip.duration - clip.offset - clip.trimEnd;
                const clipEnd = clipStart + clipDuration;

                // Skip clips entirely before current time
                if (clipEnd <= state.currentTime) continue;

                // How far into the clip we start
                const offsetIntoClip = Math.max(0, state.currentTime - clipStart) + clip.offset;
                // When to start playing (relative to now)
                const whenToStart = Math.max(0, clipStart - state.currentTime);
                // How long to play
                const playDuration = clipDuration - Math.max(0, state.currentTime - clipStart);

                if (playDuration <= 0) continue;

                source.connect(gainNode);
                source.start(ctx.currentTime + whenToStart, offsetIntoClip, playDuration);

                activeSourceNodes.push({ source, gainNode, track });
            }
        }

        animatePlayhead();
    }

    function stopPlayback() {
        if (!state || !state.isPlaying) return;
        state.isPlaying = false;
        updatePlayButton(false);
        cancelAnimationFrame(rafId);

        for (const node of activeSourceNodes) {
            try { node.source.stop(); } catch (_) {}
            try { node.source.disconnect(); } catch (_) {}
            try { node.gainNode.disconnect(); } catch (_) {}
        }
        activeSourceNodes = [];
    }

    function seekTo(time) {
        const wasPlaying = state.isPlaying;
        if (wasPlaying) stopPlayback();
        state.currentTime = Math.max(0, Math.min(time, state.totalDuration));
        updatePlayheadPosition();
        updateTimeDisplay();
        if (wasPlaying) startPlayback();
    }

    function animatePlayhead() {
        if (!state.isPlaying) return;

        const ctx = getAudioContext();
        const elapsed = ctx.currentTime - playStartCtxTime;
        state.currentTime = playStartOffset + elapsed;

        // Loop wrap
        if (state.loopEnabled && state.loopEnd > state.loopStart && state.currentTime >= state.loopEnd) {
            seekTo(state.loopStart);
            return;
        }

        // End of timeline
        if (state.currentTime >= state.totalDuration) {
            stopPlayback();
            state.currentTime = 0;
            updatePlayheadPosition();
            updateTimeDisplay();
            return;
        }

        updatePlayheadPosition();
        updateTimeDisplay();
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
        if (btn) btn.innerHTML = playing ? '&#x23F8;' : '&#x25B6;';
    }

    function updateTimeDisplay() {
        if (!timeDisplayEl) return;
        const current = formatTimePrecise(state.currentTime);
        const total = formatTimePrecise(state.totalDuration);
        timeDisplayEl.textContent = `${current} / ${total}`;
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
            hideDawLoadingOverlay(overlay);
        };
        input.click();
    }

    // ===== CLIP CONTEXT MENU =====

    function showClipContextMenu(e, clip, track) {
        // Simple context menu using a temporary div (Phase 4 will use AdvancedPopover)
        const existing = document.querySelector('.daw-context-menu');
        if (existing) existing.remove();

        const menu = createDiv(null, 'daw-context-menu');
        menu.style.position = 'fixed';
        menu.style.left = e.clientX + 'px';
        menu.style.top = e.clientY + 'px';
        menu.style.zIndex = '99999';

        const items = [
            { label: 'Split at Playhead', action: () => doSplitClip(clip, track) },
            { label: 'Duplicate', action: () => doDuplicateClip(clip, track) },
            { label: 'Delete', action: () => doDeleteClip(clip, track) },
            { label: clip.muted ? 'Unmute Clip' : 'Mute Clip', action: () => {
                clip.muted = !clip.muted;
                renderAllTracks();
            }},
            { label: 'Separate Stems (Demucs)', action: () => doSeparateStems(clip, track) }
        ];

        for (const item of items) {
            const btn = document.createElement('button');
            btn.className = 'daw-context-item';
            btn.textContent = item.label;
            btn.addEventListener('click', () => {
                menu.remove();
                item.action();
            });
            menu.appendChild(btn);
        }

        document.body.appendChild(menu);

        // Close on click outside
        const closeMenu = (ce) => {
            if (!menu.contains(ce.target)) {
                menu.remove();
                document.removeEventListener('click', closeMenu, true);
            }
        };
        setTimeout(() => document.addEventListener('click', closeMenu, true), 0);
    }

    // ===== CLIP OPERATIONS =====

    async function doSplitClip(clip, track) {
        const splitTime = state.currentTime - clip.startTime;
        if (splitTime <= 0 || splitTime >= clip.duration) {
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

            // Create two new clips replacing the original
            const clipA = AudioDawTrack.createClip(parts.before, {
                name: clip.name + ' (A)',
                startTime: clip.startTime,
                color: clip.color
            });
            await AudioDawTrack.decodeClip(clipA);
            blobStore.set(clipA.blobKey, { blob: parts.before, decodedBuffer: clipA.decodedBuffer });

            const clipB = AudioDawTrack.createClip(parts.after, {
                name: clip.name + ' (B)',
                startTime: clip.startTime + splitTime,
                color: clip.color
            });
            await AudioDawTrack.decodeClip(clipB);
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

    function doDuplicateClip(clip, track) {
        console.log('[AudioDaw] doDuplicateClip called', clip?.id, track?.id);
        try {
            pushUndo();
            console.log('[AudioDaw] pushUndo done');
            const newClip = AudioDawTrack.createClip(clip.blob, {
                name: clip.name + ' (copy)',
                startTime: clip.startTime + clip.duration + 0.5,
                color: clip.color,
                blobKey: clip.blobKey // share the same blob
            });
            newClip.decodedBuffer = clip.decodedBuffer;
            newClip.duration = clip.duration;
            newClip.offset = clip.offset;
            newClip.trimEnd = clip.trimEnd;
            newClip.gain = clip.gain;
            track.clips.push(newClip);
            state.selectedClipId = newClip.id;
            console.log('[AudioDaw] new clip created:', newClip.id, 'at', newClip.startTime);
            updateTotalDuration();
            console.log('[AudioDaw] about to renderAllTracks');
            renderAllTracks();
            console.log('[AudioDaw] renderAllTracks done');
            updateBottomPanel();
            console.log('[AudioDaw] duplicate complete');
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
        console.log('[AudioDaw] doDeleteClip called', clip?.id, track?.id);
        try {
            pushUndo();
            const idx = track.clips.indexOf(clip);
            console.log('[AudioDaw] clip index in track:', idx);
            if (idx === -1) {
                console.error('[AudioDaw] clip not found in track during delete');
                return;
            }
            track.clips.splice(idx, 1);

            // Clean up element
            const entry = track.clipElements.get(clip.id);
            if (entry?.ws) entry.ws.destroy();
            if (entry?.el) entry.el.remove();
            track.clipElements.delete(clip.id);

            // Remove from blob store if no other references
            const otherRefs = state.tracks.some(t =>
                t.clips.some(c => c.blobKey === clip.blobKey)
            );
            if (!otherRefs) blobStore.delete(clip.blobKey);

            if (state.selectedClipId === clip.id) state.selectedClipId = null;
            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();
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
            console.log('[AudioDaw] GetInstallationStatus response:', JSON.stringify(result));
            const providers = result.providers || {};
            const val = providers['demucs_fx'];
            console.log('[AudioDaw] demucs_fx value:', val, typeof val);
            // Backend returns raw boolean (true/false), not an object
            demucsInstallStatus = val === true;
        } catch (err) {
            console.warn('[AudioDaw] Failed to check Demucs status:', err);
            demucsInstallStatus = false;
        }
        return demucsInstallStatus;
    }

    /**
     * Separate a clip into stems using Demucs via the backend.
     * Creates new tracks for each stem at the same position as the original clip.
     * @param {Object} clip - The clip to separate
     * @param {Object} track - The track containing the clip
     * @param {string} [modelName='htdemucs'] - Demucs model
     */
    function showDawLoadingOverlay(message = 'Processing...') {
        const body = document.querySelector('.daw-body');
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

    async function doSeparateStems(clip, track, modelName = 'htdemucs') {
        if (!clip || !clip.blob) {
            if (typeof doNoticePopover === 'function') doNoticePopover('No clip to separate', 'notice-pop-yellow');
            return;
        }

        const overlay = showDawLoadingOverlay('Separating stems... this may take a moment');

        try {
            const base64 = await AudioLabCore.readAsBase64(clip.blob);
            const result = await AudioLabAPI.callAPI('ProcessAudio', {
                provider_id: 'demucs_fx',
                args: {
                    audio_data: base64,
                    model_name: modelName
                }
            });

            hideDawLoadingOverlay(overlay);

            if (!result.success || !result.stems) {
                throw new Error(result.error || 'Stem separation failed');
            }

            pushUndo();

            const stemNames = result.metadata?.stem_names || Object.keys(result.stems);
            const stemColors = {
                vocals: '#cc5de8',
                drums: '#ff922b',
                bass: '#22b8cf',
                other: '#82c91e',
                guitar: '#ffd43b',
                piano: '#4a9eff'
            };

            for (const stemName of stemNames) {
                const stemB64 = result.stems[stemName];
                if (!stemB64) continue;

                const stemBlob = AudioLabCore.base64ToBlob(stemB64, 'audio/wav');
                const newTrack = addTrack({
                    name: `${stemName.charAt(0).toUpperCase() + stemName.slice(1)} — ${clip.name}`,
                    color: stemColors[stemName] || undefined
                });
                await addClipToTrack(newTrack, stemBlob, {
                    name: stemName,
                    startTime: clip.startTime
                });
            }

            // Mute the original clip so stems are heard instead
            clip.muted = true;

            updateTotalDuration();
            renderAllTracks();
            updateBottomPanel();

            if (typeof doNoticePopover === 'function') {
                doNoticePopover(`Separated into ${stemNames.length} stems`, 'notice-pop-green');
            }
        } catch (err) {
            hideDawLoadingOverlay(overlay);
            console.error('[AudioDaw] Stem separation failed:', err);
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Stem separation failed: ' + err.message, 'notice-pop-red');
            }
        }
    }

    // ===== UNDO / REDO =====

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
                clip.muted = cs.muted;
                clip.decodedBuffer = stored.decodedBuffer;
                track.clips.push(clip);
            }
            state.tracks.push(track);
        }

        updateTotalDuration();
        renderAllTracks();
    }

    // ===== EXPORT =====

    function showExportMenu(e) {
        const existing = document.querySelector('.daw-context-menu');
        if (existing) existing.remove();

        const menu = createDiv(null, 'daw-context-menu');
        menu.style.position = 'fixed';
        menu.style.left = e.clientX + 'px';
        menu.style.top = (e.clientY - 180) + 'px';
        menu.style.zIndex = '99999';

        const formats = [
            { id: 'wav', label: 'WAV (Lossless)' },
            { id: 'mp3', label: 'MP3 (192kbps)' },
            { id: 'ogg', label: 'OGG Vorbis' },
            { id: 'flac', label: 'FLAC (Lossless)' },
            { id: 'aac', label: 'AAC (192kbps)' }
        ];

        for (const fmt of formats) {
            const btn = document.createElement('button');
            btn.className = 'daw-context-item';
            btn.textContent = fmt.label;
            btn.addEventListener('click', () => {
                menu.remove();
                doExportMixdown(fmt.id);
            });
            menu.appendChild(btn);
        }

        document.body.appendChild(menu);
        const closeMenu = (ce) => {
            if (!menu.contains(ce.target)) {
                menu.remove();
                document.removeEventListener('click', closeMenu, true);
            }
        };
        setTimeout(() => document.addEventListener('click', closeMenu, true), 0);
    }

    async function doExportMixdown(format = 'wav') {
        if (state.tracks.length === 0) return;

        const sampleRate = 44100;
        const totalSamples = Math.ceil(state.totalDuration * sampleRate);
        const offlineCtx = new OfflineAudioContext(2, totalSamples, sampleRate);
        const soloActive = hasSoloTracks();

        for (const track of state.tracks) {
            if (track.muted) continue;
            if (soloActive && !track.soloed) continue;

            const gainNode = offlineCtx.createGain();
            gainNode.gain.value = track.volume * state.masterVolume;
            gainNode.connect(offlineCtx.destination);

            for (const clip of track.clips) {
                if (clip.muted || !clip.decodedBuffer) continue;

                const source = offlineCtx.createBufferSource();
                source.buffer = clip.decodedBuffer;
                source.connect(gainNode);
                source.start(clip.startTime + clip.offset, clip.offset,
                    clip.duration - clip.offset - clip.trimEnd);
            }
        }

        try {
            if (typeof doNoticePopover === 'function') {
                doNoticePopover('Rendering mixdown...', 'notice-pop-blue');
            }
            const rendered = await offlineCtx.startRendering();
            const wavBlob = audioBufferToWav(rendered);

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
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT') return;
        const key = e.key.toLowerCase();

        if (key === ' ') { e.preventDefault(); togglePlayback(); }
        else if (key === 'z' && (e.ctrlKey || e.metaKey) && e.shiftKey) { e.preventDefault(); doRedo(); }
        else if (key === 'z' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doUndo(); }
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
        else if (key === 's' && !e.ctrlKey) {
            const track = getSelectedTrack();
            if (track) {
                track.soloed = !track.soloed;
                updatePlaybackGains();
                renderAllTracks();
            }
        }
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
        stopPlayback();
        // Destroy existing tracks
        if (state) {
            for (const track of state.tracks) {
                AudioDawTrack.destroyTrack(track);
            }
        }
        state = getDefaultState();
        blobStore.clear();
        activeSourceNodes = [];
    }

    function destroyAll() {
        stopPlayback();
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

    return { open, close };
})();
