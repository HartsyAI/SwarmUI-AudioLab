/**
 * AudioLab UI Module for SwarmUI
 * Manages dashboard cards, workspace panels, sidebar, and all user interactions
 */
const AudioLabUI = (() => {
    'use strict';

    let isInitialized = false;
    let activeCategory = null;
    let providerData = [];
    let installStatus = {};
    let statusPolling = null;
    let installPolling = null;
    let pendingInstallProviderId = null;

    // Active player instances (destroyed on workspace close)
    let players = {};

    // Pipeline state
    let pipelineStepCounter = 0;

    const config = {
        statusPollInterval: 30000,
        installPollInterval: 2000,
        maxHistoryItems: 20,
        maxConsoleLines: 100
    };

    const CATEGORIES = {
        tts:        { workspace: 'audiolabWorkspaceTTS',        icon: 'fa-volume-up',        title: 'Text to Speech',   apiCategory: 'TTS' },
        stt:        { workspace: 'audiolabWorkspaceSTT',        icon: 'fa-microphone',       title: 'Speech to Text',   apiCategory: 'STT' },
        musicgen:   { workspace: 'audiolabWorkspaceMusic',      icon: 'fa-music',            title: 'Music Generation', apiCategory: 'MusicGen' },
        voiceclone: { workspace: 'audiolabWorkspaceVoiceClone', icon: 'fa-user-circle',      title: 'Voice Cloning',    apiCategory: 'VoiceClone' },
        audiofx:    { workspace: 'audiolabWorkspaceAudioFX',    icon: 'fa-sliders-h',        title: 'Audio Effects',    apiCategory: 'AudioFX' },
        soundfx:    { workspace: 'audiolabWorkspaceSoundFX',    icon: 'fa-drum',             title: 'Sound Effects',    apiCategory: 'SoundFX' },
        pipeline:   { workspace: 'audiolabWorkspacePipeline',   icon: 'fa-project-diagram',  title: 'Audio Pipeline',   apiCategory: null }
    };

    function init() {
        if (isInitialized) return;
        setupEventListeners();
        refreshProviderStatus();
        statusPolling = setInterval(refreshProviderStatus, config.statusPollInterval);
        isInitialized = true;
        addConsoleMessage('info', 'AudioLab initialized');
    }

    // ===== EVENT SETUP =====
    function setupEventListeners() {
        // Sidebar
        document.getElementById('audiolabSidebarToggle')?.addEventListener('click', toggleSidebar);
        document.getElementById('audiolabSidebarCollapse')?.addEventListener('click', toggleSidebar);

        // Cards
        document.querySelectorAll('.audiolab-card').forEach(card => {
            card.addEventListener('click', () => {
                const category = card.dataset.category;
                if (category && !card.classList.contains('disabled')) openWorkspace(category);
            });
        });

        // Workspace back
        document.getElementById('audiolabWorkspaceBack')?.addEventListener('click', closeWorkspace);
        document.getElementById('audiolabRefreshProviders')?.addEventListener('click', refreshProviderStatus);
        document.getElementById('audiolabClearConsole')?.addEventListener('click', clearConsole);

        // TTS
        document.getElementById('audiolabTTSText')?.addEventListener('input', updateTTSCharCount);
        document.getElementById('audiolabTTSGenerate')?.addEventListener('click', handleTTSGenerate);
        document.getElementById('audiolabTTSStop')?.addEventListener('click', handleTTSStop);
        document.getElementById('audiolabTTSClearHistory')?.addEventListener('click', () => clearHistory('audiolabTTSHistory'));
        setupSliderSync('audiolabTTSVolume', 'audiolabTTSVolumeNum');
        setupSliderSync('audiolabTTSSpeed', 'audiolabTTSSpeedNum');

        // STT
        document.getElementById('audiolabSTTRecord')?.addEventListener('click', handleSTTRecord);
        document.getElementById('audiolabSTTFileInput')?.addEventListener('change', handleSTTFileUpload);
        document.getElementById('audiolabSTTClearHistory')?.addEventListener('click', () => clearHistory('audiolabSTTHistory'));
        document.getElementById('audiolabSTTCopy')?.addEventListener('click', handleSTTCopy);
        document.getElementById('audiolabSTTSendToTTS')?.addEventListener('click', handleSTTSendToTTS);

        // Music Gen
        document.getElementById('audiolabMusicGenerate')?.addEventListener('click', handleMusicGenerate);
        document.getElementById('audiolabMusicStop')?.addEventListener('click', () => { if (players.musicOutput) players.musicOutput.stop(); AudioLabCore.emergencyStop(); addConsoleMessage('info', 'Music: Stopped'); updateTransportStatus('idle'); });
        setupSliderSync('audiolabMusicDuration', 'audiolabMusicDurationNum');
        document.getElementById('audiolabMusicReference')?.addEventListener('change', (e) => handleFilePreview(e, 'audiolabMusicRefPreview'));
        document.getElementById('audiolabMusicPrompt')?.addEventListener('input', updateControlStates);

        // Sound FX
        document.getElementById('audiolabSFXGenerate')?.addEventListener('click', handleSFXGenerate);
        document.getElementById('audiolabSFXStop')?.addEventListener('click', () => { if (players.sfxOutput) players.sfxOutput.stop(); AudioLabCore.emergencyStop(); addConsoleMessage('info', 'SFX: Stopped'); updateTransportStatus('idle'); });
        setupSliderSync('audiolabSFXDuration', 'audiolabSFXDurationNum');
        document.getElementById('audiolabSFXPrompt')?.addEventListener('input', updateControlStates);

        // Voice Clone
        document.getElementById('audiolabCloneSourceFile')?.addEventListener('change', (e) => handleFilePreview(e, 'audiolabCloneSourcePreview'));
        document.getElementById('audiolabCloneRefFile')?.addEventListener('change', (e) => handleFilePreview(e, 'audiolabCloneRefPreview'));
        document.getElementById('audiolabCloneGenerate')?.addEventListener('click', handleCloneGenerate);
        document.getElementById('audiolabCloneStop')?.addEventListener('click', () => { AudioLabCore.emergencyStop(); addConsoleMessage('info', 'Clone: Stopped'); updateTransportStatus('idle'); });

        // Audio FX
        document.getElementById('audiolabFXInputFile')?.addEventListener('change', (e) => handleFilePreview(e, 'audiolabFXInputPreview'));
        document.getElementById('audiolabFXProcess')?.addEventListener('click', handleFXProcess);
        document.getElementById('audiolabFXStop')?.addEventListener('click', () => { AudioLabCore.emergencyStop(); addConsoleMessage('info', 'FX: Stopped'); updateTransportStatus('idle'); });

        // Pipeline
        document.getElementById('audiolabPipelineAddStep')?.addEventListener('click', addPipelineStep);
        document.getElementById('audiolabPipelineClear')?.addEventListener('click', clearPipeline);
        document.getElementById('audiolabPipelineRun')?.addEventListener('click', handlePipelineRun);
        document.querySelectorAll('.audiolab-preset-btn').forEach(btn => {
            btn.addEventListener('click', () => loadPipelinePreset(btn.dataset.preset));
        });

        // Video panel toggles
        document.querySelectorAll('.audiolab-video-panel-toggle').forEach(btn => {
            btn.addEventListener('click', () => toggleVideoPanel(btn));
        });

        // Video file inputs
        document.querySelectorAll('.audiolab-video-file').forEach(input => {
            input.addEventListener('change', (e) => {
                const panel = input.closest('.audiolab-video-panel');
                const video = panel?.querySelector('.audiolab-video-preview');
                const combineBtn = panel?.querySelector('.audiolab-combine-btn');
                if (video && e.target.files?.[0]) {
                    video.src = URL.createObjectURL(e.target.files[0]);
                    video.style.display = 'block';
                    if (combineBtn) combineBtn.disabled = false;
                }
            });
        });

        // Video combine buttons
        document.querySelectorAll('.audiolab-combine-btn').forEach(btn => {
            btn.addEventListener('click', () => handleVideoCombine(btn));
        });

        // Install
        document.getElementById('audiolabConfirmInstall')?.addEventListener('click', handleConfirmInstall);

        // Split panels
        setupSplitPanels();
    }

    // ===== SPLIT PANEL RESIZE =====
    let splitDragState = { active: false, bar: null, panel: null };

    function setupSplitPanels() {
        document.querySelectorAll('.audiolab-split-bar').forEach(bar => {
            bar.addEventListener('mousedown', (e) => {
                splitDragState.active = true;
                splitDragState.bar = bar;
                splitDragState.panel = bar.previousElementSibling;
                bar.classList.add('dragging');
                document.body.style.cursor = 'col-resize';
                document.body.style.userSelect = 'none';
                e.preventDefault();
            });
            bar.addEventListener('touchstart', (e) => {
                splitDragState.active = true;
                splitDragState.bar = bar;
                splitDragState.panel = bar.previousElementSibling;
                bar.classList.add('dragging');
                e.preventDefault();
            }, { passive: false });
        });

        function onMove(clientX) {
            if (!splitDragState.active || !splitDragState.panel) return;
            const container = splitDragState.panel.parentElement;
            const rect = container.getBoundingClientRect();
            const newWidth = clientX - rect.left;
            const minW = 280;
            const maxW = rect.width * 0.65;
            splitDragState.panel.style.width = Math.min(Math.max(newWidth, minW), maxW) + 'px';
        }

        document.addEventListener('mousemove', (e) => onMove(e.clientX));
        document.addEventListener('touchmove', (e) => {
            if (splitDragState.active) onMove(e.touches[0].clientX);
        }, { passive: true });

        function onEnd() {
            if (!splitDragState.active) return;
            if (splitDragState.bar) splitDragState.bar.classList.remove('dragging');
            if (splitDragState.panel) {
                localStorage.setItem('audiolab_splitWidth', splitDragState.panel.style.width);
            }
            splitDragState.active = false;
            splitDragState.bar = null;
            splitDragState.panel = null;
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        }

        document.addEventListener('mouseup', onEnd);
        document.addEventListener('touchend', onEnd);

        // Restore saved width
        const saved = localStorage.getItem('audiolab_splitWidth');
        if (saved) {
            document.querySelectorAll('.audiolab-panel-left').forEach(p => {
                p.style.width = saved;
            });
        }
    }

    function setupSliderSync(sliderId, numberId) {
        const slider = document.getElementById(sliderId);
        const number = document.getElementById(numberId);
        if (!slider || !number) return;
        slider.addEventListener('input', () => { number.value = slider.value; });
        number.addEventListener('input', () => { slider.value = number.value; });
    }

    // ===== SIDEBAR =====
    function toggleSidebar() {
        const layout = document.querySelector('.audiolab-layout');
        if (layout) layout.classList.toggle('sidebar-collapsed');
    }

    // ===== VIDEO PANEL =====
    function toggleVideoPanel(btn) {
        btn.classList.toggle('active');
        const panel = btn.nextElementSibling;
        if (panel) panel.style.display = panel.style.display === 'none' ? 'flex' : 'none';
    }

    // ===== FILE PREVIEW HELPER =====
    function handleFilePreview(event, previewContainerId) {
        const file = event.target.files?.[0];
        const container = document.getElementById(previewContainerId);
        if (!file || !container) return;
        container.style.display = 'block';
        const url = URL.createObjectURL(file);
        if (players[previewContainerId]) players[previewContainerId].destroy();
        players[previewContainerId] = AudioLabPlayer.createMini(container);
        players[previewContainerId].load(url);
    }

    // ===== PROVIDER STATUS =====
    async function refreshProviderStatus() {
        try {
            const [providersResp, installResp] = await Promise.all([
                AudioLabAPI.getAllProvidersStatus(),
                AudioLabAPI.getInstallationStatus()
            ]);
            if (providersResp.success) providerData = providersResp.providers || [];
            if (installResp.success) installStatus = installResp.providers || {};
            renderProviderList();
            updateCardStatuses();
            updateQuickConfigDropdowns();
            updateWorkspaceDropdowns();
            updateControlStates();
        } catch (err) {
            addConsoleMessage('error', `Failed to refresh providers: ${err.message}`);
        }
    }

    function renderProviderList() {
        const container = document.getElementById('audiolabProviderList');
        if (!container) return;
        if (providerData.length === 0) {
            container.innerHTML = '<div class="audiolab-provider-loading">No providers registered</div>';
            return;
        }
        container.innerHTML = providerData.map(p => {
            const isInstalled = installStatus[p.id] === true;
            const statusClass = isInstalled ? 'installed' : 'not-installed';
            const installBtn = isInstalled ? '' :
                `<button class="basic-button small-button audiolab-provider-install-btn" data-provider-id="${escapeHtml(p.id)}" title="Install"><i class="fas fa-download"></i></button>`;
            return `<div class="audiolab-provider-item">
                <div class="audiolab-provider-status ${statusClass}"></div>
                <span class="audiolab-provider-name">${escapeHtml(p.name)}</span>
                <span class="audiolab-provider-category">${escapeHtml(p.category)}</span>
                ${installBtn}
            </div>`;
        }).join('');
        container.querySelectorAll('.audiolab-provider-install-btn').forEach(btn => {
            btn.addEventListener('click', (e) => { e.stopPropagation(); showInstallModal(btn.dataset.providerId); });
        });
    }

    function updateCardStatuses() {
        updateCardStatus('tts', 'TTS', 'TTS');
        updateCardStatus('stt', 'STT', 'STT');
        updateCardStatus('musicgen', 'Music', 'MusicGen');
        updateCardStatus('voiceclone', 'VoiceClone', 'VoiceClone');
        updateCardStatus('audiofx', 'AudioFX', 'AudioFX');
        updateCardStatus('soundfx', 'SoundFX', 'SoundFX');
    }

    function updateCardStatus(catKey, idSuffix, apiCategory) {
        const statusEl = document.getElementById(`audiolabStatus${idSuffix}`);
        const providersEl = document.getElementById(`audiolabProviders${idSuffix}`);
        const card = document.getElementById(`audiolabCard${idSuffix}`);
        const categoryProviders = providerData.filter(p => p.category === apiCategory);
        const installedProviders = categoryProviders.filter(p => installStatus[p.id] === true);

        if (providersEl) {
            if (categoryProviders.length === 0) {
                providersEl.textContent = '';
            } else {
                const total = categoryProviders.length;
                const installed = installedProviders.length;
                providersEl.textContent = `${installed}/${total} provider${total !== 1 ? 's' : ''}`;
            }
        }
        if (statusEl) {
            statusEl.className = 'audiolab-card-status';
            if (categoryProviders.length === 0) {
                statusEl.classList.add('audiolab-status-future');
                statusEl.innerHTML = '<i class="fas fa-clock"></i> <span>No Providers</span>';
                if (card) card.classList.add('disabled');
            } else if (installedProviders.length > 0) {
                statusEl.classList.add('audiolab-status-available');
                statusEl.innerHTML = '<i class="fas fa-circle"></i> <span>Available</span>';
                if (card) card.classList.remove('disabled');
            } else {
                statusEl.classList.add('audiolab-status-not-installed');
                statusEl.innerHTML = '<i class="fas fa-circle"></i> <span>Not Installed</span>';
                if (card) card.classList.remove('disabled');
            }
        }
    }

    function updateQuickConfigDropdowns() {
        updateCategoryDropdown('audiolabActiveSTT', 'STT');
        updateCategoryDropdown('audiolabActiveTTS', 'TTS');
    }

    function updateWorkspaceDropdowns() {
        updateCategoryDropdown('audiolabTTSProvider', 'TTS');
        updateCategoryDropdown('audiolabSTTProvider', 'STT');
        updateCategoryDropdown('audiolabMusicProvider', 'MusicGen');
        updateCategoryDropdown('audiolabCloneProvider', 'VoiceClone');
        updateCategoryDropdown('audiolabFXProvider', 'AudioFX');
        updateCategoryDropdown('audiolabSFXProvider', 'SoundFX');
    }

    function updateCategoryDropdown(selectId, apiCategory) {
        const select = document.getElementById(selectId);
        if (!select) return;
        const providers = providerData.filter(p => p.category === apiCategory);
        if (providers.length === 0) {
            select.innerHTML = '<option value="">No providers</option>';
        } else {
            select.innerHTML = providers.map(p => {
                const installed = installStatus[p.id] === true;
                return `<option value="${escapeHtml(p.id)}"${installed ? '' : ' disabled'}>${escapeHtml(p.name)}${installed ? '' : ' (not installed)'}</option>`;
            }).join('');
        }
    }

    function updateControlStates() {
        const sttAvailable = providerData.some(p => p.category === 'STT' && installStatus[p.id] === true);
        const ttsAvailable = providerData.some(p => p.category === 'TTS' && installStatus[p.id] === true);
        const musicAvailable = providerData.some(p => p.category === 'MusicGen' && installStatus[p.id] === true);
        const cloneAvailable = providerData.some(p => p.category === 'VoiceClone' && installStatus[p.id] === true);
        const fxAvailable = providerData.some(p => p.category === 'AudioFX' && installStatus[p.id] === true);
        const sfxAvailable = providerData.some(p => p.category === 'SoundFX' && installStatus[p.id] === true);

        setDisabled('audiolabSTTRecord', !sttAvailable || !AudioLabCore.isRecordingSupported());
        setDisabled('audiolabTTSGenerate', !ttsAvailable || !(document.getElementById('audiolabTTSText')?.value?.trim()));
        setDisabled('audiolabMusicGenerate', !musicAvailable || !(document.getElementById('audiolabMusicPrompt')?.value?.trim()));
        setDisabled('audiolabCloneGenerate', !cloneAvailable);
        setDisabled('audiolabFXProcess', !fxAvailable);
        setDisabled('audiolabSFXGenerate', !sfxAvailable || !(document.getElementById('audiolabSFXPrompt')?.value?.trim()));
    }

    function setDisabled(id, disabled) {
        const el = document.getElementById(id);
        if (el) el.disabled = disabled;
    }

    // ===== WORKSPACE =====
    function openWorkspace(category) {
        const catConfig = CATEGORIES[category];
        if (!catConfig) return;
        activeCategory = category;

        document.querySelectorAll('.audiolab-card').forEach(c => c.classList.remove('active'));
        document.querySelector(`.audiolab-card[data-category="${category}"]`)?.classList.add('active');

        document.getElementById('audiolabCardGrid').style.display = 'none';
        document.getElementById('audiolabWorkspace').style.display = 'block';

        const icon = document.getElementById('audiolabWorkspaceIcon');
        const name = document.getElementById('audiolabWorkspaceName');
        if (icon) icon.className = `fas ${catConfig.icon}`;
        if (name) name.textContent = catConfig.title;

        document.querySelectorAll('.audiolab-workspace-panel').forEach(p => p.style.display = 'none');
        document.getElementById(catConfig.workspace).style.display = 'flex';

        addConsoleMessage('info', `Opened ${catConfig.title} workspace`);
    }

    function closeWorkspace() {
        activeCategory = null;
        document.querySelectorAll('.audiolab-card').forEach(c => c.classList.remove('active'));
        document.getElementById('audiolabCardGrid').style.display = 'grid';
        document.getElementById('audiolabWorkspace').style.display = 'none';
        AudioLabCore.emergencyStop();
    }

    // ===== TTS HANDLERS =====
    function updateTTSCharCount() {
        const text = document.getElementById('audiolabTTSText')?.value || '';
        const counter = document.getElementById('audiolabTTSCharCount');
        if (counter) {
            counter.textContent = text.length;
            counter.style.color = text.length > 800 ? 'var(--red)' : text.length > 600 ? 'var(--emphasis)' : 'var(--text-soft)';
        }
        updateControlStates();
    }

    async function handleTTSGenerate() {
        const text = document.getElementById('audiolabTTSText')?.value?.trim();
        if (!text) { showError('Enter text to speak'); return; }

        const generateBtn = document.getElementById('audiolabTTSGenerate');
        const stopBtn = document.getElementById('audiolabTTSStop');
        try {
            AudioLabAPI.validateText(text);
            if (generateBtn) { generateBtn.disabled = true; generateBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Generating...'; }
            if (stopBtn) stopBtn.disabled = false;
            updateTransportStatus('processing');
            addConsoleMessage('info', `TTS: Generating speech for "${text.substring(0, 40)}..."`);

            const options = {
                providerId: document.getElementById('audiolabTTSProvider')?.value || '',
                voice: document.getElementById('audiolabTTSVoice')?.value || 'default',
                language: document.getElementById('audiolabTTSLanguage')?.value || 'en-US',
                volume: parseFloat(document.getElementById('audiolabTTSVolume')?.value || '0.8'),
                speed: parseFloat(document.getElementById('audiolabTTSSpeed')?.value || '1.0')
            };
            const result = await AudioLabAPI.processTTS(text, options);

            if (result.success && result.audio_data) {
                const container = document.getElementById('audiolabTTSPlayerContainer');
                const placeholder = document.getElementById('audiolabTTSPlaceholder');
                if (players.ttsOutput) players.ttsOutput.destroy();
                container.style.display = 'block';
                players.ttsOutput = AudioLabPlayer.create(container);
                await players.ttsOutput.loadBase64(result.audio_data);
                if (placeholder) placeholder.style.display = 'none';
                addTTSHistoryItem(text, result.audio_data, result.duration);
                addConsoleMessage('success', 'TTS: Speech generated successfully');
            } else {
                throw new Error(result.error || 'No audio data returned');
            }
        } catch (err) {
            showError(`TTS failed: ${err.message}`);
            addConsoleMessage('error', `TTS: ${err.message}`);
        } finally {
            if (generateBtn) { generateBtn.disabled = false; generateBtn.innerHTML = '<i class="fas fa-volume-up"></i> Generate Speech'; }
            if (stopBtn) stopBtn.disabled = true;
            updateTransportStatus('idle');
            updateControlStates();
        }
    }

    async function handleTTSStop() {
        if (players.ttsOutput) players.ttsOutput.stop();
        document.getElementById('audiolabTTSStop').disabled = true;
        addConsoleMessage('info', 'TTS: Playback stopped');
    }

    function addTTSHistoryItem(text, audioData, duration) {
        const container = document.getElementById('audiolabTTSHistory');
        if (!container) return;
        container.querySelector('.audiolab-history-empty')?.remove();
        const timestamp = new Date().toLocaleTimeString();
        const itemId = `tts-hist-${Date.now()}`;
        const item = document.createElement('div');
        item.className = 'audiolab-history-item';
        item.innerHTML = `
            <div class="history-timestamp">${timestamp}${duration ? ` (${duration.toFixed(1)}s)` : ''}</div>
            <div class="history-text">${escapeHtml(text.substring(0, 100))}${text.length > 100 ? '...' : ''}</div>
            <div class="history-player" id="${itemId}"></div>`;
        container.insertBefore(item, container.firstChild);
        const miniPlayer = AudioLabPlayer.createMini(document.getElementById(itemId));
        if (miniPlayer) miniPlayer.loadBase64(audioData);
        while (container.querySelectorAll('.audiolab-history-item').length > config.maxHistoryItems) {
            container.removeChild(container.lastElementChild);
        }
    }

    // ===== STT HANDLERS =====
    async function handleSTTRecord() {
        try {
            if (AudioLabCore.isRecording()) { await stopSTTRecording(); }
            else { await startSTTRecording(); }
        } catch (err) {
            showError(`STT: ${err.message}`);
            addConsoleMessage('error', `STT: ${err.message}`);
            resetSTTControls();
        }
    }

    async function startSTTRecording() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');
        const waveformEl = document.getElementById('audiolabSTTRecordingWaveform');
        if (btn) { btn.classList.add('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Stop'; }
        if (status) { status.textContent = 'Listening...'; status.className = 'audiolab-record-status listening'; }
        if (waveformEl) waveformEl.style.display = 'block';
        updateTransportStatus('recording');
        addConsoleMessage('info', 'STT: Recording started');
        await AudioLabCore.startRecording();
    }

    async function stopSTTRecording() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');
        if (btn) { btn.classList.remove('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Processing...'; }
        if (status) { status.textContent = 'Processing...'; status.className = 'audiolab-record-status processing'; }
        updateTransportStatus('processing');
        addConsoleMessage('info', 'STT: Processing audio...');
        const audioData = await AudioLabCore.stopRecording();
        document.getElementById('audiolabSTTRecordingWaveform').style.display = 'none';
        const language = document.getElementById('audiolabSTTLanguage')?.value || 'en-US';
        const providerId = document.getElementById('audiolabSTTProvider')?.value || '';
        const result = await AudioLabAPI.processSTT(audioData, { language, providerId });
        if (result.success && result.transcription) {
            showSTTResult(result.transcription, result.confidence);
            addSTTHistoryItem(result.transcription, result.confidence);
            addConsoleMessage('success', `STT: "${result.transcription}"`);
        } else {
            throw new Error(result.error || 'No transcription returned');
        }
        resetSTTControls();
    }

    async function handleSTTFileUpload(e) {
        const file = e.target.files?.[0];
        if (!file) return;
        const status = document.getElementById('audiolabSTTStatus');
        if (status) { status.textContent = 'Processing file...'; status.className = 'audiolab-record-status processing'; }
        const previewEl = document.getElementById('audiolabSTTFilePreview');
        if (previewEl) {
            previewEl.style.display = 'block';
            if (players.sttFilePreview) players.sttFilePreview.destroy();
            players.sttFilePreview = AudioLabPlayer.createMini(previewEl);
            players.sttFilePreview.load(URL.createObjectURL(file));
        }
        try {
            addConsoleMessage('info', `STT: Processing file "${file.name}"...`);
            const audioData = await AudioLabCore.fileToBase64(file);
            const language = document.getElementById('audiolabSTTLanguage')?.value || 'en-US';
            const providerId = document.getElementById('audiolabSTTProvider')?.value || '';
            const result = await AudioLabAPI.processSTT(audioData, { language, providerId });
            if (result.success && result.transcription) {
                showSTTResult(result.transcription, result.confidence);
                addSTTHistoryItem(result.transcription, result.confidence);
                addConsoleMessage('success', `STT: "${result.transcription}"`);
            } else {
                throw new Error(result.error || 'No transcription returned');
            }
        } catch (err) {
            showError(`STT file processing failed: ${err.message}`);
            addConsoleMessage('error', `STT: ${err.message}`);
        } finally {
            resetSTTControls();
            e.target.value = '';
        }
    }

    function showSTTResult(transcription, confidence) {
        const transcEl = document.getElementById('audiolabSTTTranscription');
        const confEl = document.getElementById('audiolabSTTConfidence');
        if (transcEl) { transcEl.textContent = transcription; transcEl.classList.remove('empty'); }
        if (confEl && confidence !== undefined) confEl.textContent = `${Math.round(confidence * 100)}%`;
        setDisabled('audiolabSTTCopy', false);
        setDisabled('audiolabSTTSendToTTS', false);
    }

    function handleSTTCopy() {
        const text = document.getElementById('audiolabSTTTranscription')?.textContent;
        if (text) { navigator.clipboard.writeText(text); addConsoleMessage('info', 'STT: Copied to clipboard'); }
    }

    function handleSTTSendToTTS() {
        const text = document.getElementById('audiolabSTTTranscription')?.textContent;
        if (text) {
            openWorkspace('tts');
            const ttsText = document.getElementById('audiolabTTSText');
            if (ttsText) { ttsText.value = text; updateTTSCharCount(); }
            addConsoleMessage('info', 'STT: Sent transcription to TTS');
        }
    }

    function resetSTTControls() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');
        if (btn) { btn.classList.remove('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Record'; }
        if (status) { status.textContent = 'Ready'; status.className = 'audiolab-record-status ready'; }
        updateTransportStatus('idle');
    }

    function addSTTHistoryItem(transcription, confidence) {
        const container = document.getElementById('audiolabSTTHistory');
        if (!container) return;
        container.querySelector('.audiolab-history-empty')?.remove();
        const timestamp = new Date().toLocaleTimeString();
        const item = document.createElement('div');
        item.className = 'audiolab-history-item';
        item.innerHTML = `
            <div class="history-timestamp">${timestamp} | Confidence: ${confidence !== undefined ? Math.round(confidence * 100) + '%' : '--'}</div>
            <div class="history-text">${escapeHtml(transcription)}</div>`;
        container.insertBefore(item, container.firstChild);
        while (container.querySelectorAll('.audiolab-history-item').length > config.maxHistoryItems) {
            container.removeChild(container.lastElementChild);
        }
    }

    // ===== MUSIC GEN HANDLER =====
    async function handleMusicGenerate() {
        const prompt = document.getElementById('audiolabMusicPrompt')?.value?.trim();
        if (!prompt) { showError('Enter a music prompt'); return; }
        const btn = document.getElementById('audiolabMusicGenerate');
        try {
            if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Generating...'; }
            updateTransportStatus('processing');
            addConsoleMessage('info', `Music: Generating for "${prompt.substring(0, 40)}..."`);
            const duration = parseFloat(document.getElementById('audiolabMusicDuration')?.value || '30');
            const args = { prompt, duration };
            // Include reference audio if provided
            const refFile = document.getElementById('audiolabMusicReference')?.files?.[0];
            if (refFile) { args.reference_audio = await AudioLabCore.fileToBase64(refFile); }
            // Include lyrics if provided
            const lyrics = document.getElementById('audiolabMusicLyrics')?.value?.trim();
            if (lyrics) { args.lyrics = lyrics; }
            const result = await AudioLabAPI.processAudio(document.getElementById('audiolabMusicProvider')?.value || '', args);
            if (result.success && result.audio_data) {
                const container = document.getElementById('audiolabMusicPlayerContainer');
                if (players.musicOutput) players.musicOutput.destroy();
                container.style.display = 'block';
                players.musicOutput = AudioLabPlayer.create(container);
                await players.musicOutput.loadBase64(result.audio_data);
                document.getElementById('audiolabMusicPlaceholder').style.display = 'none';
                addConsoleMessage('success', 'Music: Generation complete');
            } else { throw new Error(result.error || 'No audio data returned'); }
        } catch (err) {
            showError(`Music generation failed: ${err.message}`);
            addConsoleMessage('error', `Music: ${err.message}`);
        } finally {
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="fas fa-music"></i> Generate Music'; }
            updateTransportStatus('idle');
        }
    }

    // ===== SOUND FX HANDLER =====
    async function handleSFXGenerate() {
        const prompt = document.getElementById('audiolabSFXPrompt')?.value?.trim();
        if (!prompt) { showError('Enter a sound effect prompt'); return; }
        const btn = document.getElementById('audiolabSFXGenerate');
        try {
            if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Generating...'; }
            updateTransportStatus('processing');
            addConsoleMessage('info', `SFX: Generating for "${prompt.substring(0, 40)}..."`);
            const duration = parseFloat(document.getElementById('audiolabSFXDuration')?.value || '10');
            const result = await AudioLabAPI.processAudio(document.getElementById('audiolabSFXProvider')?.value || '', { prompt, duration });
            if (result.success && result.audio_data) {
                const container = document.getElementById('audiolabSFXPlayerContainer');
                if (players.sfxOutput) players.sfxOutput.destroy();
                container.style.display = 'block';
                players.sfxOutput = AudioLabPlayer.create(container);
                await players.sfxOutput.loadBase64(result.audio_data);
                document.getElementById('audiolabSFXPlaceholder').style.display = 'none';
                addConsoleMessage('success', 'SFX: Generation complete');
            } else { throw new Error(result.error || 'No audio data returned'); }
        } catch (err) {
            showError(`SFX generation failed: ${err.message}`);
            addConsoleMessage('error', `SFX: ${err.message}`);
        } finally {
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="fas fa-drum"></i> Generate SFX'; }
            updateTransportStatus('idle');
        }
    }

    // ===== VOICE CLONE HANDLER =====
    async function handleCloneGenerate() {
        addConsoleMessage('info', 'Voice Clone: Processing...');
        const btn = document.getElementById('audiolabCloneGenerate');
        try {
            if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Cloning...'; }
            updateTransportStatus('processing');

            // Get source audio from file input
            const sourceFile = document.getElementById('audiolabCloneSourceFile')?.files?.[0];
            if (!sourceFile) { throw new Error('Select a source audio file'); }
            const sourceAudio = await AudioLabCore.fileToBase64(sourceFile);

            // Get reference voice from file input
            const refFile = document.getElementById('audiolabCloneRefFile')?.files?.[0];
            if (!refFile) { throw new Error('Select a reference voice file'); }
            const targetVoice = await AudioLabCore.fileToBase64(refFile);

            const providerId = document.getElementById('audiolabCloneProvider')?.value || '';
            const result = await AudioLabAPI.processAudio(providerId, {
                source_audio: sourceAudio,
                target_voice: targetVoice,
            });

            if (result.success && result.audio_data) {
                // Show A/B comparison
                const outputContainer = document.getElementById('audiolabCloneOutputContainer');
                if (outputContainer) outputContainer.style.display = 'flex';
                const placeholder = document.getElementById('audiolabClonePlaceholder');
                if (placeholder) placeholder.style.display = 'none';

                // Load original audio to orig player
                const origContainer = document.getElementById('audiolabCloneOrigPlayer');
                if (origContainer) {
                    if (players.cloneOrig) players.cloneOrig.destroy();
                    players.cloneOrig = AudioLabPlayer.create(origContainer);
                    await players.cloneOrig.loadBase64(sourceAudio);
                }

                // Load cloned audio to result player
                const resultContainer = document.getElementById('audiolabCloneResultPlayer');
                if (resultContainer) {
                    if (players.cloneResult) players.cloneResult.destroy();
                    players.cloneResult = AudioLabPlayer.create(resultContainer);
                    await players.cloneResult.loadBase64(result.audio_data);
                }

                addConsoleMessage('success', `Voice Clone: Complete (${result.duration?.toFixed(1) || '?'}s)`);
            } else {
                throw new Error(result.error || 'Clone failed');
            }
        } catch (err) {
            showError(`Voice clone failed: ${err.message}`);
            addConsoleMessage('error', `Clone: ${err.message}`);
        } finally {
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="fas fa-user-circle"></i> Clone Voice'; }
            updateTransportStatus('idle');
        }
    }

    // ===== AUDIO FX HANDLER =====
    async function handleFXProcess() {
        addConsoleMessage('info', 'Audio FX: Processing...');
        const btn = document.getElementById('audiolabFXProcess');
        try {
            if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Processing...'; }
            updateTransportStatus('processing');

            // Get input audio from file
            const inputFile = document.getElementById('audiolabFXInputFile')?.files?.[0];
            if (!inputFile) { throw new Error('Select an input audio file'); }
            const inputAudio = await AudioLabCore.fileToBase64(inputFile);

            const providerId = document.getElementById('audiolabFXProvider')?.value || '';
            const result = await AudioLabAPI.processAudio(providerId, { audio_data: inputAudio });

            if (!result.success) { throw new Error(result.error || 'Processing failed'); }

            const placeholder = document.getElementById('audiolabFXPlaceholder');
            if (placeholder) placeholder.style.display = 'none';

            if (result.stems) {
                // Demucs source separation — show stem players
                const stemsContainer = document.getElementById('audiolabFXStemsContainer');
                if (stemsContainer) stemsContainer.style.display = 'block';
                const enhanceContainer = document.getElementById('audiolabFXEnhanceContainer');
                if (enhanceContainer) enhanceContainer.style.display = 'none';

                const stemMap = { vocals: 'audiolabStemVocals', drums: 'audiolabStemDrums', bass: 'audiolabStemBass', other: 'audiolabStemOther' };
                for (const [stemName, elemId] of Object.entries(stemMap)) {
                    const stemData = result.stems[stemName];
                    if (stemData) {
                        const container = document.getElementById(elemId);
                        if (container) {
                            if (players[elemId]) players[elemId].destroy();
                            players[elemId] = AudioLabPlayer.create(container);
                            await players[elemId].loadBase64(stemData);
                        }
                    }
                }
                addConsoleMessage('success', `Audio FX: Separated into ${Object.keys(result.stems).length} stems`);
            } else if (result.audio_data) {
                // Enhancement — show before/after
                const stemsContainer = document.getElementById('audiolabFXStemsContainer');
                if (stemsContainer) stemsContainer.style.display = 'none';
                const enhanceContainer = document.getElementById('audiolabFXEnhanceContainer');
                if (enhanceContainer) enhanceContainer.style.display = 'flex';

                const beforeContainer = document.getElementById('audiolabFXBeforePlayer');
                if (beforeContainer) {
                    if (players.fxBefore) players.fxBefore.destroy();
                    players.fxBefore = AudioLabPlayer.create(beforeContainer);
                    await players.fxBefore.loadBase64(inputAudio);
                }

                const afterContainer = document.getElementById('audiolabFXAfterPlayer');
                if (afterContainer) {
                    if (players.fxAfter) players.fxAfter.destroy();
                    players.fxAfter = AudioLabPlayer.create(afterContainer);
                    await players.fxAfter.loadBase64(result.audio_data);
                }
                addConsoleMessage('success', 'Audio FX: Enhancement complete');
            }
        } catch (err) {
            showError(`Audio FX failed: ${err.message}`);
            addConsoleMessage('error', `FX: ${err.message}`);
        } finally {
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="fas fa-sliders-h"></i> Process'; }
            updateTransportStatus('idle');
        }
    }

    // ===== PIPELINE =====
    function addPipelineStep() {
        pipelineStepCounter++;
        const container = document.getElementById('audiolabPipelineSteps');
        if (!container) return;
        if (container.children.length > 0) {
            const arrow = document.createElement('div');
            arrow.className = 'audiolab-pipeline-arrow';
            arrow.innerHTML = '<i class="fas fa-arrow-down"></i>';
            container.appendChild(arrow);
        }
        const step = document.createElement('div');
        step.className = 'audiolab-pipeline-step';
        step.dataset.step = pipelineStepCounter;
        step.innerHTML = `
            <div class="step-config">
                <select class="auto-dropdown step-type">
                    <option value="tts">TTS</option>
                    <option value="stt">STT</option>
                    <option value="voiceclone">Voice Clone</option>
                    <option value="audiofx">Audio FX</option>
                    <option value="musicgen">Music Gen</option>
                    <option value="soundfx">Sound FX</option>
                </select>
                <select class="auto-dropdown step-provider"></select>
            </div>
            <span class="step-remove" title="Remove step"><i class="fas fa-times"></i></span>`;
        container.appendChild(step);
        step.querySelector('.step-remove').addEventListener('click', () => {
            const prev = step.previousElementSibling;
            if (prev?.classList.contains('audiolab-pipeline-arrow')) prev.remove();
            step.remove();
            updatePipelineRunButton();
        });
        step.querySelector('.step-type').addEventListener('change', () => updateStepProviders(step));
        updateStepProviders(step);
        updatePipelineRunButton();
    }

    function updateStepProviders(stepEl) {
        const type = stepEl.querySelector('.step-type')?.value;
        const providerSelect = stepEl.querySelector('.step-provider');
        if (!providerSelect || !type) return;
        const catMap = { tts: 'TTS', stt: 'STT', musicgen: 'MusicGen', voiceclone: 'VoiceClone', audiofx: 'AudioFX', soundfx: 'SoundFX' };
        const providers = providerData.filter(p => p.category === catMap[type]);
        providerSelect.innerHTML = providers.length === 0
            ? '<option value="">No providers</option>'
            : providers.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('');
    }

    function updatePipelineRunButton() {
        const btn = document.getElementById('audiolabPipelineRun');
        if (btn) btn.disabled = document.querySelectorAll('.audiolab-pipeline-step').length === 0;
    }

    function clearPipeline() {
        document.getElementById('audiolabPipelineSteps').innerHTML = '';
        pipelineStepCounter = 0;
        updatePipelineRunButton();
    }

    function loadPipelinePreset(preset) {
        clearPipeline();
        const presets = {
            'stt-tts': ['stt', 'tts'],
            'tts-clone': ['tts', 'voiceclone'],
            'tts-enhance': ['tts', 'audiofx'],
            'record-clone-enhance': ['stt', 'voiceclone', 'audiofx']
        };
        const steps = presets[preset];
        if (!steps) return;
        steps.forEach(type => {
            addPipelineStep();
            const allSteps = document.querySelectorAll('.audiolab-pipeline-step');
            const lastStep = allSteps[allSteps.length - 1];
            const typeSelect = lastStep?.querySelector('.step-type');
            if (typeSelect) { typeSelect.value = type; updateStepProviders(lastStep); }
        });
        addConsoleMessage('info', `Pipeline: Loaded preset "${preset}"`);
    }

    async function handlePipelineRun() {
        const steps = document.querySelectorAll('.audiolab-pipeline-step');
        if (steps.length === 0) return;
        addConsoleMessage('info', `Pipeline: Running ${steps.length} step(s)...`);
        updateTransportStatus('processing');

        const progressEl = document.getElementById('audiolabPipelineProgress');
        const progressSteps = document.getElementById('audiolabPipelineProgressSteps');
        if (progressEl) progressEl.style.display = 'block';
        if (progressSteps) {
            progressSteps.innerHTML = Array.from(steps).map((s, i) => {
                const type = s.querySelector('.step-type')?.value || '?';
                return `<div class="audiolab-pipeline-progress-step pending">
                    <span class="step-status"><i class="fas fa-circle"></i></span>
                    <span>Step ${i + 1}: ${type.toUpperCase()}</span>
                </div>`;
            }).join('');
        }

        let pipelineData = null;
        try {
            for (let i = 0; i < steps.length; i++) {
                const step = steps[i];
                const type = step.querySelector('.step-type')?.value;
                const provider = step.querySelector('.step-provider')?.value;
                const progressStep = progressSteps?.children[i];
                if (progressStep) progressStep.className = 'audiolab-pipeline-progress-step running';
                addConsoleMessage('info', `Pipeline: Step ${i + 1} (${type}) running...`);

                try {
                    switch (type) {
                        case 'tts': {
                            const text = pipelineData?.text || document.getElementById('audiolabTTSText')?.value || 'Hello world';
                            const voice = document.getElementById('audiolabTTSVoice')?.value || 'default';
                            const volume = parseFloat(document.getElementById('audiolabTTSVolume')?.value || '0.8');
                            pipelineData = await AudioLabAPI.processTTS(text, { voice, volume });
                            break;
                        }
                        case 'stt': {
                            if (!pipelineData?.audio_data) { throw new Error('STT step requires audio input from a previous step'); }
                            pipelineData = await AudioLabAPI.processSTT(pipelineData.audio_data, { language: 'en-US' });
                            break;
                        }
                        case 'voiceclone': {
                            if (!pipelineData?.audio_data) { throw new Error('Voice Clone step requires audio input'); }
                            const refFile = document.getElementById('audiolabCloneRefFile')?.files?.[0];
                            let targetVoice = '';
                            if (refFile) { targetVoice = await AudioLabCore.fileToBase64(refFile); }
                            pipelineData = await AudioLabAPI.processAudio(provider, {
                                source_audio: pipelineData.audio_data,
                                target_voice: targetVoice,
                            });
                            break;
                        }
                        case 'audiofx': {
                            if (!pipelineData?.audio_data) { throw new Error('Audio FX step requires audio input'); }
                            pipelineData = await AudioLabAPI.processAudio(provider, { audio_data: pipelineData.audio_data });
                            break;
                        }
                        case 'musicgen': {
                            const prompt = document.getElementById('audiolabMusicPrompt')?.value || 'upbeat electronic music';
                            const duration = parseFloat(document.getElementById('audiolabMusicDuration')?.value || '30');
                            pipelineData = await AudioLabAPI.processAudio(provider, { prompt, duration });
                            break;
                        }
                        case 'soundfx': {
                            const prompt = document.getElementById('audiolabSFXPrompt')?.value || 'ambient sounds';
                            const duration = parseFloat(document.getElementById('audiolabSFXDuration')?.value || '10');
                            pipelineData = await AudioLabAPI.processAudio(provider, { prompt, duration });
                            break;
                        }
                        default:
                            throw new Error(`Unknown step type: ${type}`);
                    }

                    if (!pipelineData?.success && pipelineData?.success !== undefined) {
                        throw new Error(pipelineData?.error || `Step ${i + 1} failed`);
                    }

                    if (progressStep) progressStep.className = 'audiolab-pipeline-progress-step completed';
                } catch (stepErr) {
                    if (progressStep) progressStep.className = 'audiolab-pipeline-progress-step error';
                    throw stepErr;
                }
            }

            // Load final output to player if we have audio
            if (pipelineData?.audio_data) {
                const container = document.getElementById('audiolabPipelinePlayerContainer');
                if (container) {
                    if (players.pipelineOutput) players.pipelineOutput.destroy();
                    container.style.display = 'block';
                    players.pipelineOutput = AudioLabPlayer.create(container);
                    await players.pipelineOutput.loadBase64(pipelineData.audio_data);
                    const placeholder = document.getElementById('audiolabPipelinePlaceholder');
                    if (placeholder) placeholder.style.display = 'none';
                }
            }

            addConsoleMessage('success', `Pipeline: Complete (${steps.length} steps)`);
        } catch (err) {
            showError(`Pipeline failed: ${err.message}`);
            addConsoleMessage('error', `Pipeline: ${err.message}`);
        } finally {
            updateTransportStatus('idle');
        }
    }

    // ===== VIDEO + AUDIO COMBINE =====
    async function handleVideoCombine(btn) {
        const panel = btn.closest('.audiolab-video-panel');
        if (!panel) return;
        const videoFile = panel.querySelector('.audiolab-video-file')?.files?.[0];
        if (!videoFile) { showError('Select a video file first'); return; }

        // Find the current workspace audio output
        let audioUrl = null;
        if (players.ttsOutput) audioUrl = players.ttsOutput.getUrl();
        else if (players.musicOutput) audioUrl = players.musicOutput.getUrl();
        else if (players.sfxOutput) audioUrl = players.sfxOutput.getUrl();
        else if (players.pipelineOutput) audioUrl = players.pipelineOutput.getUrl();

        if (!audioUrl) { showError('Generate audio output first before combining'); return; }

        // Determine mode from radio buttons
        const modeRadio = panel.querySelector('input[type="radio"]:checked');
        const mode = modeRadio?.value || 'replace';

        try {
            btn.disabled = true;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Combining...';
            addConsoleMessage('info', `Video: Combining (${mode} mode)...`);

            // Convert video to base64
            const videoBase64 = await AudioLabCore.fileToBase64(videoFile);

            // Fetch audio blob and convert to base64
            const audioResp = await fetch(audioUrl);
            const audioBlob = await audioResp.blob();
            const audioBase64 = await AudioLabCore.blobToBase64Export(audioBlob);

            const result = await AudioLabAPI.combineVideoAudio(videoBase64, audioBase64, mode);

            if (result.success && result.video_data) {
                // Trigger download of combined video
                const bytes = Uint8Array.from(atob(result.video_data), c => c.charCodeAt(0));
                const blob = new Blob([bytes], { type: 'video/mp4' });
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url;
                a.download = `audiolab-combined-${Date.now()}.mp4`;
                a.click();
                URL.revokeObjectURL(url);
                addConsoleMessage('success', `Video: Combined successfully (${(result.size_bytes / 1024 / 1024).toFixed(1)}MB)`);
            } else {
                throw new Error(result.error || 'Combine failed');
            }
        } catch (err) {
            showError(`Video combine failed: ${err.message}`);
            addConsoleMessage('error', `Video: ${err.message}`);
        } finally {
            btn.disabled = false;
            btn.innerHTML = '<i class="fas fa-film"></i> Combine & Export';
        }
    }

    // ===== INSTALLATION =====
    function showInstallModal(providerId) {
        pendingInstallProviderId = providerId;
        const provider = providerData.find(p => p.id === providerId);
        document.getElementById('audiolabInstallProviderName').textContent = provider?.name || providerId;
        new bootstrap.Modal(document.getElementById('audiolabInstallModal')).show();
    }

    async function handleConfirmInstall() {
        if (!pendingInstallProviderId) return;
        bootstrap.Modal.getInstance(document.getElementById('audiolabInstallModal'))?.hide();
        const progressModal = new bootstrap.Modal(document.getElementById('audiolabProgressModal'));
        progressModal.show();
        const progressBar = document.getElementById('audiolabProgressBar');
        const progressText = document.getElementById('audiolabProgressText');
        const progressPercent = document.getElementById('audiolabProgressPercent');
        const currentPkg = document.getElementById('audiolabCurrentPackage');
        const output = document.getElementById('audiolabInstallOutput');
        if (progressBar) progressBar.style.width = '0%';
        if (progressText) progressText.textContent = 'Starting installation...';
        if (progressPercent) progressPercent.textContent = '0%';
        if (currentPkg) currentPkg.textContent = '--';
        if (output) output.textContent = '';
        try {
            addConsoleMessage('info', `Installing dependencies for ${pendingInstallProviderId}...`);
            const result = await AudioLabAPI.installProviderDependencies(pendingInstallProviderId);
            if (result.success) { startInstallPolling(); }
            else { throw new Error(result.error || 'Installation failed to start'); }
        } catch (err) {
            progressModal.hide();
            showError(`Installation failed: ${err.message}`);
            addConsoleMessage('error', `Install failed: ${err.message}`);
        }
        pendingInstallProviderId = null;
    }

    function startInstallPolling() {
        if (installPolling) clearInterval(installPolling);
        installPolling = setInterval(async () => {
            try {
                const progress = await AudioLabAPI.getInstallationProgress();
                updateInstallProgress(progress);
                if (progress.is_complete || progress.has_error) {
                    clearInterval(installPolling);
                    installPolling = null;
                    if (progress.is_complete && !progress.has_error) {
                        addConsoleMessage('success', 'Dependencies installed successfully');
                        setTimeout(() => {
                            bootstrap.Modal.getInstance(document.getElementById('audiolabProgressModal'))?.hide();
                            refreshProviderStatus();
                        }, 2000);
                    } else {
                        addConsoleMessage('error', `Installation error: ${progress.error_message || 'Unknown'}`);
                        bootstrap.Modal.getInstance(document.getElementById('audiolabProgressModal'))?.hide();
                        showError(`Installation failed: ${progress.error_message}`);
                    }
                }
            } catch { clearInterval(installPolling); installPolling = null; }
        }, config.installPollInterval);
    }

    function updateInstallProgress(progress) {
        const bar = document.getElementById('audiolabProgressBar');
        const text = document.getElementById('audiolabProgressText');
        const pct = document.getElementById('audiolabProgressPercent');
        const pkg = document.getElementById('audiolabCurrentPackage');
        const out = document.getElementById('audiolabInstallOutput');
        if (bar) bar.style.width = `${progress.progress || 0}%`;
        if (pct) pct.textContent = `${progress.progress || 0}%`;
        if (text) text.textContent = progress.current_step || 'Installing...';
        if (pkg) pkg.textContent = progress.current_package || '--';
        if (out && progress.completed_packages) {
            out.textContent = progress.completed_packages.map(p => `Installed: ${p}`).join('\n');
            out.scrollTop = out.scrollHeight;
        }
    }

    // ===== TRANSPORT STATUS =====
    function updateTransportStatus(state) {
        const led = document.getElementById('audiolabTransportLed');
        const readout = document.getElementById('audiolabTransportReadout');
        if (led) {
            led.classList.remove('active', 'recording', 'processing', 'playing');
            if (state !== 'idle') {
                led.classList.add('active', state);
            }
        }
        if (readout) {
            const labels = { idle: 'READY', recording: 'RECORDING', processing: 'PROCESSING', playing: 'PLAYING' };
            readout.textContent = labels[state] || 'READY';
        }
    }

    // ===== CONSOLE =====
    function addConsoleMessage(type, message) {
        const consoleEl = document.getElementById('audiolabConsoleOutput');
        if (!consoleEl) return;
        const timestamp = new Date().toLocaleTimeString();
        const line = document.createElement('div');
        line.className = `console-line console-${type}`;
        line.innerHTML = `<span class="console-timestamp">[${timestamp}]</span><span class="console-message">${escapeHtml(message)}</span>`;
        consoleEl.appendChild(line);
        if (document.getElementById('audiolabAutoScroll')?.checked) consoleEl.scrollTop = consoleEl.scrollHeight;
        while (consoleEl.children.length > config.maxConsoleLines) consoleEl.removeChild(consoleEl.firstChild);
    }

    function clearConsole() {
        const consoleEl = document.getElementById('audiolabConsoleOutput');
        if (consoleEl) consoleEl.innerHTML = '';
        addConsoleMessage('info', 'Console cleared');
    }

    function clearHistory(containerId) {
        const container = document.getElementById(containerId);
        if (container) container.innerHTML = '<div class="audiolab-history-empty"><i class="fas fa-history"></i><p>No items yet</p></div>';
    }

    function cleanup() {
        if (statusPolling) { clearInterval(statusPolling); statusPolling = null; }
        if (installPolling) { clearInterval(installPolling); installPolling = null; }
        AudioLabPlayer.destroyAll();
        AudioLabCore.emergencyStop();
    }

    return { init, cleanup, addConsoleMessage, refreshProviderStatus };
})();

// Initialize when ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', AudioLabUI.init);
} else {
    AudioLabUI.init();
}
window.addEventListener('beforeunload', AudioLabUI.cleanup);
