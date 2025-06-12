/**
 * Voice Assistant UI Module - Complete Redesign
 * Handles all DOM manipulation, user interface updates, and user interactions.
 * Provides a clean interface between the core application logic and the HTML elements.
 */

class VoiceAssistantUI {
    constructor() {
        // DOM element cache
        this.elements = {};

        // Event handlers
        this.eventHandlers = {
            // Service control
            refreshServiceStatus: null,
            startService: null,
            stopService: null,
            confirmInstallation: null,

            // Mode switching
            switchMode: null,

            // STT mode
            sttStartRecording: null,
            sttStopRecording: null,

            // TTS mode
            ttsSpeak: null,
            ttsStop: null,

            // STS mode
            stsStartConversation: null,
            stsStopConversation: null,

            // Utility
            clearConsole: null,
            clearHistory: null
        };

        // UI state
        this.currentMode = 'stt';
        this.consoleLines = [];
        this.maxConsoleLines = 100;
        this.autoHideTimers = {};

        // Bootstrap modal instances
        this.modals = {};

        console.log('[VoiceAssistant] UI module initialized');
    }

    /**
     * Initialize the UI module and cache DOM elements
     */
    initialize() {
        try {
            console.log('[VoiceAssistant] Initializing UI module');

            // Cache DOM elements
            this.cacheElements();

            // Set up event listeners
            this.setupEventListeners();

            // Initialize modals
            this.initializeModals();

            // Initialize UI state
            this.initializeUI();

            console.log('[VoiceAssistant] UI module initialization complete');
            return true;
        } catch (error) {
            console.error('[VoiceAssistant] UI initialization failed:', error);
            return false;
        }
    }

    /**
     * Cache frequently used DOM elements
     */
    cacheElements() {
        const elementIds = [
            // Service control
            'refreshServiceStatus', 'primaryServiceButton', 'serviceStatusIcon', 'serviceStatusText',
            'dependenciesCounter', 'coreDependencies', 'aiEngines',

            // Console
            'consoleOutput', 'clearConsole', 'autoScrollConsole',

            // Mode tabs
            'sttModeTab', 'ttsModeTab', 'stsModeTab', 'commandsModeTab',

            // Mode panels
            'sttMode', 'ttsMode', 'stsMode', 'commandsMode',

            // STT elements
            'sttRecordButton', 'sttRecordingStatus', 'sttLanguage', 'sttConfidence', 'sttTranscription',

            // TTS elements
            'ttsTextInput', 'ttsCharCount', 'ttsVoice', 'ttsVolume', 'ttsVolumeNumber',
            'ttsSpeakButton', 'ttsStopButton',

            // STS elements
            'stsStartButton', 'stsStatus', 'stsUserSpeech', 'stsAiResponse', 'stsPlayResponse',

            // History
            'sessionHistory', 'clearHistory',

            // Modals
            'installationModal', 'installationProgressModal', 'confirmInstallation',
            'packagesToInstall', 'installationProgressText', 'installationProgressPercent',
            'installationProgressBar', 'currentPackage', 'packageProgress', 'installationLiveOutput',

            // Alerts
            'successAlert', 'successMessage', 'errorAlert', 'errorMessage',
            'warningAlert', 'warningMessage', 'infoAlert', 'infoMessage'
        ];

        elementIds.forEach(id => {
            this.elements[id] = document.getElementById(id);
            if (!this.elements[id]) {
                console.warn(`[VoiceAssistant] Element with ID '${id}' not found`);
            }
        });
    }

    /**
     * Set up event listeners for UI elements
     */
    setupEventListeners() {
        // Service control
        this.elements.refreshServiceStatus?.addEventListener('click', () => {
            this.eventHandlers.refreshServiceStatus?.();
        });

        this.elements.primaryServiceButton?.addEventListener('click', () => {
            this.handlePrimaryServiceButton();
        });

        this.elements.confirmInstallation?.addEventListener('click', () => {
            this.eventHandlers.confirmInstallation?.();
            this.hideModal('installationModal');
        });

        // Mode tabs
        document.querySelectorAll('.mode-tab').forEach(tab => {
            tab.addEventListener('click', (e) => {
                const mode = e.currentTarget.dataset.mode;
                if (mode && !e.currentTarget.disabled) {
                    this.switchMode(mode);
                    this.eventHandlers.switchMode?.(mode);
                }
            });
        });

        // STT controls
        this.elements.sttRecordButton?.addEventListener('click', () => {
            if (this.elements.sttRecordButton.classList.contains('recording')) {
                this.eventHandlers.sttStopRecording?.();
            } else {
                this.eventHandlers.sttStartRecording?.();
            }
        });

        // TTS controls
        this.elements.ttsSpeakButton?.addEventListener('click', () => {
            const text = this.elements.ttsTextInput?.value?.trim();
            if (text) {
                this.eventHandlers.ttsSpeak?.(text);
            }
        });

        this.elements.ttsStopButton?.addEventListener('click', () => {
            this.eventHandlers.ttsStop?.();
        });

        this.elements.ttsTextInput?.addEventListener('input', (e) => {
            this.updateCharCount(e.target.value);
        });

        // TTS volume slider sync
        this.elements.ttsVolume?.addEventListener('input', (e) => {
            this.elements.ttsVolumeNumber.value = e.target.value;
            this.updateSliderBackground(e.target);
        });

        this.elements.ttsVolumeNumber?.addEventListener('input', (e) => {
            this.elements.ttsVolume.value = e.target.value;
            this.updateSliderBackground(this.elements.ttsVolume);
        });

        // STS controls
        this.elements.stsStartButton?.addEventListener('click', () => {
            if (this.elements.stsStartButton.classList.contains('recording')) {
                this.eventHandlers.stsStopConversation?.();
            } else {
                this.eventHandlers.stsStartConversation?.();
            }
        });

        this.elements.stsPlayResponse?.addEventListener('click', () => {
            // TODO: Implement audio playback for STS responses
            console.log('[VoiceAssistant] TODO: Implement STS response playback');
        });

        // Console controls
        this.elements.clearConsole?.addEventListener('click', () => {
            this.clearConsole();
            this.eventHandlers.clearConsole?.();
        });

        // History controls
        this.elements.clearHistory?.addEventListener('click', () => {
            this.clearHistory();
            this.eventHandlers.clearHistory?.();
        });

        // Global alert close handlers
        window.hideVoiceAlert = (type) => this.hideAlert(type);

        console.log('[VoiceAssistant] Event listeners set up');
    }

    /**
     * Initialize Bootstrap modals
     */
    initializeModals() {
        try {
            // Initialize modals if Bootstrap is available
            if (window.bootstrap) {
                this.modals.installation = new bootstrap.Modal(this.elements.installationModal);
                this.modals.installationProgress = new bootstrap.Modal(this.elements.installationProgressModal);
            }
        } catch (error) {
            console.warn('[VoiceAssistant] Bootstrap modals not available:', error);
        }
    }

    /**
     * Initialize UI state
     */
    initializeUI() {
        // Set initial mode
        this.switchMode('stt');

        // Initialize dependencies display
        this.updateDependenciesDisplay([]);

        // Initialize console
        this.addConsoleMessage('info', 'Voice Assistant UI initialized');

        // Clear history
        this.clearHistory();

        // Hide all alerts
        this.hideAllAlerts();

        // Initialize volume slider
        this.updateSliderBackground(this.elements.ttsVolume);

        // Initialize char count
        this.updateCharCount('');
    }

    /**
     * Event handler registration methods
     */
    onRefreshServiceStatus(handler) { this.eventHandlers.refreshServiceStatus = handler; }
    onStartService(handler) { this.eventHandlers.startService = handler; }
    onStopService(handler) { this.eventHandlers.stopService = handler; }
    onConfirmInstallation(handler) { this.eventHandlers.confirmInstallation = handler; }
    onSwitchMode(handler) { this.eventHandlers.switchMode = handler; }
    onSTTStartRecording(handler) { this.eventHandlers.sttStartRecording = handler; }
    onSTTStopRecording(handler) { this.eventHandlers.sttStopRecording = handler; }
    onTTSSpeak(handler) { this.eventHandlers.ttsSpeak = handler; }
    onTTSStop(handler) { this.eventHandlers.ttsStop = handler; }
    onSTSStartConversation(handler) { this.eventHandlers.stsStartConversation = handler; }
    onSTSStopConversation(handler) { this.eventHandlers.stsStopConversation = handler; }
    onClearConsole(handler) { this.eventHandlers.clearConsole = handler; }
    onClearHistory(handler) { this.eventHandlers.clearHistory = handler; }

    /**
     * Handle primary service button click (Install/Start/Stop)
     */
    handlePrimaryServiceButton() {
        const buttonText = this.elements.primaryServiceButton?.textContent || '';

        if (buttonText.includes('Install')) {
            this.showInstallationModal();
        } else if (buttonText.includes('Start')) {
            this.eventHandlers.startService?.();
        } else if (buttonText.includes('Stop')) {
            this.eventHandlers.stopService?.();
        }
    }

    /**
     * Update service status display
     */
    updateServiceStatus(status, text, isHealthy = false, isRunning = false) {
        if (this.elements.serviceStatusIcon) {
            const iconMap = {
                'online': '<i class="fas fa-circle" style="color: var(--green);"></i>',
                'offline': '<i class="fas fa-circle" style="color: var(--text-soft);"></i>',
                'warning': '<i class="fas fa-circle" style="color: var(--emphasis);"></i>',
                'error': '<i class="fas fa-circle" style="color: var(--red);"></i>',
                'checking': '<i class="fas fa-spinner fa-spin" style="color: var(--text);"></i>',
                'starting': '<i class="fas fa-spinner fa-spin" style="color: var(--emphasis);"></i>',
                'stopping': '<i class="fas fa-spinner fa-spin" style="color: var(--emphasis);"></i>',
                'installing': '<i class="fas fa-cog fa-spin" style="color: var(--emphasis);"></i>'
            };

            this.elements.serviceStatusIcon.innerHTML = iconMap[status] || iconMap['offline'];
        }

        if (this.elements.serviceStatusText) {
            this.elements.serviceStatusText.textContent = text;
        }

        // Update primary button based on service state
        this.updatePrimaryServiceButton(isHealthy, isRunning);
    }

    /**
     * Update primary service button state
     */
    updatePrimaryServiceButton(isHealthy, isRunning, dependenciesInstalled = true) {
        const button = this.elements.primaryServiceButton;
        if (!button) return;

        if (!dependenciesInstalled) {
            button.innerHTML = '<i class="fas fa-download"></i> Install Dependencies';
            button.disabled = false;
            button.className = 'basic-button btn-primary';
        } else if (!isRunning) {
            button.innerHTML = '<i class="fas fa-play"></i> Start Service';
            button.disabled = false;
            button.className = 'basic-button btn-primary';
        } else if (isRunning && isHealthy) {
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
     * Update dependencies display
     */
    updateDependenciesDisplay(dependencies) {
        // Update counter
        const installed = dependencies.filter(dep => dep.status === 'installed').length;
        const total = dependencies.length;

        if (this.elements.dependenciesCounter) {
            this.elements.dependenciesCounter.textContent = `${installed}/${total} installed`;
        }

        // Group dependencies by category
        const corePackages = dependencies.filter(dep => dep.category === 'core');
        const aiEngines = dependencies.filter(dep => dep.category === 'ai');

        this.updateDependencyCategory('coreDependencies', corePackages);
        this.updateDependencyCategory('aiEngines', aiEngines);
    }

    /**
     * Update a dependency category display
     */
    updateDependencyCategory(elementId, packages) {
        const container = this.elements[elementId];
        if (!container) return;

        if (packages.length === 0) {
            container.innerHTML = '<div class="dependency-loading">Checking...</div>';
            return;
        }

        const html = packages.map(pkg => `
            <div class="dependency-item">
                <span class="dependency-status ${pkg.status}">
                    ${this.getDependencyStatusIcon(pkg.status)}
                </span>
                <span class="dependency-name">${pkg.name}</span>
                ${pkg.version ? `<span class="dependency-version">${pkg.version}</span>` : ''}
            </div>
        `).join('');

        container.innerHTML = html;
    }

    /**
     * Get dependency status icon
     */
    getDependencyStatusIcon(status) {
        const icons = {
            'installed': '✅',
            'missing': '❌',
            'installing': '⚙️',
            'error': '⚠️'
        };
        return icons[status] || '❓';
    }

    /**
     * Switch between modes (STT/TTS/STS/Commands)
     */
    switchMode(mode) {
        this.currentMode = mode;

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

        const activePanel = this.elements[`${mode}Mode`];
        if (activePanel) {
            activePanel.classList.add('active');
        }

        this.addConsoleMessage('info', `Switched to ${mode.toUpperCase()} mode`);
    }

    /**
     * Update STT recording state
     */
    updateSTTRecordingState(isRecording, isProcessing = false) {
        const button = this.elements.sttRecordButton;
        const status = this.elements.sttRecordingStatus;

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
     * Update STT transcription display
     */
    updateSTTTranscription(text, confidence = null) {
        const transcription = this.elements.sttTranscription;
        const confidenceDisplay = this.elements.sttConfidence;

        if (transcription) {
            if (text) {
                transcription.textContent = text;
                transcription.classList.remove('empty');
            } else {
                transcription.textContent = 'Click "Start Listening" to begin speech recognition';
                transcription.classList.add('empty');
            }
        }

        if (confidenceDisplay && confidence !== null) {
            confidenceDisplay.textContent = `${Math.round(confidence * 100)}%`;
        }
    }

    /**
     * Update TTS controls state
     */
    updateTTSControlsState(isSpeaking, canSpeak = true) {
        const speakButton = this.elements.ttsSpeakButton;
        const stopButton = this.elements.ttsStopButton;

        if (speakButton) {
            speakButton.disabled = !canSpeak || isSpeaking;
            if (isSpeaking) {
                speakButton.innerHTML = '<i class="fas fa-cog fa-spin"></i> Speaking...';
            } else {
                speakButton.innerHTML = '<i class="fas fa-volume-up"></i> Speak Text';
            }
        }

        if (stopButton) {
            stopButton.disabled = !isSpeaking;
        }
    }

    /**
     * Update TTS character count
     */
    updateCharCount(text) {
        const charCount = this.elements.ttsCharCount;
        if (charCount) {
            charCount.textContent = text.length;

            // Color coding for character count
            if (text.length > 800) {
                charCount.style.color = 'var(--red)';
            } else if (text.length > 600) {
                charCount.style.color = 'var(--emphasis)';
            } else {
                charCount.style.color = 'var(--text-soft)';
            }
        }

        // Update speak button state
        const speakButton = this.elements.ttsSpeakButton;
        if (speakButton) {
            speakButton.disabled = text.trim().length === 0;
        }
    }

    /**
     * Update STS conversation state
     */
    updateSTSConversationState(isActive, userSpeech = '', aiResponse = '') {
        const button = this.elements.stsStartButton;
        const status = this.elements.stsStatus;
        const userBubble = this.elements.stsUserSpeech;
        const aiBubble = this.elements.stsAiResponse;
        const playButton = this.elements.stsPlayResponse;

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

        if (userBubble) {
            if (userSpeech) {
                userBubble.textContent = userSpeech;
                userBubble.classList.remove('empty');
            } else {
                userBubble.textContent = 'Start conversation to see your speech here';
                userBubble.classList.add('empty');
            }
        }

        if (aiBubble) {
            if (aiResponse) {
                aiBubble.textContent = aiResponse;
                aiBubble.classList.remove('empty');
                if (playButton) {
                    playButton.style.display = 'flex';
                }
            } else {
                aiBubble.textContent = 'AI responses will appear here';
                aiBubble.classList.add('empty');
                if (playButton) {
                    playButton.style.display = 'none';
                }
            }
        }
    }

    /**
     * Add message to console output
     */
    addConsoleMessage(type, message, timestamp = null) {
        const ts = timestamp || new Date().toLocaleTimeString();
        const line = { type, message, timestamp: ts };

        this.consoleLines.push(line);

        // Limit console lines
        if (this.consoleLines.length > this.maxConsoleLines) {
            this.consoleLines = this.consoleLines.slice(-this.maxConsoleLines);
        }

        this.updateConsoleDisplay();
    }

    /**
     * Update console display
     */
    updateConsoleDisplay() {
        const console = this.elements.consoleOutput;
        if (!console) return;

        const html = this.consoleLines.map(line => `
            <div class="console-line console-${line.type}">
                <span class="console-timestamp">[${line.timestamp}]</span>
                <span class="console-message">${this.escapeHtml(line.message)}</span>
            </div>
        `).join('');

        console.innerHTML = html;

        // Auto-scroll if enabled
        if (this.elements.autoScrollConsole?.checked) {
            console.scrollTop = console.scrollHeight;
        }
    }

    /**
     * Clear console output
     */
    clearConsole() {
        this.consoleLines = [];
        this.updateConsoleDisplay();
    }

    /**
     * Add item to session history
     */
    addHistoryItem(mode, input, response, timestamp = null) {
        const historyContainer = this.elements.sessionHistory;
        if (!historyContainer) return;

        const ts = timestamp || new Date().toLocaleTimeString();
        const isEmpty = historyContainer.querySelector('.history-empty');

        if (isEmpty) {
            historyContainer.innerHTML = '';
        }

        const historyItem = document.createElement('div');
        historyItem.className = 'history-item fade-in-up';
        historyItem.innerHTML = `
            <div class="history-timestamp">${ts}</div>
            <div class="history-mode">${mode.toUpperCase()}</div>
            <div class="history-content">${this.escapeHtml(input)}</div>
            <div class="history-response">${this.escapeHtml(response)}</div>
        `;

        historyContainer.insertBefore(historyItem, historyContainer.firstChild);

        // Limit history items
        const items = historyContainer.querySelectorAll('.history-item');
        if (items.length > 20) {
            items[items.length - 1].remove();
        }
    }

    /**
     * Clear session history
     */
    clearHistory() {
        const historyContainer = this.elements.sessionHistory;
        if (historyContainer) {
            historyContainer.innerHTML = `
                <div class="history-empty">
                    <i class="fas fa-history"></i>
                    <p>No interactions yet. Start using voice features to see history here.</p>
                </div>
            `;
        }
    }

    /**
     * Show installation confirmation modal
     */
    showInstallationModal() {
        if (this.modals.installation) {
            this.modals.installation.show();
        } else {
            // Fallback for when Bootstrap is not available
            const modal = this.elements.installationModal;
            if (modal) {
                modal.style.display = 'block';
                modal.classList.add('show');
            }
        }
    }

    /**
     * Show installation progress modal
     */
    showInstallationProgressModal() {
        if (this.modals.installationProgress) {
            this.modals.installationProgress.show();
        } else {
            // Fallback for when Bootstrap is not available
            const modal = this.elements.installationProgressModal;
            if (modal) {
                modal.style.display = 'block';
                modal.classList.add('show');
            }
        }
    }

    /**
     * Hide a modal
     */
    hideModal(modalId) {
        const modalKey = modalId.replace('Modal', '');
        if (this.modals[modalKey]) {
            this.modals[modalKey].hide();
        } else {
            // Fallback
            const modal = this.elements[modalId];
            if (modal) {
                modal.style.display = 'none';
                modal.classList.remove('show');
            }
        }
    }

    /**
     * Update installation progress
     */
    updateInstallationProgress(progress, step, currentPackage = '', packageProgress = '', liveOutput = '') {
        // Update progress bar
        const progressBar = this.elements.installationProgressBar;
        const progressText = this.elements.installationProgressText;
        const progressPercent = this.elements.installationProgressPercent;

        if (progressBar) {
            progressBar.style.width = `${progress}%`;
            progressBar.setAttribute('aria-valuenow', progress);
        }

        if (progressText) {
            progressText.textContent = step;
        }

        if (progressPercent) {
            progressPercent.textContent = `${progress}%`;
        }

        // Update current package
        if (this.elements.currentPackage) {
            this.elements.currentPackage.textContent = currentPackage || '--';
        }

        if (this.elements.packageProgress) {
            this.elements.packageProgress.textContent = packageProgress || '--';
        }

        // Update live output
        if (this.elements.installationLiveOutput && liveOutput) {
            const output = this.elements.installationLiveOutput;
            output.textContent += liveOutput + '\n';
            output.scrollTop = output.scrollHeight;
        }
    }

    /**
     * Update slider background for volume control
     */
    updateSliderBackground(slider) {
        if (!slider) return;

        const value = (slider.value - slider.min) / (slider.max - slider.min) * 100;
        slider.style.setProperty('--range-value', `${value}%`);
    }

    /**
     * Show alert message
     */
    showAlert(type, message, autoHideDelay = 5000) {
        const alertElement = this.elements[`${type}Alert`];
        const messageElement = this.elements[`${type}Message`];

        if (alertElement && messageElement) {
            messageElement.textContent = message;
            alertElement.style.display = 'block';

            // Clear any existing auto-hide timer
            if (this.autoHideTimers[type]) {
                clearTimeout(this.autoHideTimers[type]);
            }

            // Set up auto-hide if specified
            if (autoHideDelay > 0) {
                this.autoHideTimers[type] = setTimeout(() => {
                    this.hideAlert(type);
                }, autoHideDelay);
            }
        }
    }

    /**
     * Hide specific alert
     */
    hideAlert(type) {
        const alertElement = this.elements[`${type}Alert`];
        if (alertElement) {
            alertElement.style.display = 'none';
        }

        // Clear auto-hide timer
        if (this.autoHideTimers[type]) {
            clearTimeout(this.autoHideTimers[type]);
            delete this.autoHideTimers[type];
        }
    }

    /**
     * Hide all alerts
     */
    hideAllAlerts() {
        ['success', 'error', 'warning', 'info'].forEach(type => {
            this.hideAlert(type);
        });
    }

    /**
     * Convenience methods for different alert types
     */
    showSuccess(message, autoHide = true) {
        this.showAlert('success', message, autoHide ? 4000 : 0);
        this.addConsoleMessage('success', message);
    }

    showError(message, autoHide = true) {
        this.showAlert('error', message, autoHide ? 8000 : 0);
        this.addConsoleMessage('error', message);
    }

    showWarning(message, autoHide = true) {
        this.showAlert('warning', message, autoHide ? 6000 : 0);
        this.addConsoleMessage('warning', message);
    }

    showInfo(message, autoHide = true) {
        this.showAlert('info', message, autoHide ? 5000 : 0);
        this.addConsoleMessage('info', message);
    }

    /**
     * Get current settings from UI
     */
    getCurrentSettings() {
        return {
            mode: this.currentMode,
            stt: {
                language: this.elements.sttLanguage?.value || 'en-US'
            },
            tts: {
                voice: this.elements.ttsVoice?.value || 'default',
                volume: parseFloat(this.elements.ttsVolume?.value || '0.8')
            }
        };
    }

    /**
     * Update UI state based on application state
     */
    updateState(state) {
        // Update service status
        if (state.isInstalling) {
            this.updateServiceStatus('installing', 'Installing dependencies...', false, false);
        } else if (state.serviceRunning && state.serviceHealthy) {
            this.updateServiceStatus('online', 'Service online', true, true);
        } else if (state.serviceRunning && !state.serviceHealthy) {
            this.updateServiceStatus('warning', 'Service starting...', false, true);
        } else {
            this.updateServiceStatus('offline', 'Service offline', false, false);
        }

        // Update button states based on service availability
        const serviceReady = state.serviceRunning && state.serviceHealthy && !state.isInstalling;

        // STT button
        if (this.elements.sttRecordButton) {
            this.elements.sttRecordButton.disabled = !serviceReady;
        }

        // TTS buttons
        if (this.elements.ttsSpeakButton) {
            const hasText = this.elements.ttsTextInput?.value?.trim().length > 0;
            this.elements.ttsSpeakButton.disabled = !serviceReady || !hasText;
        }

        if (this.elements.ttsStopButton) {
            this.elements.ttsStopButton.disabled = !serviceReady;
        }

        // STS button
        if (this.elements.stsStartButton) {
            this.elements.stsStartButton.disabled = !serviceReady;
        }
    }

    /**
     * Escape HTML for safe display
     */
    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    /**
     * Clean up UI resources
     */
    destroy() {
        try {
            // Clear all auto-hide timers
            Object.values(this.autoHideTimers).forEach(timer => {
                clearTimeout(timer);
            });
            this.autoHideTimers = {};

            // Dispose modals
            Object.values(this.modals).forEach(modal => {
                if (modal && modal.dispose) {
                    modal.dispose();
                }
            });

            // Remove global functions
            if (window.hideVoiceAlert) {
                delete window.hideVoiceAlert;
            }

            console.log('[VoiceAssistant] UI module destroyed');
        } catch (error) {
            console.error('[VoiceAssistant] Error during UI cleanup:', error);
        }
    }
}

// Export for global access
window.VoiceAssistantUI = VoiceAssistantUI;
