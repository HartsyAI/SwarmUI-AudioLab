/**
 * AudioLab — Professional audio editor modal with voice cloning integration.
 * Combines WaveSurfer waveform editing with context-aware model parameter setup.
 * Uses AudioLabPlayer (waveform + edit ops) and AudioLabCore (Crunker DSP).
 */
const AudioLab = (() => {
    'use strict';

    const MODAL_ID = 'audiolab_modal';

    let player = null;
    let modalEl = null;
    let statusEl = null;
    let undoStack = [];
    let originalBlob = null;
    let originalPlayer = null;
    let comparisonVisible = false;
    let dragSelectionUnsub = null;
    let splitParts = null;
    let splitPlayerA = null;
    let splitPlayerB = null;
    let pendingOverlayFile = null;
    let applySectionEl = null;
    let refTextInput = null;

    function open(audioSrc) {
        if (!modalEl) buildModal();
        undoStack = [];
        originalBlob = null;
        splitParts = null;
        pendingOverlayFile = null;
        comparisonVisible = false;
        updateUndoButton();
        hideSplitPanel();
        hideOverlayPanel();
        hideComparison();
        updateStatus('Loading...');
        $(modalEl).modal('show');
        setTimeout(() => initPlayer(audioSrc), 100);
    }

    function close() {
        if (modalEl) $(modalEl).modal('hide');
        destroyAllPlayers();
        undoStack = [];
        originalBlob = null;
        splitParts = null;
        pendingOverlayFile = null;
        if (dragSelectionUnsub) { dragSelectionUnsub(); dragSelectionUnsub = null; }
    }

    function getDuration() {
        return player ? player.getDuration() : 0;
    }

    async function fetchAudioAsBlob(audioSrc) {
        if (audioSrc.startsWith('data:')) {
            const mimeType = audioSrc.substring(audioSrc.indexOf(':') + 1, audioSrc.indexOf(';'));
            const base64 = audioSrc.split(',')[1];
            return AudioLabCore.base64ToBlob(base64, mimeType);
        }
        const response = await fetch(audioSrc);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.blob();
    }

    async function initPlayer(audioSrc) {
        destroyAllPlayers();
        const container = document.getElementById('ale_waveform');
        if (!container) { updateStatus('Editor container not found'); return; }
        container.innerHTML = '';
        player = AudioLabPlayer.create(container, {
            height: 128,
            enableRegions: true,
            editorMode: true,
            showControls: true,
            showDownload: false,
            showSpeed: true,
            showVolume: true
        });
        if (!player) { updateStatus('Failed to create player'); return; }
        player.on('ready', (duration) => {
            updateStatus(`Duration: ${AudioLabPlayer.formatTime(duration)}`);
            refreshApplySection();
        });
        player.on('edit', (type) => {
            const dur = player.getDuration();
            updateStatus(`${type} applied \u2014 Duration: ${AudioLabPlayer.formatTime(dur)}`);
            if (!comparisonVisible && originalBlob) showComparison();
            refreshApplySection();
        });
        enableDragSelection();
        try {
            const blob = await fetchAudioAsBlob(audioSrc);
            originalBlob = blob;
            const loadPromise = player.loadBlob(blob);
            const timeout = new Promise((_, reject) =>
                setTimeout(() => reject(new Error('Load timed out')), 15000)
            );
            await Promise.race([loadPromise, timeout]);
        } catch (err) {
            console.error('[AudioLab] Load failed:', err);
            updateStatus('Load failed: ' + err.message);
        }
    }

    function destroyAllPlayers() {
        if (player) { player.destroy(); player = null; }
        if (originalPlayer) { originalPlayer.destroy(); originalPlayer = null; }
        if (splitPlayerA) { splitPlayerA.destroy(); splitPlayerA = null; }
        if (splitPlayerB) { splitPlayerB.destroy(); splitPlayerB = null; }
        if (dragSelectionUnsub) { dragSelectionUnsub(); dragSelectionUnsub = null; }
    }

    function enableDragSelection() {
        if (!player) return;
        const regionsPlugin = player.getRegionsPlugin();
        if (!regionsPlugin) return;
        dragSelectionUnsub = regionsPlugin.enableDragSelection({
            color: 'rgba(100, 140, 210, 0.3)',
            drag: true,
            resize: true
        });
        regionsPlugin.on('region-created', (region) => {
            regionsPlugin.getRegions().forEach(r => {
                if (r.id !== region.id) r.remove();
            });
            const trimBtn = document.getElementById('ale_trim');
            if (trimBtn) trimBtn.disabled = false;
            updateTrimIndicators(region);
        });
        regionsPlugin.on('region-updated', (region) => {
            updateTrimIndicators(region);
        });
    }

    function updateTrimIndicators(region) {
        const waveformEl = document.getElementById('ale_waveform');
        if (!waveformEl || !player) return;
        const duration = player.getDuration();
        if (duration <= 0) return;
        waveformEl.querySelectorAll('.ale-dim-overlay').forEach(el => el.remove());
        const leftPct = (region.start / duration) * 100;
        const rightPct = ((duration - region.end) / duration) * 100;
        if (leftPct > 0.5) {
            const dimLeft = document.createElement('div');
            dimLeft.className = 'ale-dim-overlay';
            dimLeft.style.left = '0';
            dimLeft.style.width = leftPct + '%';
            waveformEl.appendChild(dimLeft);
        }
        if (rightPct > 0.5) {
            const dimRight = document.createElement('div');
            dimRight.className = 'ale-dim-overlay';
            dimRight.style.right = '0';
            dimRight.style.width = rightPct + '%';
            waveformEl.appendChild(dimRight);
        }
    }

    function clearTrimIndicators() {
        const waveformEl = document.getElementById('ale_waveform');
        if (waveformEl) waveformEl.querySelectorAll('.ale-dim-overlay').forEach(el => el.remove());
    }

    function buildModal() {
        const existing = document.getElementById(MODAL_ID);
        if (existing) existing.remove();

        const bodyHtml = `
        <div class="modal-body">
            <div class="ale-waveform" id="ale_waveform"></div>
            <div class="ale-toolbar" id="ale_toolbar">
                <button class="basic-button" id="ale_select_range" title="Select a time range on the waveform for trimming (or drag directly on waveform)">
                    &#x2194; Select Range
                </button>
                <button class="basic-button" id="ale_trim" title="Trim audio to selected range" disabled>
                    &#x2702; Trim
                </button>
                <button class="basic-button" id="ale_split" title="Split audio at the current playback position">
                    &#x2502;&#x2502; Split
                </button>
                <div class="ale-separator"></div>
                <button class="basic-button" id="ale_append" title="Append another audio file to the end">
                    + Append
                </button>
                <button class="basic-button" id="ale_overlay" title="Mix/overlay another audio file on top">
                    &#x229E; Overlay
                </button>
                <div class="ale-separator"></div>
                <button class="basic-button" id="ale_undo" title="Undo last edit (Ctrl+Z)" disabled>
                    &#x21B6; Undo
                </button>
                <span class="ale-status" id="ale_status"></span>
            </div>
            <div class="ale-split-panel" id="ale_split_panel" style="display:none">
                <div class="ale-split-part">
                    <span class="ale-split-part-label">Part A</span>
                    <div class="ale-split-waveform" id="ale_split_a"></div>
                </div>
                <div class="ale-split-part">
                    <span class="ale-split-part-label">Part B</span>
                    <div class="ale-split-waveform" id="ale_split_b"></div>
                </div>
                <div class="ale-split-actions">
                    <button class="basic-button" id="ale_keep_a">&#x2713; Keep Part A</button>
                    <button class="basic-button" id="ale_keep_b">&#x2713; Keep Part B</button>
                    <button class="basic-button" id="ale_cancel_split">Cancel</button>
                </div>
            </div>
            <div class="ale-overlay-panel" id="ale_overlay_panel" style="display:none">
                <label>Offset: <input type="number" id="ale_overlay_offset" value="0" min="0" step="0.1"> sec</label>
                <label>Volume: <input type="range" id="ale_overlay_gain" min="0" max="2" step="0.05" value="1.0">
                    <span class="ale-gain-value" id="ale_gain_label">100%</span>
                </label>
                <div class="ale-overlay-actions">
                    <button class="basic-button" id="ale_apply_overlay">&#x2713; Apply Overlay</button>
                    <button class="basic-button" id="ale_cancel_overlay">Cancel</button>
                </div>
            </div>
            <div class="ale-comparison collapsed" id="ale_comparison" style="display:none">
                <div class="ale-comparison-header" id="ale_comparison_toggle">
                    <span>&#x25B6; Original</span>
                    <button class="alp-btn" id="ale_hide_comparison" title="Hide">&#x2715;</button>
                </div>
                <div class="ale-comparison-body">
                    <div class="ale-comparison-waveform" id="ale_comparison_waveform"></div>
                </div>
            </div>
            <div class="ale-apply-section" id="ale_apply_section" style="display:none"></div>
        </div>`;

        const footerHtml = `
        <div class="modal-footer">
            <button class="btn btn-primary basic-button" id="ale_export">
                &#x2913; Export WAV
            </button>
            <button class="btn btn-secondary basic-button" id="ale_close">Close</button>
        </div>`;

        const html = modalHeader(MODAL_ID, 'Audio Lab') + bodyHtml + footerHtml + modalFooter();
        const wrapper = document.createElement('div');
        wrapper.innerHTML = html;
        document.body.appendChild(wrapper.firstElementChild);

        modalEl = document.getElementById(MODAL_ID);
        statusEl = document.getElementById('ale_status');
        applySectionEl = document.getElementById('ale_apply_section');

        document.getElementById('ale_select_range').addEventListener('click', doSelectRange);
        document.getElementById('ale_trim').addEventListener('click', doTrim);
        document.getElementById('ale_split').addEventListener('click', doSplit);
        document.getElementById('ale_append').addEventListener('click', () => pickFile(doAppend));
        document.getElementById('ale_overlay').addEventListener('click', () => pickFile(doOverlayStart));
        document.getElementById('ale_undo').addEventListener('click', doUndo);
        document.getElementById('ale_export').addEventListener('click', doExport);
        document.getElementById('ale_close').addEventListener('click', close);

        document.getElementById('ale_keep_a').addEventListener('click', () => doKeepSplitPart('before'));
        document.getElementById('ale_keep_b').addEventListener('click', () => doKeepSplitPart('after'));
        document.getElementById('ale_cancel_split').addEventListener('click', doCancelSplit);

        document.getElementById('ale_overlay_gain').addEventListener('input', (e) => {
            document.getElementById('ale_gain_label').textContent = Math.round(e.target.value * 100) + '%';
        });
        document.getElementById('ale_apply_overlay').addEventListener('click', doApplyOverlay);
        document.getElementById('ale_cancel_overlay').addEventListener('click', () => {
            pendingOverlayFile = null;
            hideOverlayPanel();
        });

        document.getElementById('ale_comparison_toggle').addEventListener('click', toggleComparison);
        document.getElementById('ale_hide_comparison').addEventListener('click', (e) => {
            e.stopPropagation();
            hideComparison();
        });

        modalEl.addEventListener('keydown', handleKeyboard);
    }

    function handleKeyboard(e) {
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT') return;
        const key = e.key.toLowerCase();
        if (key === ' ') { e.preventDefault(); if (player) player.playPause(); }
        else if (key === 'z' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doUndo(); }
        else if (key === 's' && !e.ctrlKey) { doSelectRange(); }
        else if (key === 't') { doTrim(); }
        else if (key === 'x') { doSplit(); }
        else if (key === 'escape') { doCancelSplit(); hideOverlayPanel(); }
    }

    async function pushUndo() {
        if (!player) return;
        const blob = await player.exportBlob();
        if (blob) {
            undoStack.push(blob);
            if (undoStack.length > 20) undoStack.shift();
            updateUndoButton();
        }
    }

    function doSelectRange() {
        if (!player) return;
        const duration = player.getDuration();
        if (duration <= 0) return;
        player.setRegion(duration * 0.25, duration * 0.75);
        const trimBtn = document.getElementById('ale_trim');
        if (trimBtn) trimBtn.disabled = false;
        const region = player.getRegion();
        if (region) updateTrimIndicators({ start: region.start, end: region.end });
    }

    async function doTrim() {
        if (!player) return;
        const region = player.getRegion();
        if (!region) { updateStatus('Select a range first'); return; }
        await pushUndo();
        await player.trimToRegion();
        clearTrimIndicators();
        const trimBtn = document.getElementById('ale_trim');
        if (trimBtn) trimBtn.disabled = true;
    }

    async function doSplit() {
        if (!player) return;
        const time = player.getCurrentTime();
        if (time <= 0) { updateStatus('Move the cursor to a split point'); return; }
        await pushUndo();
        const parts = await player.splitAtCursor();
        if (!parts) return;
        splitParts = parts;
        showSplitPanel(parts, time);
    }

    async function doAppend(file) {
        if (!player) return;
        await pushUndo();
        await player.appendAudio(file);
        updateStatus(`Appended: ${file.name}`);
    }

    function doOverlayStart(file) {
        pendingOverlayFile = file;
        document.getElementById('ale_overlay_offset').value = '0';
        document.getElementById('ale_overlay_gain').value = '1';
        document.getElementById('ale_gain_label').textContent = '100%';
        showOverlayPanel();
    }

    async function doApplyOverlay() {
        if (!player || !pendingOverlayFile) return;
        const offset = parseFloat(document.getElementById('ale_overlay_offset').value) || 0;
        const gain = parseFloat(document.getElementById('ale_overlay_gain').value) || 1.0;
        await pushUndo();
        await player.overlayAudio(pendingOverlayFile, { offset, gain });
        pendingOverlayFile = null;
        hideOverlayPanel();
        updateStatus(`Overlay applied (offset: ${offset}s, vol: ${Math.round(gain * 100)}%)`);
    }

    async function doUndo() {
        if (!player || undoStack.length === 0) return;
        const blob = undoStack.pop();
        await player.loadBlob(blob);
        updateUndoButton();
        clearTrimIndicators();
        updateStatus('Undone');
        refreshApplySection();
    }

    async function doExport() {
        if (!player) return;
        const blob = await player.exportBlob();
        if (!blob) return;
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `audiolab-edit-${Date.now()}.wav`;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
        updateStatus('Exported');
    }

    function showSplitPanel(parts, splitTime) {
        const panel = document.getElementById('ale_split_panel');
        if (!panel) return;
        panel.style.display = '';
        setToolbarEnabled(false);
        const containerA = document.getElementById('ale_split_a');
        const containerB = document.getElementById('ale_split_b');
        containerA.innerHTML = '';
        containerB.innerHTML = '';
        splitPlayerA = AudioLabPlayer.createMini(containerA, { showDownload: false });
        splitPlayerB = AudioLabPlayer.createMini(containerB, { showDownload: false });
        if (splitPlayerA) splitPlayerA.loadBlob(parts.before);
        if (splitPlayerB) splitPlayerB.loadBlob(parts.after);
        const labelA = panel.querySelector('.ale-split-part:first-child .ale-split-part-label');
        const labelB = panel.querySelector('.ale-split-part:last-child .ale-split-part-label');
        if (labelA) labelA.textContent = `Part A (0:00 \u2013 ${AudioLabPlayer.formatTime(splitTime)})`;
        if (labelB) labelB.textContent = `Part B (${AudioLabPlayer.formatTime(splitTime)} \u2013 end)`;
    }

    async function doKeepSplitPart(which) {
        if (!player || !splitParts) return;
        const blob = which === 'before' ? splitParts.before : splitParts.after;
        await player.loadBlob(blob);
        hideSplitPanel();
        updateStatus(`Kept ${which === 'before' ? 'Part A' : 'Part B'}`);
        refreshApplySection();
    }

    async function doCancelSplit() {
        if (!splitParts) return;
        hideSplitPanel();
        await doUndo();
    }

    function hideSplitPanel() {
        const panel = document.getElementById('ale_split_panel');
        if (panel) panel.style.display = 'none';
        if (splitPlayerA) { splitPlayerA.destroy(); splitPlayerA = null; }
        if (splitPlayerB) { splitPlayerB.destroy(); splitPlayerB = null; }
        splitParts = null;
        setToolbarEnabled(true);
    }

    function showOverlayPanel() {
        const panel = document.getElementById('ale_overlay_panel');
        if (panel) panel.style.display = '';
    }

    function hideOverlayPanel() {
        const panel = document.getElementById('ale_overlay_panel');
        if (panel) panel.style.display = 'none';
        pendingOverlayFile = null;
    }

    function showComparison() {
        if (!originalBlob) return;
        const container = document.getElementById('ale_comparison');
        if (!container) return;
        container.style.display = '';
        container.classList.remove('collapsed');
        comparisonVisible = true;
        const waveformEl = document.getElementById('ale_comparison_waveform');
        if (waveformEl && !originalPlayer) {
            waveformEl.innerHTML = '';
            originalPlayer = AudioLabPlayer.createMini(waveformEl, { showDownload: false });
            if (originalPlayer) originalPlayer.loadBlob(originalBlob);
        }
    }

    function hideComparison() {
        const container = document.getElementById('ale_comparison');
        if (container) container.style.display = 'none';
        if (originalPlayer) { originalPlayer.destroy(); originalPlayer = null; }
        comparisonVisible = false;
    }

    function toggleComparison() {
        const container = document.getElementById('ale_comparison');
        if (!container) return;
        const collapsed = container.classList.toggle('collapsed');
        const arrow = container.querySelector('.ale-comparison-header span');
        if (arrow) arrow.innerHTML = collapsed ? '&#x25B6; Original' : '&#x25BC; Original';
    }

    function isParamActive(paramId) {
        const el = document.getElementById(`input_${paramId}`);
        if (!el) return false;
        const wrapper = el.closest('.auto-input');
        if (!wrapper) return true;
        return wrapper.style.display !== 'none';
    }

    function refreshApplySection() {
        if (!applySectionEl) return;
        const params = [
            { id: 'referenceaudio', label: 'Set as Reference Audio', icon: '&#x266B;' },
            { id: 'sourceaudio', label: 'Set as Source Audio', icon: '&#x266A;' },
            { id: 'targetvoice', label: 'Set as Target Voice', icon: '&#x2699;' },
            { id: 'acesourceaudio', label: 'Set as ACE Source Audio', icon: '&#x266A;' },
            { id: 'acereferenceaudio', label: 'Set as ACE Reference Audio', icon: '&#x266B;' }
        ];
        const activeParams = params.filter(p => isParamActive(p.id));
        if (activeParams.length === 0) {
            applySectionEl.style.display = 'none';
            return;
        }
        const duration = player ? player.getDuration() : 0;
        const hasRefText = isParamActive('referencetext');
        let refTextValue = '';
        if (hasRefText) {
            const refTextEl = document.getElementById('input_referencetext');
            if (refTextEl) refTextValue = refTextEl.value || '';
        }
        const durationText = `Duration: ${AudioLabPlayer.formatTime(duration)}`;
        let durationClass = '';
        let recommendation = '';
        if (isParamActive('referenceaudio')) {
            if (duration >= 10 && duration <= 15) { durationClass = 'good'; recommendation = ' \u2014 Good length for voice cloning (10-15s)'; }
            else if (duration >= 5 && duration <= 30) { durationClass = ''; recommendation = ' \u2014 Acceptable (best: 10-15s of clean speech)'; }
            else if (duration > 0) { durationClass = 'warn'; recommendation = ' \u2014 Try trimming to 10-15s of clean speech for best results'; }
        } else if (isParamActive('sourceaudio')) {
            recommendation = ' \u2014 Source audio for voice conversion';
        }

        let html = '<div class="ale-apply-header">Apply to Model</div>';
        if (hasRefText) {
            html += `<div class="ale-apply-ref-text">
                <label>Reference Text:</label>
                <input type="text" id="ale_ref_text" value="${escapeAttr(refTextValue)}" placeholder="Enter text spoken in the reference audio...">
            </div>`;
        }
        html += `<div class="ale-apply-duration ${durationClass}">${durationText}${recommendation}</div>`;
        html += '<div class="ale-apply-buttons">';
        for (let p of activeParams) {
            html += `<button class="basic-button" data-param="${p.id}" title="${p.label}">${p.icon} ${p.label}</button>`;
        }
        html += '</div>';

        applySectionEl.innerHTML = html;
        applySectionEl.style.display = '';
        refTextInput = document.getElementById('ale_ref_text');
        applySectionEl.querySelectorAll('.ale-apply-buttons .basic-button').forEach(btn => {
            btn.addEventListener('click', () => {
                doSetAsParam(btn.dataset.param, btn.title);
            });
        });
    }

    async function doSetAsParam(paramId, label) {
        if (!player) return;
        const blob = await player.exportBlob();
        if (!blob) return;
        const input = document.getElementById(`input_${paramId}`);
        if (!input) { updateStatus('Parameter not found'); return; }
        const file = new File([blob], 'audiolab-audio.wav', { type: 'audio/wav' });
        const dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        triggerChangeFor(input);
        if (refTextInput && refTextInput.value && paramId.includes('reference')) {
            const refTextEl = document.getElementById('input_referencetext');
            if (refTextEl) {
                refTextEl.value = refTextInput.value;
                triggerChangeFor(refTextEl);
            }
        }
        close();
        if (typeof doNoticePopover === 'function') {
            doNoticePopover(`Audio set as ${label}`, 'notice-pop-green');
        }
    }

    function pickFile(callback) {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = 'audio/*';
        input.onchange = () => { if (input.files.length > 0) callback(input.files[0]); };
        input.click();
    }

    function setToolbarEnabled(enabled) {
        const toolbar = document.getElementById('ale_toolbar');
        if (!toolbar) return;
        toolbar.querySelectorAll('.basic-button').forEach(btn => {
            if (enabled) {
                btn.removeAttribute('data-ale-was-disabled');
                btn.disabled = false;
            } else {
                btn.dataset.aleWasDisabled = btn.disabled ? '1' : '';
                btn.disabled = true;
            }
        });
        if (enabled) updateUndoButton();
    }

    function updateUndoButton() {
        const btn = document.getElementById('ale_undo');
        if (btn) btn.disabled = undoStack.length === 0;
    }

    function updateStatus(text) {
        if (statusEl) statusEl.textContent = text;
    }

    function escapeAttr(str) {
        return str.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    return { open, close, isParamActive, getDuration };
})();
