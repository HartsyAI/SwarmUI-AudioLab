/**
 * AudioLab UI Module for SwarmUI
 * Manages dashboard cards, workspace panels, sidebar, and all user interactions
 */
const AudioLabUI = (() => {
    'use strict';

    // State
    let isInitialized = false;
    let activeCategory = null; // Currently expanded workspace
    let providerData = [];     // Cached provider list from API
    let installStatus = {};    // provider_id -> boolean
    let statusPolling = null;
    let installPolling = null;
    let pendingInstallProviderId = null;

    const config = {
        statusPollInterval: 30000,
        installPollInterval: 2000,
        maxHistoryItems: 20,
        maxConsoleLines: 100
    };

    // Category config: maps data-category to workspace/icon/title
    const CATEGORIES = {
        tts:        { workspace: 'audiolabWorkspaceTTS',        icon: 'fa-volume-up',     title: 'Text to Speech',   apiCategory: 'TTS' },
        stt:        { workspace: 'audiolabWorkspaceSTT',        icon: 'fa-microphone',    title: 'Speech to Text',   apiCategory: 'STT' },
        musicgen:   { workspace: 'audiolabWorkspaceMusic',      icon: 'fa-music',         title: 'Music Generation', apiCategory: 'MusicGen' },
        voiceclone: { workspace: 'audiolabWorkspaceVoiceClone', icon: 'fa-user-circle',   title: 'Voice Cloning',    apiCategory: 'VoiceClone' },
        audiofx:    { workspace: 'audiolabWorkspaceAudioFX',    icon: 'fa-sliders-h',     title: 'Audio Effects',    apiCategory: 'AudioFX' },
        soundfx:    { workspace: 'audiolabWorkspaceSoundFX',    icon: 'fa-drum',          title: 'Sound Effects',    apiCategory: 'SoundFX' }
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
        // Sidebar toggle
        document.getElementById('audiolabSidebarToggle')?.addEventListener('click', toggleSidebar);
        document.getElementById('audiolabSidebarCollapse')?.addEventListener('click', toggleSidebar);

        // Card clicks
        document.querySelectorAll('.audiolab-card').forEach(card => {
            card.addEventListener('click', () => {
                const category = card.dataset.category;
                if (category && !card.classList.contains('disabled')) openWorkspace(category);
            });
        });

        // Workspace back button
        document.getElementById('audiolabWorkspaceBack')?.addEventListener('click', closeWorkspace);

        // Provider refresh
        document.getElementById('audiolabRefreshProviders')?.addEventListener('click', refreshProviderStatus);

        // Console
        document.getElementById('audiolabClearConsole')?.addEventListener('click', clearConsole);

        // TTS events
        document.getElementById('audiolabTTSText')?.addEventListener('input', updateTTSCharCount);
        document.getElementById('audiolabTTSGenerate')?.addEventListener('click', handleTTSGenerate);
        document.getElementById('audiolabTTSStop')?.addEventListener('click', handleTTSStop);
        document.getElementById('audiolabTTSClearHistory')?.addEventListener('click', () => clearHistory('audiolabTTSHistory'));
        setupSliderSync('audiolabTTSVolume', 'audiolabTTSVolumeNum');
        setupSliderSync('audiolabTTSSpeed', 'audiolabTTSSpeedNum');
        setupSliderSync('audiolabTTSPitch', 'audiolabTTSPitchNum');

        // STT events
        document.getElementById('audiolabSTTRecord')?.addEventListener('click', handleSTTRecord);
        document.getElementById('audiolabSTTFileInput')?.addEventListener('change', handleSTTFileUpload);
        document.getElementById('audiolabSTTClearHistory')?.addEventListener('click', () => clearHistory('audiolabSTTHistory'));

        // Install modal
        document.getElementById('audiolabConfirmInstall')?.addEventListener('click', handleConfirmInstall);
    }

    /** Link a range slider and number input for two-way sync */
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

    // ===== PROVIDER STATUS =====
    async function refreshProviderStatus() {
        try {
            const [providersResp, installResp] = await Promise.all([
                AudioLabAPI.getAllProvidersStatus(),
                AudioLabAPI.getInstallationStatus()
            ]);

            if (providersResp.success) {
                providerData = providersResp.providers || [];
            }
            if (installResp.success) {
                installStatus = installResp.providers || {};
            }

            renderProviderList();
            updateCardStatuses();
            updateQuickConfigDropdowns();
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
                `<button class="basic-button small-button audiolab-provider-install-btn" data-provider-id="${escapeHtml(p.id)}" title="Install">
                    <i class="fas fa-download"></i>
                </button>`;
            return `
                <div class="audiolab-provider-item">
                    <div class="audiolab-provider-status ${statusClass}"></div>
                    <span class="audiolab-provider-name">${escapeHtml(p.name)}</span>
                    <span class="audiolab-provider-category">${escapeHtml(p.category)}</span>
                    ${installBtn}
                </div>`;
        }).join('');

        // Attach install button handlers
        container.querySelectorAll('.audiolab-provider-install-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                showInstallModal(btn.dataset.providerId);
            });
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
                providersEl.textContent = 'No providers';
            } else {
                providersEl.textContent = categoryProviders.map(p => p.name).join(', ');
            }
        }

        if (statusEl) {
            statusEl.className = 'audiolab-card-status';
            if (categoryProviders.length === 0) {
                statusEl.classList.add('audiolab-status-future');
                statusEl.innerHTML = '<i class="fas fa-clock"></i> <span>Coming Soon</span>';
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

    function updateCategoryDropdown(selectId, apiCategory) {
        const select = document.getElementById(selectId);
        if (!select) return;
        const providers = providerData.filter(p => p.category === apiCategory && installStatus[p.id] === true);
        select.innerHTML = providers.length === 0
            ? '<option value="">None installed</option>'
            : providers.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('');
    }

    function updateControlStates() {
        const sttAvailable = providerData.some(p => p.category === 'STT' && installStatus[p.id] === true);
        const ttsAvailable = providerData.some(p => p.category === 'TTS' && installStatus[p.id] === true);

        const sttRecord = document.getElementById('audiolabSTTRecord');
        const ttsGenerate = document.getElementById('audiolabTTSGenerate');

        if (sttRecord) sttRecord.disabled = !sttAvailable || !AudioLabCore.isRecordingSupported();
        if (ttsGenerate) {
            const hasText = (document.getElementById('audiolabTTSText')?.value?.trim().length || 0) > 0;
            ttsGenerate.disabled = !ttsAvailable || !hasText;
        }
    }

    // ===== WORKSPACE =====
    function openWorkspace(category) {
        const catConfig = CATEGORIES[category];
        if (!catConfig) return;

        activeCategory = category;

        // Update card active states
        document.querySelectorAll('.audiolab-card').forEach(c => c.classList.remove('active'));
        const card = document.querySelector(`.audiolab-card[data-category="${category}"]`);
        if (card) card.classList.add('active');

        // Hide card grid, show workspace
        const grid = document.getElementById('audiolabCardGrid');
        const workspace = document.getElementById('audiolabWorkspace');
        if (grid) grid.style.display = 'none';
        if (workspace) workspace.style.display = 'block';

        // Update workspace header
        const icon = document.getElementById('audiolabWorkspaceIcon');
        const name = document.getElementById('audiolabWorkspaceName');
        if (icon) icon.className = `fas ${catConfig.icon}`;
        if (name) name.textContent = catConfig.title;

        // Show the correct panel
        document.querySelectorAll('.audiolab-workspace-panel').forEach(p => p.style.display = 'none');
        const panel = document.getElementById(catConfig.workspace);
        if (panel) panel.style.display = 'flex';

        addConsoleMessage('info', `Opened ${catConfig.title} workspace`);
    }

    function closeWorkspace() {
        activeCategory = null;

        document.querySelectorAll('.audiolab-card').forEach(c => c.classList.remove('active'));

        const grid = document.getElementById('audiolabCardGrid');
        const workspace = document.getElementById('audiolabWorkspace');
        if (grid) grid.style.display = 'grid';
        if (workspace) workspace.style.display = 'none';

        // Stop any active operations
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

            addConsoleMessage('info', `TTS: Generating speech for "${text.substring(0, 40)}..."`);

            const options = {
                voice: document.getElementById('audiolabTTSVoice')?.value || 'default',
                volume: parseFloat(document.getElementById('audiolabTTSVolume')?.value || '0.8'),
                speed: parseFloat(document.getElementById('audiolabTTSSpeed')?.value || '1.0'),
                pitch: parseFloat(document.getElementById('audiolabTTSPitch')?.value || '1.0')
            };

            const result = await AudioLabAPI.processTTS(text, options);

            if (result.success && result.audio_data) {
                // Show in audio player
                const player = document.getElementById('audiolabTTSPlayer');
                const placeholder = document.getElementById('audiolabTTSPlaceholder');
                if (player) {
                    const url = AudioLabCore.createAudioURL(result.audio_data);
                    player.src = url;
                    player.style.display = 'block';
                }
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
            updateControlStates();
        }
    }

    async function handleTTSStop() {
        await AudioLabCore.stopAudio();
        const stopBtn = document.getElementById('audiolabTTSStop');
        if (stopBtn) stopBtn.disabled = true;
        addConsoleMessage('info', 'TTS: Playback stopped');
    }

    function addTTSHistoryItem(text, audioData, duration) {
        const container = document.getElementById('audiolabTTSHistory');
        if (!container) return;
        const emptyMsg = container.querySelector('.audiolab-history-empty');
        if (emptyMsg) emptyMsg.remove();

        const url = AudioLabCore.createAudioURL(audioData);
        const timestamp = new Date().toLocaleTimeString();
        const item = document.createElement('div');
        item.className = 'audiolab-history-item';
        item.innerHTML = `
            <div class="history-timestamp">${timestamp}${duration ? ` (${duration.toFixed(1)}s)` : ''}</div>
            <div class="history-text">${escapeHtml(text.substring(0, 100))}${text.length > 100 ? '...' : ''}</div>
            <audio controls src="${url}"></audio>
        `;
        container.insertBefore(item, container.firstChild);

        while (container.querySelectorAll('.audiolab-history-item').length > config.maxHistoryItems) {
            container.removeChild(container.lastElementChild);
        }
    }

    // ===== STT HANDLERS =====
    async function handleSTTRecord() {
        try {
            if (AudioLabCore.isRecording()) {
                await stopSTTRecording();
            } else {
                await startSTTRecording();
            }
        } catch (err) {
            showError(`STT: ${err.message}`);
            addConsoleMessage('error', `STT: ${err.message}`);
            resetSTTControls();
        }
    }

    async function startSTTRecording() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');

        if (btn) { btn.classList.add('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Stop'; }
        if (status) { status.textContent = 'Listening...'; status.className = 'audiolab-record-status listening'; }

        addConsoleMessage('info', 'STT: Recording started');
        await AudioLabCore.startRecording();
    }

    async function stopSTTRecording() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');

        if (btn) { btn.classList.remove('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Processing...'; }
        if (status) { status.textContent = 'Processing...'; status.className = 'audiolab-record-status processing'; }

        addConsoleMessage('info', 'STT: Processing audio...');

        const audioData = await AudioLabCore.stopRecording();
        const language = document.getElementById('audiolabSTTLanguage')?.value || 'en-US';

        const result = await AudioLabAPI.processSTT(audioData, { language });

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

        try {
            addConsoleMessage('info', `STT: Processing file "${file.name}"...`);
            const audioData = await AudioLabCore.fileToBase64(file);
            const language = document.getElementById('audiolabSTTLanguage')?.value || 'en-US';
            const result = await AudioLabAPI.processSTT(audioData, { language });

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
            e.target.value = ''; // Reset file input
        }
    }

    function showSTTResult(transcription, confidence) {
        const transcEl = document.getElementById('audiolabSTTTranscription');
        const confEl = document.getElementById('audiolabSTTConfidence');
        if (transcEl) { transcEl.textContent = transcription; transcEl.classList.remove('empty'); }
        if (confEl && confidence !== undefined) confEl.textContent = `${Math.round(confidence * 100)}%`;
    }

    function resetSTTControls() {
        const btn = document.getElementById('audiolabSTTRecord');
        const status = document.getElementById('audiolabSTTStatus');
        if (btn) { btn.classList.remove('recording'); btn.querySelector('.audiolab-record-label').textContent = 'Record'; }
        if (status) { status.textContent = 'Ready'; status.className = 'audiolab-record-status ready'; }
    }

    function addSTTHistoryItem(transcription, confidence) {
        const container = document.getElementById('audiolabSTTHistory');
        if (!container) return;
        const emptyMsg = container.querySelector('.audiolab-history-empty');
        if (emptyMsg) emptyMsg.remove();

        const timestamp = new Date().toLocaleTimeString();
        const item = document.createElement('div');
        item.className = 'audiolab-history-item';
        item.innerHTML = `
            <div class="history-timestamp">${timestamp} | Confidence: ${confidence !== undefined ? Math.round(confidence * 100) + '%' : '--'}</div>
            <div class="history-text">${escapeHtml(transcription)}</div>
        `;
        container.insertBefore(item, container.firstChild);

        while (container.querySelectorAll('.audiolab-history-item').length > config.maxHistoryItems) {
            container.removeChild(container.lastElementChild);
        }
    }

    // ===== INSTALLATION =====
    function showInstallModal(providerId) {
        pendingInstallProviderId = providerId;
        const provider = providerData.find(p => p.id === providerId);
        const nameEl = document.getElementById('audiolabInstallProviderName');
        if (nameEl) nameEl.textContent = provider?.name || providerId;

        // Show modal via Bootstrap
        const modal = new bootstrap.Modal(document.getElementById('audiolabInstallModal'));
        modal.show();
    }

    async function handleConfirmInstall() {
        if (!pendingInstallProviderId) return;

        // Hide confirm modal
        bootstrap.Modal.getInstance(document.getElementById('audiolabInstallModal'))?.hide();

        // Show progress modal
        const progressModal = new bootstrap.Modal(document.getElementById('audiolabProgressModal'));
        progressModal.show();

        // Reset progress display
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

            if (result.success) {
                startInstallPolling();
            } else {
                throw new Error(result.error || 'Installation failed to start');
            }
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
            } catch (err) {
                clearInterval(installPolling);
                installPolling = null;
            }
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

    // ===== CONSOLE =====
    function addConsoleMessage(type, message) {
        const consoleEl = document.getElementById('audiolabConsoleOutput');
        if (!consoleEl) return;

        const timestamp = new Date().toLocaleTimeString();
        const line = document.createElement('div');
        line.className = `console-line console-${type}`;
        line.innerHTML = `<span class="console-timestamp">[${timestamp}]</span><span class="console-message">${escapeHtml(message)}</span>`;
        consoleEl.appendChild(line);

        if (document.getElementById('audiolabAutoScroll')?.checked) {
            consoleEl.scrollTop = consoleEl.scrollHeight;
        }

        while (consoleEl.children.length > config.maxConsoleLines) {
            consoleEl.removeChild(consoleEl.firstChild);
        }
    }

    function clearConsole() {
        const consoleEl = document.getElementById('audiolabConsoleOutput');
        if (consoleEl) consoleEl.innerHTML = '';
        addConsoleMessage('info', 'Console cleared');
    }

    // ===== HISTORY =====
    function clearHistory(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = `
            <div class="audiolab-history-empty">
                <i class="fas fa-history"></i>
                <p>No items yet</p>
            </div>`;
    }

    // ===== CLEANUP =====
    function cleanup() {
        if (statusPolling) { clearInterval(statusPolling); statusPolling = null; }
        if (installPolling) { clearInterval(installPolling); installPolling = null; }
        AudioLabCore.emergencyStop();
    }

    return {
        init,
        cleanup,
        addConsoleMessage,
        refreshProviderStatus
    };
})();

// Initialize when ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', AudioLabUI.init);
} else {
    AudioLabUI.init();
}

window.addEventListener('beforeunload', AudioLabUI.cleanup);
