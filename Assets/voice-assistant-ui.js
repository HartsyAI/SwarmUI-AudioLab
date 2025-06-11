/**
 * Voice Assistant UI Module
 * Handles all DOM manipulation, user interface updates, and user interactions.
 * Provides a clean interface between the core application logic and the HTML elements.
 */

class VoiceAssistantUI {
    constructor() {
        // DOM element cache
        this.elements = {};

        // Event handlers
        this.eventHandlers = {
            refreshStatus: null,
            startService: null,
            stopService: null,
            checkInstallation: null,
            toggleRecording: null,
            sendTextCommand: null,
            volumeChange: null,
            clearTranscript: null,
            clearHistory: null
        };

        // Auto-hide timers
        this.autoHideTimers = {};

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
            'refreshStatus', 'startService', 'stopService', 'checkInstallation', 'statusIcon', 'statusText',
            'toggleRecording', 'recordingStatus', 'voiceLanguage', 'ttsVoice', 'voiceVolume',
            'volumeValue', 'textCommand', 'sendTextCommand', 'currentTranscript', 'commandHistory',
            'clearTranscript', 'clearHistory', 'errorAlert', 'errorMessage', 'successAlert', 'successMessage',
            'warningAlert', 'warningMessage', 'installationProgress', 'progressBar', 'installationStatus',
            'installationDetails', 'installationInfo', 'sttEngine', 'ttsEngine', 'backendStatus', 'lastHealthCheck'
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
        // Service control buttons
        if (this.elements.refreshStatus) {
            this.elements.refreshStatus.addEventListener('click', () => {
                if (this.eventHandlers.refreshStatus) {
                    this.eventHandlers.refreshStatus();
                }
            });
        }

        if (this.elements.startService) {
            this.elements.startService.addEventListener('click', () => {
                if (this.eventHandlers.startService) {
                    this.eventHandlers.startService();
                }
            });
        }

        if (this.elements.stopService) {
            this.elements.stopService.addEventListener('click', () => {
                if (this.eventHandlers.stopService) {
                    this.eventHandlers.stopService();
                }
            });
        }

        if (this.elements.checkInstallation) {
            this.elements.checkInstallation.addEventListener('click', () => {
                if (this.eventHandlers.checkInstallation) {
                    this.eventHandlers.checkInstallation();
                }
            });
        }

        // Voice recording button
        if (this.elements.toggleRecording) {
            this.elements.toggleRecording.addEventListener('click', () => {
                if (this.eventHandlers.toggleRecording) {
                    this.eventHandlers.toggleRecording();
                }
            });
        }

        // Text command input
        if (this.elements.sendTextCommand) {
            this.elements.sendTextCommand.addEventListener('click', () => {
                if (this.eventHandlers.sendTextCommand && this.elements.textCommand) {
                    this.eventHandlers.sendTextCommand(this.elements.textCommand.value);
                }
            });
        }

        if (this.elements.textCommand) {
            this.elements.textCommand.addEventListener('keypress', (e) => {
                if (e.key === 'Enter' && this.eventHandlers.sendTextCommand) {
                    this.eventHandlers.sendTextCommand(this.elements.textCommand.value);
                }
            });
        }

        // Volume slider
        if (this.elements.voiceVolume) {
            this.elements.voiceVolume.addEventListener('input', (e) => {
                if (this.elements.volumeValue) {
                    this.elements.volumeValue.textContent = e.target.value;
                }
                if (this.eventHandlers.volumeChange) {
                    this.eventHandlers.volumeChange(parseFloat(e.target.value));
                }
            });
        }

        // Clear buttons
        if (this.elements.clearTranscript) {
            this.elements.clearTranscript.addEventListener('click', () => {
                if (this.eventHandlers.clearTranscript) {
                    this.eventHandlers.clearTranscript();
                }
            });
        }

        if (this.elements.clearHistory) {
            this.elements.clearHistory.addEventListener('click', () => {
                if (this.eventHandlers.clearHistory) {
                    this.eventHandlers.clearHistory();
                }
            });
        }

        // Alert close buttons (global functions)
        window.hideError = () => this.hideError();
        window.hideSuccess = () => this.hideSuccess();
        window.hideWarning = () => this.hideWarning();
    }

    /**
     * Initialize UI state
     */
    initializeUI() {
        // Clear transcript and history displays
        this.clearTranscript();
        this.updateHistoryDisplay([]);

        // Hide all alerts
        this.hideAllAlerts();

        // Hide installation progress
        this.hideInstallationProgress();

        // Set default values
        if (this.elements.volumeValue) {
            this.elements.volumeValue.textContent = '0.8';
        }
    }

    /**
     * Event handler registration methods
     */
    onRefreshStatus(handler) { this.eventHandlers.refreshStatus = handler; }
    onStartService(handler) { this.eventHandlers.startService = handler; }
    onStopService(handler) { this.eventHandlers.stopService = handler; }
    onCheckInstallation(handler) { this.eventHandlers.checkInstallation = handler; }
    onToggleRecording(handler) { this.eventHandlers.toggleRecording = handler; }
    onSendTextCommand(handler) { this.eventHandlers.sendTextCommand = handler; }
    onVolumeChange(handler) { this.eventHandlers.volumeChange = handler; }
    onClearTranscript(handler) { this.eventHandlers.clearTranscript = handler; }
    onClearHistory(handler) { this.eventHandlers.clearHistory = handler; }

    /**
     * Update service status display
     */
    updateServiceStatus(status, text) {
        if (this.elements.statusIcon) {
            const iconMap = {
                'online': '<i class="fas fa-circle text-success"></i>',
                'offline': '<i class="fas fa-circle text-secondary"></i>',
                'warning': '<i class="fas fa-circle text-warning"></i>',
                'error': '<i class="fas fa-circle text-danger"></i>',
                'checking': '<i class="fas fa-circle text-info spinning"></i>',
                'starting': '<i class="fas fa-circle text-info spinning"></i>',
                'stopping': '<i class="fas fa-circle text-warning spinning"></i>'
            };

            this.elements.statusIcon.innerHTML = iconMap[status] || iconMap['offline'];
        }

        if (this.elements.statusText) {
            this.elements.statusText.textContent = text;
        }

        // Update last health check time
        if (this.elements.lastHealthCheck) {
            this.elements.lastHealthCheck.textContent = new Date().toLocaleTimeString();
        }
    }

    /**
     * Update UI state based on application state
     */
    updateState(state) {
        // Update service control buttons
        if (this.elements.startService) {
            this.elements.startService.disabled = state.serviceRunning || state.isInstallingDependencies;
        }
        if (this.elements.stopService) {
            this.elements.stopService.disabled = !state.serviceRunning || state.isInstallingDependencies;
        }

        // Update recording button
        this.updateRecordingButton(state);

        // Update recording status
        this.updateRecordingStatus(state);

        // Update text command button
        if (this.elements.sendTextCommand) {
            this.elements.sendTextCommand.disabled = state.isProcessing || state.isInstallingDependencies;
        }
    }

    /**
     * Update recording button appearance and state
     */
    updateRecordingButton(state) {
        const recordButton = this.elements.toggleRecording;
        if (!recordButton) return;

        const canRecord = state.serviceRunning && state.serviceHealthy && !state.isProcessing && !state.isInstallingDependencies;
        recordButton.disabled = !canRecord;

        if (state.isRecording) {
            recordButton.className = 'btn btn-danger btn-lg voice-record-btn recording';
            recordButton.innerHTML = '<i class="fas fa-stop"></i><span class="btn-text">Stop Recording</span>';
        } else if (state.isProcessing) {
            recordButton.className = 'btn btn-warning btn-lg voice-record-btn processing';
            recordButton.innerHTML = '<i class="fas fa-cog fa-spin"></i><span class="btn-text">Processing...</span>';
        } else {
            recordButton.className = 'btn btn-primary btn-lg voice-record-btn';
            recordButton.innerHTML = '<i class="fas fa-microphone"></i><span class="btn-text">Start Listening</span>';
        }
    }

    /**
     * Update recording status text
     */
    updateRecordingStatus(state) {
        const statusElement = this.elements.recordingStatus;
        if (!statusElement) return;

        if (state.isRecording) {
            statusElement.className = 'recording-status listening';
            statusElement.innerHTML = '<span class="status-text">Listening...</span>';
        } else if (state.isProcessing) {
            statusElement.className = 'recording-status processing';
            statusElement.innerHTML = '<span class="status-text">Processing...</span>';
        } else if (state.isInstallingDependencies) {
            statusElement.className = 'recording-status processing';
            statusElement.innerHTML = '<span class="status-text">Installing dependencies...</span>';
        } else if (state.serviceRunning && state.serviceHealthy) {
            statusElement.className = 'recording-status ready';
            statusElement.innerHTML = '<span class="status-text">Ready to listen</span>';
        } else {
            statusElement.className = 'recording-status';
            statusElement.innerHTML = '<span class="status-text">Service not ready</span>';
        }
    }

    /**
     * Get current settings from UI elements
     */
    getCurrentSettings() {
        return {
            language: this.elements.voiceLanguage?.value || 'en-US',
            voice: this.elements.ttsVoice?.value || 'default',
            volume: parseFloat(this.elements.voiceVolume?.value || '0.8')
        };
    }

    /**
     * Update transcript display
     */
    updateTranscript(text) {
        const transcriptElement = this.elements.currentTranscript;
        if (!transcriptElement) return;

        transcriptElement.innerHTML = `
            <div class="transcript-item">
                <div class="timestamp">${new Date().toLocaleTimeString()}</div>
                <div class="content">${this.escapeHtml(text)}</div>
            </div>
        `;
    }

    /**
     * Clear transcript display
     */
    clearTranscript() {
        const transcriptElement = this.elements.currentTranscript;
        if (transcriptElement) {
            transcriptElement.innerHTML = `
                <div class="empty-state">
                    <i class="fas fa-microphone-slash text-muted"></i>
                    <p class="text-muted mb-0">No transcript available</p>
                </div>
            `;
        }
    }

    /**
     * Update command history display
     */
    updateHistoryDisplay(commandHistory) {
        const historyElement = this.elements.commandHistory;
        if (!historyElement) return;

        if (commandHistory.length === 0) {
            historyElement.innerHTML = `
                <div class="empty-state">
                    <i class="fas fa-history text-muted"></i>
                    <p class="text-muted mb-0">No commands yet</p>
                </div>
            `;
            return;
        }

        const historyHTML = commandHistory.map(item => `
            <div class="history-item">
                <div class="timestamp">${item.timestamp.toLocaleTimeString()}</div>
                <div class="command-type">${item.type}</div>
                <div class="content">${this.escapeHtml(item.input)}</div>
                <div class="response">${this.escapeHtml(item.response)}</div>
            </div>
        `).join('');

        historyElement.innerHTML = historyHTML;
    }

    /**
     * Clear text input
     */
    clearTextInput() {
        if (this.elements.textCommand) {
            this.elements.textCommand.value = '';
        }
    }

    /**
     * Show/hide installation progress
     */
    showInstallationProgress(show, progress = 0, status = '') {
        if (this.elements.installationProgress) {
            this.elements.installationProgress.style.display = show ? 'block' : 'none';
        }

        if (show) {
            this.updateInstallationProgress(progress, status);
        }
    }

    /**
     * Hide installation progress
     */
    hideInstallationProgress() {
        this.showInstallationProgress(false);
    }

    /**
     * Update installation progress
     */
    updateInstallationProgress(progress, status) {
        if (this.elements.progressBar) {
            this.elements.progressBar.style.width = `${progress}%`;
            this.elements.progressBar.setAttribute('aria-valuenow', progress);
        }

        this.updateInstallationInfo(status);
    }

    /**
     * Update real-time progress from API
     */
    updateRealTimeProgress(progressData) {
        const progress = progressData.progress || 0;
        const currentStep = progressData.current_step || '';
        const currentPackage = progressData.current_package || '';
        const downloadProgress = progressData.download_progress || 0;
        const statusMessage = progressData.status_message || '';

        // Update main progress bar
        if (this.elements.progressBar) {
            this.elements.progressBar.style.width = `${progress}%`;
            this.elements.progressBar.setAttribute('aria-valuenow', progress);
        }

        // Create detailed status message
        let detailedMessage = statusMessage;
        if (currentPackage && downloadProgress > 0) {
            detailedMessage = `${currentStep}: ${currentPackage} (${downloadProgress}% downloaded)`;
        } else if (currentPackage) {
            detailedMessage = `${currentStep}: ${currentPackage}`;
        } else if (currentStep) {
            detailedMessage = currentStep;
        }

        // Update status text
        this.updateInstallationInfo(detailedMessage);
    }

    /**
     * Update installation status text
     */
    updateInstallationInfo(text) {
        if (this.elements.installationStatus) {
            this.elements.installationStatus.innerHTML = `<small class="text-muted">${text}</small>`;
        }
    }

    /**
     * Show/hide installation details panel
     */
    showInstallationDetails(show) {
        if (this.elements.installationDetails) {
            this.elements.installationDetails.style.display = show ? 'block' : 'none';
        }
    }

    /**
     * Display installation check results
     */
    displayInstallationResults(result) {
        const info = this.elements.installationInfo;
        if (!info) return;

        let html = `
            <div class="row">
                <div class="col-md-6">
                    <h6><i class="fas fa-cog"></i> System Information</h6>
                    <ul class="list-unstyled">
                        <li><strong>Python Detected:</strong> ${result.python_detected ? '✅ Yes' : '❌ No'}</li>
                        <li><strong>Operating System:</strong> ${result.operating_system || 'Unknown'}</li>
                        <li><strong>Python Type:</strong> ${result.is_embedded_python ? 'Embedded' : 'Virtual Environment'}</li>
                        <li><strong>Dependencies:</strong> ${result.dependencies_installed ? '✅ Installed' : '⚠️ Missing'}</li>
                    </ul>
                </div>
                <div class="col-md-6">
                    <h6><i class="fas fa-puzzle-piece"></i> Package Status</h6>
        `;

        if (result.installation_details) {
            const details = result.installation_details;

            // Core packages
            if (details.core_packages) {
                html += '<p><strong>Core Packages:</strong></p><ul class="list-unstyled">';
                Object.entries(details.core_packages).forEach(([pkg, installed]) => {
                    html += `<li>${installed ? '✅' : '❌'} ${pkg}</li>`;
                });
                html += '</ul>';
            }

            // STT packages
            if (details.stt_packages) {
                html += '<p><strong>Speech-to-Text:</strong></p><ul class="list-unstyled">';
                Object.entries(details.stt_packages).forEach(([pkg, installed]) => {
                    html += `<li>${installed ? '✅' : '❌'} ${pkg}</li>`;
                });
                html += '</ul>';
            }

            // TTS packages
            if (details.tts_packages) {
                html += '<p><strong>Text-to-Speech:</strong></p><ul class="list-unstyled">';
                Object.entries(details.tts_packages).forEach(([pkg, installed]) => {
                    html += `<li>${installed ? '✅' : '❌'} ${pkg}</li>`;
                });
                html += '</ul>';
            }
        }

        html += `
                </div>
            </div>
        `;

        if (!result.dependencies_installed) {
            html += `
                <div class="alert alert-warning mt-3">
                    <i class="fas fa-exclamation-triangle"></i>
                    <strong>Dependencies Missing:</strong> Some required packages are not installed. 
                    Click "Start Service" to automatically install them. This process includes downloading 
                    large packages like TorchAudio (~200MB) and may take 10-15 minutes.
                </div>
            `;
        } else {
            html += `
                <div class="alert alert-success mt-3">
                    <i class="fas fa-check-circle"></i>
                    <strong>Ready:</strong> All dependencies are installed and ready to use.
                </div>
            `;
        }

        info.innerHTML = html;
    }

    /**
     * Show error message
     */
    showError(message) {
        console.error('[VoiceAssistant] Error:', message);
        this.showAlert('error', message, 8000);
    }

    /**
     * Show success message
     */
    showSuccess(message) {
        console.log('[VoiceAssistant] Success:', message);
        this.showAlert('success', message, 4000);
    }

    /**
     * Show warning message
     */
    showWarning(message) {
        console.warn('[VoiceAssistant] Warning:', message);
        this.showAlert('warning', message, 6000);
    }

    /**
     * Generic alert display method
     */
    showAlert(type, message, autoHideDelay = 0) {
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
     * Hide error message
     */
    hideError() {
        this.hideAlert('error');
    }

    /**
     * Hide success message
     */
    hideSuccess() {
        this.hideAlert('success');
    }

    /**
     * Hide warning message
     */
    hideWarning() {
        this.hideAlert('warning');
    }

    /**
     * Hide all alerts
     */
    hideAllAlerts() {
        ['error', 'success', 'warning'].forEach(type => {
            this.hideAlert(type);
        });
    }

    /**
     * Update system information display
     */
    updateSystemInfo(key, value) {
        if (this.elements[key]) {
            this.elements[key].textContent = value;
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
     * Add CSS animation class
     */
    addAnimation(element, animationClass, duration = 1000) {
        if (element && animationClass) {
            element.classList.add(animationClass);
            setTimeout(() => {
                element.classList.remove(animationClass);
            }, duration);
        }
    }

    /**
     * Show loading state for an element
     */
    showLoading(elementId, show = true) {
        const element = this.elements[elementId] || document.getElementById(elementId);
        if (element) {
            if (show) {
                element.classList.add('loading');
            } else {
                element.classList.remove('loading');
            }
        }
    }

    /**
     * Update button state (enabled/disabled with visual feedback)
     */
    updateButtonState(buttonId, enabled = true, text = null) {
        const button = this.elements[buttonId] || document.getElementById(buttonId);
        if (button) {
            button.disabled = !enabled;
            if (text) {
                button.textContent = text;
            }

            if (enabled) {
                button.classList.remove('btn-secondary');
                button.classList.add('btn-primary');
            } else {
                button.classList.remove('btn-primary');
                button.classList.add('btn-secondary');
            }
        }
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

            // Remove global functions
            if (window.hideError) delete window.hideError;
            if (window.hideSuccess) delete window.hideSuccess;
            if (window.hideWarning) delete window.hideWarning;

            console.log('[VoiceAssistant] UI module destroyed');
        } catch (error) {
            console.error('[VoiceAssistant] Error during UI cleanup:', error);
        }
    }
}

// Export for global access
window.VoiceAssistantUI = VoiceAssistantUI;
