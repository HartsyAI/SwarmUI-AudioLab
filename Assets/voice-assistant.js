/**
 * Voice Assistant Frontend for SwarmUI - Production Ready with Progress Tracking
 * Provides voice-controlled image generation and UI interaction with real-time installation progress
 */

class SwarmVoiceAssistant {
    constructor() {
        // State management
        this.isRecording = false;
        this.isProcessing = false;
        this.serviceRunning = false;
        this.serviceHealthy = false;
        this.mediaRecorder = null;
        this.audioChunks = [];
        this.sessionId = this.generateSessionId();

        // Audio context for playback
        this.audioContext = null;

        // Command history
        this.commandHistory = [];
        this.maxHistoryItems = 20;

        // UI element references
        this.elements = {};

        // System information
        this.systemInfo = {
            sttEngine: 'Not initialized',
            ttsEngine: 'Not initialized',
            backendStatus: 'Disconnected',
            lastHealthCheck: 'Never'
        };

        // Progress tracking
        this.progressPollingInterval = null;
        this.isInstallingDependencies = false;

        // Initialize when DOM is ready
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => this.initialize());
        } else {
            this.initialize();
        }
    }

    /**
     * Generate a unique session identifier
     */
    generateSessionId() {
        return 'voice-session-' + Date.now() + '-' + Math.random().toString(36).substr(2, 9);
    }

    /**
     * Initialize the voice assistant
     */
    async initialize() {
        console.log('[VoiceAssistant] Initializing SwarmUI Voice Assistant v1.0 with Progress Tracking');

        try {
            // Cache DOM elements
            this.cacheElements();

            // Set up event listeners
            this.setupEventListeners();

            // Check browser compatibility
            if (!this.checkBrowserCompatibility()) {
                this.showError('Your browser does not support voice recording. Please use a modern browser.');
                return;
            }

            // Initial service status check
            await this.checkServiceStatus();

            console.log('[VoiceAssistant] Initialization complete');
        } catch (error) {
            console.error('[VoiceAssistant] Initialization failed:', error);
            this.showError('Failed to initialize voice assistant: ' + error.message);
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
     * Set up all event listeners
     */
    setupEventListeners() {
        // Service control buttons
        if (this.elements.refreshStatus) {
            this.elements.refreshStatus.addEventListener('click', () => this.checkServiceStatus());
        }

        if (this.elements.startService) {
            this.elements.startService.addEventListener('click', () => this.startService());
        }

        if (this.elements.stopService) {
            this.elements.stopService.addEventListener('click', () => this.stopService());
        }

        if (this.elements.checkInstallation) {
            this.elements.checkInstallation.addEventListener('click', () => this.checkInstallationStatus());
        }

        // Voice recording button
        if (this.elements.toggleRecording) {
            this.elements.toggleRecording.addEventListener('click', () => this.toggleRecording());
        }

        // Text command input
        if (this.elements.sendTextCommand) {
            this.elements.sendTextCommand.addEventListener('click', () => this.sendTextCommand());
        }

        if (this.elements.textCommand) {
            this.elements.textCommand.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    this.sendTextCommand();
                }
            });
        }

        // Volume slider
        if (this.elements.voiceVolume) {
            this.elements.voiceVolume.addEventListener('input', (e) => {
                if (this.elements.volumeValue) {
                    this.elements.volumeValue.textContent = e.target.value;
                }
            });
        }

        // Clear buttons
        if (this.elements.clearTranscript) {
            this.elements.clearTranscript.addEventListener('click', () => this.clearTranscript());
        }

        if (this.elements.clearHistory) {
            this.elements.clearHistory.addEventListener('click', () => this.clearHistory());
        }
    }

    /**
     * Check browser compatibility for voice features
     */
    checkBrowserCompatibility() {
        const hasMediaDevices = !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        const hasMediaRecorder = !!(window.MediaRecorder);
        const hasAudioContext = !!(window.AudioContext || window.webkitAudioContext);

        console.log('[VoiceAssistant] Browser compatibility:', {
            mediaDevices: hasMediaDevices,
            mediaRecorder: hasMediaRecorder,
            audioContext: hasAudioContext
        });

        if (!hasMediaDevices) {
            this.showWarning('Microphone access not available. Please use HTTPS or localhost.');
        }

        return hasMediaDevices && hasMediaRecorder && hasAudioContext;
    }

    /**
     * Check the status of the voice service
     */
    async checkServiceStatus() {
        console.log('[VoiceAssistant] Checking service status');

        try {
            this.updateStatusIcon('checking');
            this.updateSystemInfo('lastHealthCheck', 'Checking...');

            const result = await this.makeAPICall('GetVoiceStatus', {});

            if (result.success) {
                this.serviceRunning = result.backend_running || false;
                this.serviceHealthy = result.backend_healthy || false;

                console.log('[VoiceAssistant] Service status:', {
                    running: this.serviceRunning,
                    healthy: this.serviceHealthy
                });

                this.updateSystemInfo('backendStatus', this.serviceRunning && this.serviceHealthy ? 'Connected' : 'Disconnected');
                this.updateSystemInfo('lastHealthCheck', new Date().toLocaleTimeString());
                this.updateUI();

                // Get detailed service information if available
                if (this.serviceRunning && this.serviceHealthy) {
                    await this.updateServiceDetails();
                }
            } else {
                throw new Error(result.error || 'Failed to get service status');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking service status:', error);
            this.serviceRunning = false;
            this.serviceHealthy = false;
            this.updateStatusIcon('error');
            this.updateStatusText('Service unavailable');
            this.updateSystemInfo('backendStatus', 'Error');
            this.updateSystemInfo('lastHealthCheck', new Date().toLocaleTimeString());
            this.showError('Failed to check service status: ' + error.message);
        }
    }

    /**
     * Update detailed service information
     */
    async updateServiceDetails() {
        try {
            // This would call a detailed status endpoint if available
            // For now, we'll set generic information
            this.updateSystemInfo('sttEngine', 'RealtimeSTT/speech-recognition');
            this.updateSystemInfo('ttsEngine', 'Chatterbox TTS/gTTS');
        } catch (error) {
            console.warn('[VoiceAssistant] Could not get detailed service info:', error);
        }
    }

    /**
     * Update system information display
     */
    updateSystemInfo(key, value) {
        this.systemInfo[key] = value;
        if (this.elements[key]) {
            this.elements[key].textContent = value;
        }
    }

    /**
     * Start the voice service with real-time progress tracking
     */
    async startService() {
        console.log('[VoiceAssistant] Starting voice service with progress tracking');

        try {
            this.updateStatusIcon('starting');
            this.updateStatusText('Starting service...');
            this.updateUI();

            // Check installation status first
            this.showSuccess('Checking installation status...');
            this.showInstallationProgress(true, 5, 'Checking Python environment...');

            const installStatus = await this.makeAPICall('CheckInstallationStatus', {});

            if (installStatus.success) {
                this.updateInstallationProgress(10, 'Python environment detected');

                if (!installStatus.dependencies_installed) {
                    // Dependencies need installation - start progress tracking
                    this.isInstallingDependencies = true;
                    this.showSuccess('Installing dependencies automatically... This will take several minutes. Real-time progress shown below.');
                    this.updateInstallationProgress(15, 'Starting dependency installation...');

                    // Start polling for progress updates
                    this.startProgressPolling();

                    console.log('[VoiceAssistant] Dependencies need installation:', installStatus.installation_details);
                } else {
                    this.updateInstallationProgress(80, 'Dependencies already installed');
                }
            } else {
                this.updateInstallationProgress(12, 'Warning: Could not fully check installation status');
            }

            // Start the service (this will trigger dependency installation if needed)
            this.updateInstallationProgress(90, 'Starting voice service backend...');
            const result = await this.makeAPICall('StartVoiceService', {});

            if (result.success) {
                // Stop progress polling
                this.stopProgressPolling();

                this.updateInstallationProgress(100, 'Voice service started successfully!');

                // Hide progress after a delay
                setTimeout(() => {
                    this.showInstallationProgress(false);
                    this.isInstallingDependencies = false;
                }, 3000);

                if (result.first_time_setup) {
                    this.showSuccess('Voice service started successfully! Dependencies were installed automatically.');
                } else {
                    this.showSuccess('Voice service started successfully');
                }
                await this.checkServiceStatus(); // Refresh status
            } else {
                this.stopProgressPolling();
                this.showInstallationProgress(false);
                this.isInstallingDependencies = false;
                throw new Error(result.error || 'Failed to start service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error starting service:', error);
            this.stopProgressPolling();
            this.showInstallationProgress(false);
            this.isInstallingDependencies = false;

            // Provide helpful error messages based on common issues
            let errorMessage = error.message;
            if (errorMessage.includes('dependencies')) {
                errorMessage += '\n\nThis appears to be a dependency issue. The system will automatically install required packages when you start the service. This process may take 5-15 minutes on first run.';
            } else if (errorMessage.includes('Python')) {
                errorMessage += '\n\nThis appears to be a Python environment issue. Please ensure SwarmUI is properly installed with its Python backend.';
            } else if (errorMessage.includes('timeout') || errorMessage.includes('time')) {
                errorMessage += '\n\nThe installation process may be taking longer than expected. Large packages like TorchAudio can take 10+ minutes to download and install.';
            }

            this.showError('Failed to start voice service: ' + errorMessage);
            await this.checkServiceStatus(); // Refresh status even on failure
        }
    }

    /**
     * Start polling for installation progress updates
     */
    startProgressPolling() {
        if (this.progressPollingInterval) {
            clearInterval(this.progressPollingInterval);
        }

        console.log('[VoiceAssistant] Starting progress polling');

        this.progressPollingInterval = setInterval(async () => {
            try {
                const progressResult = await this.makeAPICall('GetInstallationProgress', {});

                if (progressResult.success) {
                    this.updateRealTimeProgress(progressResult);

                    // Stop polling if complete or error
                    if (progressResult.is_complete || progressResult.has_error) {
                        this.stopProgressPolling();

                        if (progressResult.has_error) {
                            this.showError('Installation failed: ' + progressResult.error_message);
                            this.showInstallationProgress(false);
                        }
                    }
                } else {
                    // API call failed, stop polling
                    this.stopProgressPolling();
                }
            } catch (error) {
                console.warn('[VoiceAssistant] Progress polling error:', error);
                // Continue polling despite errors
            }
        }, 1000); // Poll every second
    }

    /**
     * Stop polling for installation progress updates
     */
    stopProgressPolling() {
        if (this.progressPollingInterval) {
            clearInterval(this.progressPollingInterval);
            this.progressPollingInterval = null;
            console.log('[VoiceAssistant] Stopped progress polling');
        }
    }

    /**
     * Update progress display with real-time data from backend
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

        console.log(`[VoiceAssistant] Progress update: ${progress}% - ${detailedMessage}`);
    }

    /**
     * Stop the voice service
     */
    async stopService() {
        console.log('[VoiceAssistant] Stopping voice service');

        try {
            this.updateStatusIcon('stopping');
            this.updateStatusText('Stopping service...');

            const result = await this.makeAPICall('StopVoiceService', {});

            if (result.success) {
                this.showSuccess('Voice service stopped successfully');
                this.updateSystemInfo('sttEngine', 'Not initialized');
                this.updateSystemInfo('ttsEngine', 'Not initialized');
                await this.checkServiceStatus(); // Refresh status
            } else {
                throw new Error(result.error || 'Failed to stop service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error stopping service:', error);
            this.showError('Failed to stop voice service: ' + error.message);
            await this.checkServiceStatus(); // Refresh status even on failure
        }
    }

    /**
     * Toggle voice recording
     */
    async toggleRecording() {
        if (this.isRecording) {
            await this.stopRecording();
        } else {
            await this.startRecording();
        }
    }

    /**
     * Start voice recording
     */
    async startRecording() {
        if (!this.serviceRunning || !this.serviceHealthy) {
            this.showError('Voice service is not available. Please start the service first.');
            return;
        }

        if (this.isProcessing) {
            this.showError('Please wait for current processing to complete.');
            return;
        }

        console.log('[VoiceAssistant] Starting voice recording');

        try {
            // Request microphone access
            const stream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    sampleRate: 16000,
                    channelCount: 1,
                    echoCancellation: true,
                    noiseSuppression: true
                }
            });

            // Set up MediaRecorder with WebM format (supported by most browsers)
            let mimeType = 'audio/webm;codecs=opus';
            if (!MediaRecorder.isTypeSupported(mimeType)) {
                mimeType = 'audio/webm';
                if (!MediaRecorder.isTypeSupported(mimeType)) {
                    mimeType = 'audio/wav';
                }
            }

            this.mediaRecorder = new MediaRecorder(stream, { mimeType });
            this.audioChunks = [];

            this.mediaRecorder.ondataavailable = (event) => {
                if (event.data.size > 0) {
                    this.audioChunks.push(event.data);
                }
            };

            this.mediaRecorder.onstop = async () => {
                console.log('[VoiceAssistant] Recording stopped, processing audio');
                const audioBlob = new Blob(this.audioChunks, { type: mimeType });
                await this.processAudio(audioBlob);

                // Stop all tracks to release microphone
                stream.getTracks().forEach(track => track.stop());
            };

            this.mediaRecorder.onerror = (event) => {
                console.error('[VoiceAssistant] MediaRecorder error:', event.error);
                this.showError('Recording error: ' + event.error.message);
                this.isRecording = false;
                this.updateUI();
            };

            // Start recording
            this.mediaRecorder.start();
            this.isRecording = true;
            this.updateUI();

            // Auto-stop after 30 seconds
            this.recordingTimeout = setTimeout(() => {
                if (this.isRecording) {
                    console.log('[VoiceAssistant] Auto-stopping recording after 30 seconds');
                    this.stopRecording();
                }
            }, 30000);

        } catch (error) {
            console.error('[VoiceAssistant] Error starting recording:', error);
            this.showError('Failed to start recording: ' + error.message);
            this.isRecording = false;
            this.updateUI();
        }
    }

    /**
     * Stop voice recording
     */
    async stopRecording() {
        if (!this.isRecording || !this.mediaRecorder) {
            return;
        }

        console.log('[VoiceAssistant] Stopping voice recording');

        try {
            if (this.recordingTimeout) {
                clearTimeout(this.recordingTimeout);
                this.recordingTimeout = null;
            }

            if (this.mediaRecorder.state !== 'inactive') {
                this.mediaRecorder.stop();
            }

            this.isRecording = false;
            this.updateUI();

        } catch (error) {
            console.error('[VoiceAssistant] Error stopping recording:', error);
            this.showError('Error stopping recording: ' + error.message);
        }
    }

    /**
     * Process recorded audio
     */
    async processAudio(audioBlob) {
        if (this.isProcessing) {
            return;
        }

        console.log('[VoiceAssistant] Processing audio blob:', audioBlob.size, 'bytes');

        try {
            this.isProcessing = true;
            this.updateUI();

            // Convert audio blob to base64
            const base64Audio = await this.blobToBase64(audioBlob);

            // Remove data URL prefix
            const audioData = base64Audio.split(',')[1];

            const payload = {
                session_id: this.sessionId,
                audio_data: audioData,
                language: this.elements.voiceLanguage?.value || 'en-US',
                voice: this.elements.ttsVoice?.value || 'default',
                volume: parseFloat(this.elements.voiceVolume?.value || '0.8')
            };

            console.log('[VoiceAssistant] Sending audio for processing');

            const result = await this.makeAPICall('ProcessVoiceInput', payload);

            if (result.success) {
                // Update transcript
                if (result.transcription) {
                    this.updateTranscript(result.transcription);
                }

                // Add to history
                this.addToHistory({
                    type: 'voice',
                    input: result.transcription || 'Audio processed',
                    response: result.ai_response || 'Command processed',
                    timestamp: new Date()
                });

                // Play TTS response if available
                if (result.audio_response) {
                    await this.playAudioResponse(result.audio_response);
                }

                this.showSuccess('Voice command processed successfully');
            } else {
                throw new Error(result.error || 'Failed to process voice input');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Error processing audio:', error);
            this.showError('Failed to process voice input: ' + error.message);
        } finally {
            this.isProcessing = false;
            this.updateUI();
        }
    }

    /**
     * Send text command
     */
    async sendTextCommand() {
        const textInput = this.elements.textCommand;
        if (!textInput || !textInput.value.trim()) {
            this.showError('Please enter a text command');
            return;
        }

        const text = textInput.value.trim();
        console.log('[VoiceAssistant] Sending text command:', text);

        try {
            this.isProcessing = true;
            this.updateUI();

            const payload = {
                session_id: this.sessionId,
                text: text,
                voice: this.elements.ttsVoice?.value || 'default',
                language: this.elements.voiceLanguage?.value || 'en-US',
                volume: parseFloat(this.elements.voiceVolume?.value || '0.8')
            };

            const result = await this.makeAPICall('ProcessTextCommand', payload);

            if (result.success) {
                // Clear input
                textInput.value = '';

                // Update transcript
                this.updateTranscript(text);

                // Add to history
                this.addToHistory({
                    type: 'text',
                    input: text,
                    response: result.text || 'Command processed',
                    timestamp: new Date()
                });

                // Play TTS response if available
                if (result.audio_response) {
                    await this.playAudioResponse(result.audio_response);
                }

                this.showSuccess('Text command processed successfully');
            } else {
                throw new Error(result.error || 'Failed to process text command');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Error processing text command:', error);
            this.showError('Failed to process text command: ' + error.message);
        } finally {
            this.isProcessing = false;
            this.updateUI();
        }
    }

    /**
     * Play TTS audio response
     */
    async playAudioResponse(base64Audio) {
        try {
            console.log('[VoiceAssistant] Playing TTS audio response');

            // Initialize audio context if needed
            if (!this.audioContext) {
                const AudioContext = window.AudioContext || window.webkitAudioContext;
                this.audioContext = new AudioContext();
            }

            // Decode base64 to array buffer
            const audioData = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));

            // Decode audio data
            const audioBuffer = await this.audioContext.decodeAudioData(audioData.buffer);

            // Create and play audio source
            const source = this.audioContext.createBufferSource();
            source.buffer = audioBuffer;
            source.connect(this.audioContext.destination);
            source.start(0);

            console.log('[VoiceAssistant] TTS audio playback started');

        } catch (error) {
            console.error('[VoiceAssistant] Error playing TTS audio:', error);
            // Don't show error to user for audio playback issues
        }
    }

    /**
     * Update the transcript display
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
     * Add item to command history
     */
    addToHistory(item) {
        this.commandHistory.unshift(item);

        // Limit history size
        if (this.commandHistory.length > this.maxHistoryItems) {
            this.commandHistory = this.commandHistory.slice(0, this.maxHistoryItems);
        }

        this.updateHistoryDisplay();
    }

    /**
     * Update command history display
     */
    updateHistoryDisplay() {
        const historyElement = this.elements.commandHistory;
        if (!historyElement) return;

        if (this.commandHistory.length === 0) {
            historyElement.innerHTML = `
                <div class="empty-state">
                    <i class="fas fa-history text-muted"></i>
                    <p class="text-muted mb-0">No commands yet</p>
                </div>
            `;
            return;
        }

        const historyHTML = this.commandHistory.map(item => `
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
     * Clear transcript
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
     * Clear command history
     */
    clearHistory() {
        this.commandHistory = [];
        this.updateHistoryDisplay();
    }

    /**
     * Check installation status of dependencies
     */
    async checkInstallationStatus() {
        console.log('[VoiceAssistant] Checking installation status');

        try {
            this.showInstallationDetails(true);
            this.updateInstallationInfo('Checking Python environment and dependencies...');

            const result = await this.makeAPICall('CheckInstallationStatus', {});

            if (result.success) {
                this.displayInstallationResults(result);
            } else {
                this.showError('Failed to check installation status: ' + result.error);
                this.showInstallationDetails(false);
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking installation status:', error);
            this.showError('Error checking installation status: ' + error.message);
            this.showInstallationDetails(false);
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
     * Show/hide installation details panel
     */
    showInstallationDetails(show) {
        if (this.elements.installationDetails) {
            this.elements.installationDetails.style.display = show ? 'block' : 'none';
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
     * Update installation status text
     */
    updateInstallationInfo(text) {
        if (this.elements.installationStatus) {
            this.elements.installationStatus.innerHTML = `<small class="text-muted">${text}</small>`;
        }
    }

    /**
     * Update UI based on current state
     */
    updateUI() {
        // Update service status
        if (this.serviceRunning && this.serviceHealthy) {
            this.updateStatusIcon('online');
            this.updateStatusText('Service online');
        } else if (this.serviceRunning && !this.serviceHealthy) {
            this.updateStatusIcon('warning');
            this.updateStatusText('Service starting...');
        } else {
            this.updateStatusIcon('offline');
            this.updateStatusText('Service offline');
        }

        // Update service control buttons
        if (this.elements.startService) {
            this.elements.startService.disabled = this.serviceRunning || this.isInstallingDependencies;
        }
        if (this.elements.stopService) {
            this.elements.stopService.disabled = !this.serviceRunning || this.isInstallingDependencies;
        }

        // Update recording button
        const recordButton = this.elements.toggleRecording;
        if (recordButton) {
            const canRecord = this.serviceRunning && this.serviceHealthy && !this.isProcessing && !this.isInstallingDependencies;
            recordButton.disabled = !canRecord;

            if (this.isRecording) {
                recordButton.className = 'btn btn-danger btn-lg voice-record-btn recording';
                recordButton.innerHTML = '<i class="fas fa-stop"></i><span class="btn-text">Stop Recording</span>';
            } else if (this.isProcessing) {
                recordButton.className = 'btn btn-warning btn-lg voice-record-btn processing';
                recordButton.innerHTML = '<i class="fas fa-cog fa-spin"></i><span class="btn-text">Processing...</span>';
            } else {
                recordButton.className = 'btn btn-primary btn-lg voice-record-btn';
                recordButton.innerHTML = '<i class="fas fa-microphone"></i><span class="btn-text">Start Listening</span>';
            }
        }

        // Update recording status
        const statusElement = this.elements.recordingStatus;
        if (statusElement) {
            if (this.isRecording) {
                statusElement.className = 'recording-status listening';
                statusElement.innerHTML = '<span class="status-text">Listening...</span>';
            } else if (this.isProcessing) {
                statusElement.className = 'recording-status processing';
                statusElement.innerHTML = '<span class="status-text">Processing...</span>';
            } else if (this.isInstallingDependencies) {
                statusElement.className = 'recording-status processing';
                statusElement.innerHTML = '<span class="status-text">Installing dependencies...</span>';
            } else if (this.serviceRunning && this.serviceHealthy) {
                statusElement.className = 'recording-status ready';
                statusElement.innerHTML = '<span class="status-text">Ready to listen</span>';
            } else {
                statusElement.className = 'recording-status';
                statusElement.innerHTML = '<span class="status-text">Service not ready</span>';
            }
        }

        // Update text command button
        if (this.elements.sendTextCommand) {
            this.elements.sendTextCommand.disabled = this.isProcessing || this.isInstallingDependencies;
        }
    }

    /**
     * Update status icon
     */
    updateStatusIcon(status) {
        const iconElement = this.elements.statusIcon;
        if (!iconElement) return;

        const iconMap = {
            'online': '<i class="fas fa-circle text-success"></i>',
            'offline': '<i class="fas fa-circle text-secondary"></i>',
            'warning': '<i class="fas fa-circle text-warning"></i>',
            'error': '<i class="fas fa-circle text-danger"></i>',
            'checking': '<i class="fas fa-circle text-info spinning"></i>',
            'starting': '<i class="fas fa-circle text-info spinning"></i>',
            'stopping': '<i class="fas fa-circle text-warning spinning"></i>'
        };

        iconElement.innerHTML = iconMap[status] || iconMap['offline'];
    }

    /**
     * Update status text
     */
    updateStatusText(text) {
        if (this.elements.statusText) {
            this.elements.statusText.textContent = text;
        }
    }

    /**
     * Show error message
     */
    showError(message) {
        console.error('[VoiceAssistant] Error:', message);
        if (this.elements.errorAlert && this.elements.errorMessage) {
            this.elements.errorMessage.textContent = message;
            this.elements.errorAlert.style.display = 'block';

            // Auto-hide after 8 seconds for errors
            setTimeout(() => this.hideError(), 8000);
        }
    }

    /**
     * Show success message
     */
    showSuccess(message) {
        console.log('[VoiceAssistant] Success:', message);
        if (this.elements.successAlert && this.elements.successMessage) {
            this.elements.successMessage.textContent = message;
            this.elements.successAlert.style.display = 'block';

            // Auto-hide after 4 seconds
            setTimeout(() => this.hideSuccess(), 4000);
        }
    }

    /**
     * Show warning message
     */
    showWarning(message) {
        console.warn('[VoiceAssistant] Warning:', message);
        if (this.elements.warningAlert && this.elements.warningMessage) {
            this.elements.warningMessage.textContent = message;
            this.elements.warningAlert.style.display = 'block';

            // Auto-hide after 6 seconds
            setTimeout(() => this.hideWarning(), 6000);
        }
    }

    /**
     * Hide error message
     */
    hideError() {
        if (this.elements.errorAlert) {
            this.elements.errorAlert.style.display = 'none';
        }
    }

    /**
     * Hide success message
     */
    hideSuccess() {
        if (this.elements.successAlert) {
            this.elements.successAlert.style.display = 'none';
        }
    }

    /**
     * Hide warning message
     */
    hideWarning() {
        if (this.elements.warningAlert) {
            this.elements.warningAlert.style.display = 'none';
        }
    }

    /**
     * Convert blob to base64
     */
    blobToBase64(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result);
            reader.onerror = reject;
            reader.readAsDataURL(blob);
        });
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
     * Make API call using SwarmUI's genericRequest
     */
    makeAPICall(methodName, payload) {
        return new Promise((resolve, reject) => {
            console.log(`[VoiceAssistant] Making API call: ${methodName}`, payload);

            try {
                genericRequest(methodName, payload, (data) => {
                    console.log(`[VoiceAssistant] API response for ${methodName}:`, data);
                    resolve(data);
                });
            } catch (error) {
                console.error(`[VoiceAssistant] API request error for ${methodName}:`, error);
                reject(error);
            }
        });
    }
}

// Global functions for alert close buttons
function hideError() {
    if (window.swarmVoiceAssistant) {
        window.swarmVoiceAssistant.hideError();
    }
}

function hideSuccess() {
    if (window.swarmVoiceAssistant) {
        window.swarmVoiceAssistant.hideSuccess();
    }
}

function hideWarning() {
    if (window.swarmVoiceAssistant) {
        window.swarmVoiceAssistant.hideWarning();
    }
}

// Initialize when script loads
(() => {
    console.log('[VoiceAssistant] Voice Assistant script loaded - Production v1.0 with Progress Tracking');
    window.swarmVoiceAssistant = new SwarmVoiceAssistant();
})();
