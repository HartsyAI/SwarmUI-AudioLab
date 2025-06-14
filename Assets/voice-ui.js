/**
 * Voice Assistant UI Module for SwarmUI
 * Handles all user interface interactions, DOM manipulation, and event coordination
 */

const VoiceUI = (() => {
    'use strict';

    // Private state
    let isInitialized = false;
    let elements = {};
    let currentMode = 'stt';
    let serviceStatus = {
        running: false,
        healthy: false,
        dependenciesInstalled: false
    };
    let installationPolling = null;
    let statusPolling = null;

    // Configuration
    const config = {
        statusCheckInterval: 30000, // 30 seconds
        installationPollInterval: 2000, // 2 seconds
        maxHistoryItems: 20
    };

    /**
     * Initialize the UI module
     */
    function init() {
        if (isInitialized) return;

        cacheElements();
        setupEventListeners();
        initializeUI();
        startStatusPolling();

        isInitialized = true;
        console.log('[VoiceUI] Initialized');
    }

    /**
     * Cache frequently used DOM elements
     */
    function cacheElements() {
        const elementIds = [
            // Service control
            'refreshServiceStatus', 'primaryServiceButton', 'serviceStatusIcon', 'serviceStatusText',
            'dependenciesCounter', 'coreDependencies', 'aiEngines',

            // Console
            'consoleOutput', 'clearConsole', 'autoScrollConsole',

            // Mode tabs and panels
            'sttModeTab', 'ttsModeTab', 'stsModeTab', 'commandsModeTab',
            'sttMode', 'ttsMode', 'stsMode', 'commandsMode',

            // STT elements
            'sttRecordButton', 'sttRecordingStatus', 'sttLanguage', 'sttConfidence', 'sttTranscription',

            // TTS elements
            'ttsTextInput', 'ttsCharCount', 'ttsVoice', 'ttsVolume', 'ttsVolumeNumber',
            'ttsSpeakButton', 'ttsStopButton',

            // STS elements
            'stsStartButton', 'stsStatus', 'stsUserSpeech', 'stsAiResponse',

            // History and modals
            'sessionHistory', 'clearHistory',
            'installationModal', 'installationProgressModal', 'confirmInstallation',
            'installationProgressText', 'installationProgressPercent', 'installationProgressBar',
            'currentPackage', 'packageProgress', 'installationLiveOutput'
        ];

        elementIds.forEach(id => {
            elements[id] = document.getElementById(id);
            if (!elements[id]) {
                console.warn(`[VoiceUI] Element with ID '${id}' not found`);
            }
        });
    }

    /**
     * Set up all event listeners
     */
    function setupEventListeners() {
        // Service control events
        elements.refreshServiceStatus?.addEventListener('click', refreshServiceStatus);
        elements.primaryServiceButton?.addEventListener('click', handlePrimaryServiceButton);
        elements.confirmInstallation?.addEventListener('click', startInstallation);

        // Mode tab events
        document.querySelectorAll('.mode-tab').forEach(tab => {
            tab.addEventListener('click', (e) => {
                const mode = e.currentTarget.dataset.mode;
                if (mode && !e.currentTarget.disabled) {
                    switchMode(mode);
                }
            });
        });

        // STT events
        elements.sttRecordButton?.addEventListener('click', handleSTTRecordClick);

        // TTS events
        elements.ttsSpeakButton?.addEventListener('click', handleTTSSpeakClick);
        elements.ttsStopButton?.addEventListener('click', handleTTSStopClick);
        elements.ttsTextInput?.addEventListener('input', updateTTSCharCount);

        // TTS volume slider sync
        elements.ttsVolume?.addEventListener('input', (e) => {
            elements.ttsVolumeNumber.value = e.target.value;
            updateSliderBackground(e.target);
        });
        elements.ttsVolumeNumber?.addEventListener('input', (e) => {
            elements.ttsVolume.value = e.target.value;
            updateSliderBackground(elements.ttsVolume);
        });

        // STS events
        elements.stsStartButton?.addEventListener('click', handleSTSClick);

        // Utility events
        elements.clearConsole?.addEventListener('click', clearConsole);
        elements.clearHistory?.addEventListener('click', clearHistory);

        console.log('[VoiceUI] Event listeners set up');
    }

    /**
     * Initialize UI state
     */
    function initializeUI() {
        switchMode('stt');
        updateTTSCharCount();
        updateSliderBackground(elements.ttsVolume);
        clearHistory();
        addConsoleMessage('info', 'Voice Assistant UI initialized');
    }

    // ===== SERVICE MANAGEMENT =====

    /**
     * Start periodic status checking
     */
    function startStatusPolling() {
        refreshServiceStatus(); // Initial check

        if (statusPolling) {
            clearInterval(statusPolling);
        }

        statusPolling = setInterval(refreshServiceStatus, config.statusCheckInterval);
    }

    /**
     * Refresh service status from backend
     */
    async function refreshServiceStatus() {
        try {
            const status = await VoiceAPI.getServiceStatus();
            updateServiceStatus(status);

            // Also check dependencies if service is available
            if (status.service_available) {
                const depStatus = await VoiceAPI.getDependencyStatus();
                updateDependencyStatus(depStatus);
            }
        } catch (error) {
            console.error('[VoiceUI] Failed to refresh service status:', error);
            updateServiceStatus({
                service_running: false,
                service_healthy: false,
                service_available: false,
                error: error.message
            });
        }
    }

    /**
     * Update service status display
     */
    function updateServiceStatus(status) {
        serviceStatus = {
            running: status.service_running || false,
            healthy: status.service_healthy || false,
            available: status.service_available || false
        };

        // Update status icon and text
        const { icon, text, className } = getStatusDisplay(status);

        if (elements.serviceStatusIcon) {
            elements.serviceStatusIcon.innerHTML = icon;
        }
        if (elements.serviceStatusText) {
            elements.serviceStatusText.textContent = text;
            elements.serviceStatusText.className = className;
        }

        // Update primary button
        updatePrimaryServiceButton();

        // Update UI controls based on service state
        updateControlsState();
    }

    /**
     * Get display information for current status
     */
    function getStatusDisplay(status) {
        if (!status.service_available) {
            return {
                icon: '<i class="fas fa-circle" style="color: var(--red);"></i>',
                text: 'Service unavailable',
                className: 'text-danger'
            };
        }

        if (status.service_running && status.service_healthy) {
            return {
                icon: '<i class="fas fa-circle" style="color: var(--green);"></i>',
                text: 'Service online',
                className: 'text-success'
            };
        }

        if (status.service_running) {
            return {
                icon: '<i class="fas fa-circle" style="color: var(--emphasis);"></i>',
                text: 'Service starting...',
                className: 'text-warning'
            };
        }

        return {
            icon: '<i class="fas fa-circle" style="color: var(--text-soft);"></i>',
            text: 'Service offline',
            className: 'text-muted'
        };
    }

    /**
     * Update dependency status display
     */
    function updateDependencyStatus(status) {
        serviceStatus.dependenciesInstalled = status.dependencies_installed || false;

        if (elements.dependenciesCounter) {
            const installed = status.installed_count || 0;
            const total = status.total_count || 0;
            elements.dependenciesCounter.textContent = `${installed}/${total} installed`;
        }

        updateDependencyList('coreDependencies', status.core_dependencies || []);
        updateDependencyList('aiEngines', status.ai_dependencies || []);

        updatePrimaryServiceButton();
    }

    /**
     * Update dependency list display
     */
    function updateDependencyList(elementId, dependencies) {
        const container = elements[elementId];
        if (!container) return;

        if (dependencies.length === 0) {
            container.innerHTML = '<div class="dependency-loading">Checking...</div>';
            return;
        }

        const html = dependencies.map(dep => `
            <div class="dependency-item">
                <span class="dependency-status ${dep.status}">${getDependencyIcon(dep.status)}</span>
                <span class="dependency-name">${escapeHtml(dep.name)}</span>
                ${dep.version ? `<span class="dependency-version">${escapeHtml(dep.version)}</span>` : ''}
            </div>
        `).join('');

        container.innerHTML = html;
    }

    /**
     * Get icon for dependency status
     */
    function getDependencyIcon(status) {
        const icons = {
            'installed': '✅',
            'missing': '❌',
            'installing': '⚙️',
            'error': '⚠️'
        };
        return icons[status] || '❓';
    }

    /**
     * Update primary service button based on current state
     */
    function updatePrimaryServiceButton() {
        const button = elements.primaryServiceButton;
        if (!button) return;

        if (!serviceStatus.dependenciesInstalled) {
            button.innerHTML = '<i class="fas fa-download"></i> Install Dependencies';
            button.disabled = false;
            button.className = 'basic-button btn-primary';
        } else if (!serviceStatus.running) {
            button.innerHTML = '<i class="fas fa-play"></i> Start Service';
            button.disabled = false;
            button.className = 'basic-button btn-primary';
        } else if (serviceStatus.running && serviceStatus.healthy) {
            button.innerHTML = '<i class="fas fa-stop"></i> Stop Service';
            button.disabled = false;
            button.className = 'basic-button';
        } else {
            button.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Starting...';
            button.disabled = true;
            button.className = 'basic-button';
        }
    }

    /**
     * Handle primary service button click
     */
    async function handlePrimaryServiceButton() {
        const buttonText = elements.primaryServiceButton?.textContent || '';

        if (buttonText.includes('Install')) {
            showInstallationModal();
        } else if (buttonText.includes('Start')) {
            await startVoiceService();
        } else if (buttonText.includes('Stop')) {
            await stopVoiceService();
        }
    }

    /**
     * Start voice service
     */
    async function startVoiceService() {
        try {
            addConsoleMessage('info', 'Starting voice service...');
            updatePrimaryServiceButton(); // Show loading state

            const result = await VoiceAPI.startService();

            if (result.success) {
                showSuccess('Voice service started successfully');
                addConsoleMessage('success', 'Voice service started');
            } else {
                throw new Error(result.error || 'Failed to start service');
            }
        } catch (error) {
            showError(`Failed to start voice service: ${error.message}`);
            addConsoleMessage('error', `Service start failed: ${error.message}`);
        } finally {
            await refreshServiceStatus();
        }
    }

    /**
     * Stop voice service
     */
    async function stopVoiceService() {
        try {
            addConsoleMessage('info', 'Stopping voice service...');
            updatePrimaryServiceButton(); // Show loading state

            const result = await VoiceAPI.stopService();

            if (result.success) {
                showSuccess('Voice service stopped successfully');
                addConsoleMessage('success', 'Voice service stopped');
            } else {
                throw new Error(result.error || 'Failed to stop service');
            }
        } catch (error) {
            showError(`Failed to stop voice service: ${error.message}`);
            addConsoleMessage('error', `Service stop failed: ${error.message}`);
        } finally {
            await refreshServiceStatus();
        }
    }

    // ===== INSTALLATION MANAGEMENT =====

    /**
     * Show installation confirmation modal
     */
    function showInstallationModal() {
        // Use SwarmUI's modal system if available, otherwise fallback
        if (elements.installationModal) {
            elements.installationModal.style.display = 'block';
            elements.installationModal.classList.add('show');
        }
    }

    /**
     * Start dependency installation
     */
    async function startInstallation() {
        try {
            hideModal('installationModal');
            showInstallationProgressModal();

            addConsoleMessage('info', 'Starting dependency installation...');

            const result = await VoiceAPI.installDependencies();

            if (result.success) {
                startInstallationPolling();
            } else {
                throw new Error(result.error || 'Failed to start installation');
            }
        } catch (error) {
            hideModal('installationProgressModal');
            showError(`Installation failed: ${error.message}`);
            addConsoleMessage('error', `Installation failed: ${error.message}`);
        }
    }

    /**
     * Show installation progress modal
     */
    function showInstallationProgressModal() {
        if (elements.installationProgressModal) {
            elements.installationProgressModal.style.display = 'block';
            elements.installationProgressModal.classList.add('show');
        }
    }

    /**
     * Start polling installation progress
     */
    function startInstallationPolling() {
        if (installationPolling) {
            clearInterval(installationPolling);
        }

        installationPolling = setInterval(async () => {
            try {
                const progress = await VoiceAPI.getInstallationProgress();
                updateInstallationProgress(progress);

                if (progress.completed || progress.failed) {
                    stopInstallationPolling();

                    if (progress.completed) {
                        showSuccess('Dependencies installed successfully!');
                        setTimeout(() => hideModal('installationProgressModal'), 3000);
                    } else {
                        hideModal('installationProgressModal');
                        showError(`Installation failed: ${progress.error || 'Unknown error'}`);
                    }

                    await refreshServiceStatus();
                }
            } catch (error) {
                console.error('[VoiceUI] Installation polling error:', error);
                stopInstallationPolling();
            }
        }, config.installationPollInterval);
    }

    /**
     * Stop installation polling
     */
    function stopInstallationPolling() {
        if (installationPolling) {
            clearInterval(installationPolling);
            installationPolling = null;
        }
    }

    /**
     * Update installation progress display
     */
    function updateInstallationProgress(progress) {
        const percent = progress.progress || 0;
        const step = progress.current_step || 'Installing...';
        const currentPkg = progress.current_package || '--';
        const pkgProgress = progress.package_progress || '--';
        const output = progress.output || '';

        if (elements.installationProgressBar) {
            elements.installationProgressBar.style.width = `${percent}%`;
        }
        if (elements.installationProgressPercent) {
            elements.installationProgressPercent.textContent = `${percent}%`;
        }
        if (elements.installationProgressText) {
            elements.installationProgressText.textContent = step;
        }
        if (elements.currentPackage) {
            elements.currentPackage.textContent = currentPkg;
        }
        if (elements.packageProgress) {
            elements.packageProgress.textContent = pkgProgress;
        }
        if (elements.installationLiveOutput && output) {
            elements.installationLiveOutput.textContent += output + '\n';
            elements.installationLiveOutput.scrollTop = elements.installationLiveOutput.scrollHeight;
        }
    }

    // ===== MODE MANAGEMENT =====

    /**
     * Switch between voice modes
     */
    function switchMode(mode) {
        // Stop any current activity
        stopAllActivity();

        currentMode = mode;

        // Update tabs
        document.querySelectorAll('.mode-tab').forEach(tab => {
            tab.classList.remove('active');
            if (tab.dataset.mode === mode) {
                tab.classList.add('active');
            }
        });

        // Update panels
        document.querySelectorAll('.mode-panel').forEach(panel => {
            panel.classList.remove('active');
        });

        if (elements[`${mode}Mode`]) {
            elements[`${mode}Mode`].classList.add('active');
        }

        addConsoleMessage('info', `Switched to ${mode.toUpperCase()} mode`);
    }

    /**
     * Stop all current voice activity
     */
    function stopAllActivity() {
        try {
            VoiceCore.emergencyStop();
            updateControlsState();
        } catch (error) {
            console.error('[VoiceUI] Error stopping activity:', error);
        }
    }

    // ===== STT HANDLERS =====

    /**
     * Handle STT record button click
     */
    async function handleSTTRecordClick() {
        if (!isServiceReady()) return;

        try {
            if (VoiceCore.isRecording()) {
                await stopSTTRecording();
            } else {
                await startSTTRecording();
            }
        } catch (error) {
            showError(`STT operation failed: ${error.message}`);
            addConsoleMessage('error', `STT failed: ${error.message}`);
            updateSTTControls(false, false);
        }
    }

    /**
     * Start STT recording
     */
    async function startSTTRecording() {
        addConsoleMessage('info', 'STT: Starting recording...');
        updateSTTControls(true, false);

        await VoiceCore.startRecording();
        addConsoleMessage('info', 'STT: Listening...');
    }

    /**
     * Stop STT recording and process
     */
    async function stopSTTRecording() {
        addConsoleMessage('info', 'STT: Processing audio...');
        updateSTTControls(false, true);

        const audioData = await VoiceCore.stopRecording();

        const options = {
            language: elements.sttLanguage?.value || 'en-US',
            returnConfidence: true
        };

        const result = await VoiceAPI.processSTT(audioData, options);

        if (result.transcription) {
            updateSTTResult(result.transcription, result.confidence);
            addHistoryItem('stt', 'Audio input', result.transcription);
            addConsoleMessage('success', `STT: "${result.transcription}"`);
        } else {
            throw new Error('No transcription received');
        }

        updateSTTControls(false, false);
    }

    /**
     * Update STT controls state
     */
    function updateSTTControls(isRecording, isProcessing) {
        const button = elements.sttRecordButton;
        const status = elements.sttRecordingStatus;

        if (!button || !status) return;

        if (isRecording) {
            button.innerHTML = '<i class="fas fa-stop"></i><span class="button-text">Stop Recording</span>';
            button.classList.add('recording');
            status.textContent = 'Listening...';
            status.className = 'recording-status listening';
        } else if (isProcessing) {
            button.innerHTML = '<i class="fas fa-cog fa-spin"></i><span class="button-text">Processing...</span>';
            button.classList.remove('recording');
            button.classList.add('processing');
            status.textContent = 'Processing audio...';
            status.className = 'recording-status processing';
        } else {
            button.innerHTML = '<i class="fas fa-microphone"></i><span class="button-text">Start Listening</span>';
            button.classList.remove('recording', 'processing');
            status.textContent = 'Ready to listen';
            status.className = 'recording-status ready';
        }
    }

    /**
     * Update STT result display
     */
    function updateSTTResult(transcription, confidence) {
        if (elements.sttTranscription) {
            elements.sttTranscription.textContent = transcription;
            elements.sttTranscription.classList.remove('empty');
        }

        if (elements.sttConfidence && confidence !== undefined) {
            elements.sttConfidence.textContent = `${Math.round((confidence || 0) * 100)}%`;
        }
    }

    // ===== TTS HANDLERS =====

    /**
     * Handle TTS speak button click
     */
    async function handleTTSSpeakClick() {
        if (!isServiceReady()) return;

        const text = elements.ttsTextInput?.value?.trim();
        if (!text) {
            showError('Please enter text to speak');
            return;
        }

        try {
            await speakText(text);
        } catch (error) {
            showError(`TTS operation failed: ${error.message}`);
            addConsoleMessage('error', `TTS failed: ${error.message}`);
            updateTTSControls(false);
        }
    }

    /**
     * Handle TTS stop button click
     */
    async function handleTTSStopClick() {
        try {
            await VoiceCore.stopAudio();
            updateTTSControls(false);
            addConsoleMessage('info', 'TTS: Playback stopped');
        } catch (error) {
            console.error('[VoiceUI] Error stopping TTS:', error);
        }
    }

    /**
     * Speak text using TTS
     */
    async function speakText(text) {
        addConsoleMessage('info', `TTS: Speaking "${text.substring(0, 30)}..."`);
        updateTTSControls(true);

        const options = {
            voice: elements.ttsVoice?.value || 'default',
            volume: parseFloat(elements.ttsVolume?.value || '0.8'),
            language: 'en-US'
        };

        const result = await VoiceCore.processTTSWorkflow(text, options);

        addHistoryItem('tts', text, 'Text spoken successfully');
        showSuccess('Text spoken successfully');
        addConsoleMessage('success', 'TTS: Text spoken successfully');

        updateTTSControls(false);
    }

    /**
     * Update TTS controls state
     */
    function updateTTSControls(isSpeaking) {
        const speakButton = elements.ttsSpeakButton;
        const stopButton = elements.ttsStopButton;
        const hasText = elements.ttsTextInput?.value?.trim().length > 0;

        if (speakButton) {
            speakButton.disabled = !isServiceReady() || !hasText || isSpeaking;
            speakButton.innerHTML = isSpeaking
                ? '<i class="fas fa-cog fa-spin"></i> Speaking...'
                : '<i class="fas fa-volume-up"></i> Speak Text';
        }

        if (stopButton) {
            stopButton.disabled = !isSpeaking;
        }
    }

    /**
     * Update TTS character count
     */
    function updateTTSCharCount() {
        const text = elements.ttsTextInput?.value || '';
        const charCount = elements.ttsCharCount;

        if (charCount) {
            charCount.textContent = text.length;

            // Color coding
            if (text.length > 800) {
                charCount.style.color = 'var(--red)';
            } else if (text.length > 600) {
                charCount.style.color = 'var(--emphasis)';
            } else {
                charCount.style.color = 'var(--text-soft)';
            }
        }

        updateTTSControls(VoiceCore.isPlaying());
    }

    // ===== STS HANDLERS =====

    /**
     * Handle STS button click
     */
    async function handleSTSClick() {
        if (!isServiceReady()) return;

        try {
            if (VoiceCore.isRecording()) {
                await stopSTSConversation();
            } else {
                await startSTSConversation();
            }
        } catch (error) {
            showError(`STS operation failed: ${error.message}`);
            addConsoleMessage('error', `STS failed: ${error.message}`);
            updateSTSControls(false);
        }
    }

    /**
     * Start STS conversation
     */
    async function startSTSConversation() {
        addConsoleMessage('info', 'STS: Starting conversation...');
        updateSTSControls(true);

        await VoiceCore.startRecording();
        addConsoleMessage('info', 'STS: Listening for conversation...');
    }

    /**
     * Stop STS conversation and process
     */
    async function stopSTSConversation() {
        addConsoleMessage('info', 'STS: Processing conversation...');

        const audioData = await VoiceCore.stopRecording();

        const options = {
            sttLanguage: elements.sttLanguage?.value || 'en-US',
            ttsVoice: elements.ttsVoice?.value || 'default',
            ttsVolume: parseFloat(elements.ttsVolume?.value || '0.8'),
            ttsLanguage: 'en-US'
        };

        const result = await VoiceAPI.processSTS(audioData, options);

        const transcription = result.transcription || 'Audio processed';
        const aiResponse = result.ai_response || `Echo: ${transcription}`;

        updateSTSResult(transcription, aiResponse);

        if (result.audio_data) {
            await VoiceCore.playAudio(result.audio_data);
        }

        addHistoryItem('sts', transcription, aiResponse);
        addConsoleMessage('success', 'STS: Conversation completed');

        updateSTSControls(false);
    }

    /**
     * Update STS controls state
     */
    function updateSTSControls(isActive) {
        const button = elements.stsStartButton;
        const status = elements.stsStatus;

        if (button) {
            if (isActive) {
                button.innerHTML = '<i class="fas fa-stop"></i><span class="button-text">Stop Conversation</span>';
                button.classList.add('recording');
            } else {
                button.innerHTML = '<i class="fas fa-comments"></i><span class="button-text">Start Conversation</span>';
                button.classList.remove('recording');
            }
        }

        if (status) {
            status.textContent = isActive ? 'Listening for conversation...' : 'Ready for conversation';
            status.className = isActive ? 'recording-status listening' : 'recording-status ready';
        }
    }

    /**
     * Update STS result display
     */
    function updateSTSResult(userSpeech, aiResponse) {
        if (elements.stsUserSpeech) {
            elements.stsUserSpeech.textContent = userSpeech;
            elements.stsUserSpeech.classList.remove('empty');
        }

        if (elements.stsAiResponse) {
            elements.stsAiResponse.textContent = aiResponse;
            elements.stsAiResponse.classList.remove('empty');
        }
    }

    // ===== UTILITY FUNCTIONS =====

    /**
     * Check if service is ready for voice operations
     */
    function isServiceReady() {
        if (!serviceStatus.running || !serviceStatus.healthy) {
            showError('Voice service is not available. Please start the service first.');
            return false;
        }
        return true;
    }

    /**
     * Update all controls state based on service and capabilities
     */
    function updateControlsState() {
        const serviceReady = serviceStatus.running && serviceStatus.healthy;
        const recordingSupported = VoiceCore.isRecordingSupported();

        // STT controls
        if (elements.sttRecordButton) {
            elements.sttRecordButton.disabled = !serviceReady || !recordingSupported;
        }

        // TTS controls
        updateTTSControls(VoiceCore.isPlaying());

        // STS controls
        if (elements.stsStartButton) {
            elements.stsStartButton.disabled = !serviceReady || !recordingSupported;
        }
    }

    /**
     * Update slider background for volume control
     */
    function updateSliderBackground(slider) {
        if (!slider) return;
        const value = (slider.value - slider.min) / (slider.max - slider.min) * 100;
        slider.style.setProperty('--range-value', `${value}%`);
    }

    /**
     * Add message to console
     */
    function addConsoleMessage(type, message) {
        const console = elements.consoleOutput;
        if (!console) return;

        const timestamp = new Date().toLocaleTimeString();
        const line = document.createElement('div');
        line.className = `console-line console-${type}`;
        line.innerHTML = `
            <span class="console-timestamp">[${timestamp}]</span>
            <span class="console-message">${escapeHtml(message)}</span>
        `;

        console.appendChild(line);

        // Auto-scroll if enabled
        if (elements.autoScrollConsole?.checked) {
            console.scrollTop = console.scrollHeight;
        }

        // Limit console lines
        while (console.children.length > 100) {
            console.removeChild(console.firstChild);
        }
    }

    /**
     * Clear console output
     */
    function clearConsole() {
        if (elements.consoleOutput) {
            elements.consoleOutput.innerHTML = '';
        }
        addConsoleMessage('info', 'Console cleared');
    }

    /**
     * Add item to session history
     */
    function addHistoryItem(mode, input, response) {
        const historyContainer = elements.sessionHistory;
        if (!historyContainer) return;

        const isEmpty = historyContainer.querySelector('.history-empty');
        if (isEmpty) {
            historyContainer.innerHTML = '';
        }

        const timestamp = new Date().toLocaleTimeString();
        const historyItem = document.createElement('div');
        historyItem.className = 'history-item fade-in-up';
        historyItem.innerHTML = `
            <div class="history-timestamp">${timestamp}</div>
            <div class="history-mode">${mode.toUpperCase()}</div>
            <div class="history-content">${escapeHtml(input)}</div>
            <div class="history-response">${escapeHtml(response)}</div>
        `;

        historyContainer.insertBefore(historyItem, historyContainer.firstChild);

        // Limit history items
        const items = historyContainer.querySelectorAll('.history-item');
        if (items.length > config.maxHistoryItems) {
            items[items.length - 1].remove();
        }
    }

    /**
     * Clear session history
     */
    function clearHistory() {
        if (elements.sessionHistory) {
            elements.sessionHistory.innerHTML = `
                <div class="history-empty">
                    <i class="fas fa-history"></i>
                    <p>No interactions yet. Start using voice features to see history here.</p>
                </div>
            `;
        }
    }

    /**
     * Hide modal
     */
    function hideModal(modalId) {
        const modal = elements[modalId];
        if (modal) {
            modal.style.display = 'none';
            modal.classList.remove('show');
        }
    }

    /**
     * Show success message using SwarmUI pattern
     */
    function showSuccess(message) {
        // Use SwarmUI's showError function pattern - SwarmUI likely has showSuccess too
        console.log(`[VoiceUI] Success: ${message}`);
        // TODO: Implement SwarmUI success notification if available
    }

    /**
     * Show error message using SwarmUI pattern
     */
    function showError(message) {
        // Use SwarmUI's showError function
        if (typeof showError === 'function') {
            showError(message);
        } else {
            console.error(`[VoiceUI] Error: ${message}`);
            alert(message); // Fallback
        }
    }

    /**
     * Escape HTML for safe display
     */
    function escapeHtml(text) {
        if (typeof escapeHtml === 'function') {
            return escapeHtml(text); // Use SwarmUI's escapeHtml if available
        }

        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    /**
     * Cleanup resources
     */
    function cleanup() {
        stopInstallationPolling();

        if (statusPolling) {
            clearInterval(statusPolling);
            statusPolling = null;
        }

        stopAllActivity();
    }

    // Public API
    return {
        init,
        cleanup,

        // Status
        refreshServiceStatus,

        // Utilities
        showSuccess,
        showError,
        addConsoleMessage
    };
})();

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', VoiceUI.init);
} else {
    VoiceUI.init();
}

// Cleanup on page unload
window.addEventListener('beforeunload', VoiceUI.cleanup);
