/**
 * AudioLab Editor — Modal-based audio editor with WaveSurfer waveform visualization.
 * Uses AudioLabPlayer (trim/split/concat/overlay/export) and AudioLabCore (Crunker ops).
 * Designed for extensibility: audio editing now, video/lip-sync later.
 */
const AudioLabEditor = (() => {
    'use strict';

    const MODAL_ID = 'audiolab_editor_modal';

    let player = null;
    let modalEl = null;
    let undoStack = [];
    let statusEl = null;

    /**
     * Open the editor with an audio source.
     * @param {string} audioSrc - URL of audio to edit
     */
    function open(audioSrc) {
        if (!modalEl) {
            buildModal();
        }
        undoStack = [];
        updateUndoButton();
        updateStatus('Loading...');
        $(modalEl).modal('show');
        // SwarmUI modals appear instantly (no fade animation),
        // but allow a frame for layout before creating WaveSurfer
        setTimeout(() => createPlayer(audioSrc), 100);
    }

    /** Create the WaveSurfer player after the modal is visible. */
    async function createPlayer(audioSrc) {
        if (player) {
            player.destroy();
            player = null;
        }
        let waveformContainer = document.getElementById('audiolab_editor_waveform');
        if (!waveformContainer) {
            updateStatus('Editor container not found');
            return;
        }
        waveformContainer.innerHTML = '';
        player = AudioLabPlayer.create(waveformContainer, {
            height: 128,
            enableRegions: true,
            showControls: true,
            showDownload: false,
            showSpeed: true,
            showVolume: true
        });
        if (!player) {
            updateStatus('Failed to create player');
            return;
        }
        player.on('ready', (duration) => {
            updateStatus(`Duration: ${AudioLabPlayer.formatTime(duration)}`);
        });
        player.on('edit', (type) => {
            let dur = player.getDuration();
            updateStatus(`${type} applied \u2014 Duration: ${AudioLabPlayer.formatTime(dur)}`);
        });
        // Convert audio src to a blob, then load via WaveSurfer.
        // The src can be a file path (Output/audio/...) or data URI (data:audio/wav;base64,...).
        // We always convert to a blob first because WaveSurfer's ws.load() can hang
        // if the internal <audio> element's loadedmetadata event never fires.
        try {
            let blob;
            console.log('[AudioLabEditor] src type:', audioSrc.startsWith('data:') ? 'data URI' : 'URL', audioSrc.substring(0, 80));
            if (audioSrc.startsWith('data:')) {
                let mimeType = audioSrc.substring(audioSrc.indexOf(':') + 1, audioSrc.indexOf(';'));
                let base64 = audioSrc.split(',')[1];
                let data = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
                blob = new Blob([data], { type: mimeType });
            } else {
                let response = await fetch(audioSrc);
                console.log('[AudioLabEditor] fetch:', response.status, response.headers.get('content-type'));
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                blob = await response.blob();
            }
            console.log('[AudioLabEditor] blob:', blob.size, 'bytes, type:', blob.type);
            // loadBlob calls ws.loadBlob(blob) which decodes directly — no extra fetch
            let loadPromise = player.loadBlob(blob);
            let timeoutPromise = new Promise((_, reject) =>
                setTimeout(() => reject(new Error('Load timed out — WaveSurfer decode may have failed')), 15000)
            );
            await Promise.race([loadPromise, timeoutPromise]);
        } catch (err) {
            console.error('[AudioLabEditor] Audio load failed:', err, 'src:', audioSrc.substring(0, 120));
            updateStatus('Load failed: ' + err.message);
        }
    }

    /** Close the editor and clean up. */
    function close() {
        if (modalEl) {
            $(modalEl).modal('hide');
        }
        if (player) {
            player.destroy();
            player = null;
        }
        undoStack = [];
    }

    /** Build the Bootstrap modal DOM (once). */
    function buildModal() {
        let existing = document.getElementById(MODAL_ID);
        if (existing) existing.remove();

        let bodyHtml = `
        <div class="modal-body">
            <div class="audiolab-editor-waveform" id="audiolab_editor_waveform"></div>
            <div class="audiolab-editor-toolbar">
                <button class="basic-button" id="ale_select_range" title="Select a time range on the waveform for trimming">
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
                <button class="basic-button" id="ale_undo" title="Undo last edit" disabled>
                    &#x21B6; Undo
                </button>
                <span class="audiolab-editor-status" id="ale_status"></span>
            </div>
        </div>`;

        let footerHtml = `
        <div class="modal-footer">
            <button class="btn btn-primary basic-button" id="ale_export">
                &#x2913; Export WAV
            </button>
            <button class="btn btn-primary basic-button" id="ale_use_as_input">
                &#x21B3; Use as Input
            </button>
            <button class="btn btn-secondary basic-button" id="ale_close">Close</button>
        </div>`;

        let html = modalHeader(MODAL_ID, 'Audio Editor') + bodyHtml + footerHtml + modalFooter();
        let wrapper = document.createElement('div');
        wrapper.innerHTML = html;
        document.body.appendChild(wrapper.firstElementChild);

        modalEl = document.getElementById(MODAL_ID);
        statusEl = document.getElementById('ale_status');

        // Wire toolbar buttons
        document.getElementById('ale_select_range').addEventListener('click', doSelectRange);
        document.getElementById('ale_trim').addEventListener('click', doTrim);
        document.getElementById('ale_split').addEventListener('click', doSplit);
        document.getElementById('ale_append').addEventListener('click', () => pickFile(doAppend));
        document.getElementById('ale_overlay').addEventListener('click', () => pickFile(doOverlay));
        document.getElementById('ale_undo').addEventListener('click', doUndo);
        document.getElementById('ale_export').addEventListener('click', doExport);
        document.getElementById('ale_use_as_input').addEventListener('click', doUseAsInput);
        document.getElementById('ale_close').addEventListener('click', close);
    }

    // ===== Edit Operations =====

    /** Save current audio state to undo stack before an edit. */
    async function pushUndo() {
        if (!player) return;
        let blob = await player.exportBlob();
        if (blob) {
            undoStack.push(blob);
            if (undoStack.length > 20) undoStack.shift();
            updateUndoButton();
        }
    }

    function doSelectRange() {
        if (!player) return;
        let duration = player.getDuration();
        if (duration <= 0) return;
        player.setRegion(duration * 0.25, duration * 0.75);
        document.getElementById('ale_trim').disabled = false;
    }

    async function doTrim() {
        if (!player) return;
        let region = player.getRegion();
        if (!region) {
            updateStatus('Select a range first');
            return;
        }
        await pushUndo();
        await player.trimToRegion();
        document.getElementById('ale_trim').disabled = true;
    }

    async function doSplit() {
        if (!player) return;
        let time = player.getCurrentTime();
        if (time <= 0) {
            updateStatus('Move the cursor to a split point');
            return;
        }
        await pushUndo();
        let parts = await player.splitAtCursor();
        if (!parts) return;
        // Let user choose which part to keep
        let keepFirst = confirm(
            `Split at ${AudioLabPlayer.formatTime(time)}.\n\nOK = keep first part\nCancel = keep second part`
        );
        let keepBlob = keepFirst ? parts.before : parts.after;
        let url = URL.createObjectURL(keepBlob);
        await player.load(url);
        updateStatus(`Kept ${keepFirst ? 'first' : 'second'} part`);
    }

    async function doAppend(file) {
        if (!player) return;
        await pushUndo();
        let url = URL.createObjectURL(file);
        await player.appendAudio(url);
        URL.revokeObjectURL(url);
        updateStatus(`Appended: ${file.name}`);
    }

    async function doOverlay(file) {
        if (!player) return;
        await pushUndo();
        let url = URL.createObjectURL(file);
        await player.overlayAudio(url);
        URL.revokeObjectURL(url);
        updateStatus(`Overlayed: ${file.name}`);
    }

    async function doUndo() {
        if (!player || undoStack.length === 0) return;
        let blob = undoStack.pop();
        let url = URL.createObjectURL(blob);
        await player.load(url);
        updateUndoButton();
        updateStatus('Undone');
    }

    async function doExport() {
        if (!player) return;
        let blob = await player.exportBlob();
        if (!blob) return;
        let a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `audiolab-edit-${Date.now()}.wav`;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
        updateStatus('Exported');
    }

    async function doUseAsInput() {
        if (!player) return;
        let blob = await player.exportBlob();
        if (!blob) return;
        // Set as reference audio param if available
        let refInput = document.getElementById('input_referenceaudio');
        if (refInput) {
            let file = new File([blob], 'edited-audio.wav', { type: 'audio/wav' });
            let dt = new DataTransfer();
            dt.items.add(file);
            refInput.files = dt.files;
            triggerChangeFor(refInput);
            updateStatus('Set as Reference Audio input');
        } else {
            // Fallback: try source audio param
            let srcInput = document.getElementById('input_acesourceaudio') || document.getElementById('input_sourceaudio');
            if (srcInput) {
                let file = new File([blob], 'edited-audio.wav', { type: 'audio/wav' });
                let dt = new DataTransfer();
                dt.items.add(file);
                srcInput.files = dt.files;
                triggerChangeFor(srcInput);
                updateStatus('Set as Source Audio input');
            } else {
                updateStatus('No audio input param found \u2014 use Export instead');
            }
        }
    }

    // ===== Helpers =====

    /** Prompt user to pick an audio file, then call callback with it. */
    function pickFile(callback) {
        let input = document.createElement('input');
        input.type = 'file';
        input.accept = 'audio/*';
        input.onchange = () => {
            if (input.files.length > 0) {
                callback(input.files[0]);
            }
        };
        input.click();
    }

    function updateUndoButton() {
        let btn = document.getElementById('ale_undo');
        if (btn) btn.disabled = undoStack.length === 0;
    }

    function updateStatus(text) {
        if (statusEl) statusEl.textContent = text;
    }

    return { open, close };
})();
