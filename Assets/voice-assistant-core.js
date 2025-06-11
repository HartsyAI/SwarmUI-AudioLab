/**
 * Voice Assistant Core - Main Coordination Module
 * Handles application state, voice recording, audio processing, and service coordination.
 * Acts as the central hub between UI and API components.
 */

class VoiceAssistantCore {
    constructor() {
        // Application state
        this.state = {
            isRecording: false,
            isProcessing: false,
            serviceRunning: false,
            serviceHealthy: false,
            isInstallingDependencies: false
        };

        // Audio context and recording
        this.mediaRecorder = null;
        this.audioChunks = [];
        this.audioContext = null;
        this.recordingTimeout = null;

        // Session management
        this.sessionId = this.generateSessionId();

        // Command history
        this.commandHistory = [];
        this.maxHistoryItems = 20;

        // Configuration
        this.config = {
            maxRecordingDuration: 30000, // 30 seconds
            maxAudioSize: 50 * 1024 * 1024, // 50MB
            autoStopTimeout: 30000,
            healthCheckInterval: 30000
        };

        // Dependencies
        this.api = null;
        this.ui = null;

        console.log('[VoiceAssistant] Core module initialized');
    }

    /**
     * Initialize the voice assistant with dependencies
     */
    async initialize(apiModule, uiModule) {
        try {
            console.log('[VoiceAssistant] Initializing Voice Assistant Core v1.0');

            // Store dependencies
            this.api = apiModule;
            this.ui = uiModule;

            // Set up cross-module communication
            this.setupEventHandlers();

            // Check browser compatibility
            if (!this.checkBrowserCompatibility()) {
                this.ui.showError('Your browser does not support voice recording. Please use a modern browser.');
                return false;
            }

            // Initial service status check
            await this.checkServiceStatus();

            // Start health monitoring
            this.startHealthMonitoring();

            console.log('[VoiceAssistant] Core initialization complete');
            return true;
        } catch (error) {
            console.error('[VoiceAssistant] Core initialization failed:', error);
            this.ui.showError('Failed to initialize voice assistant: ' + error.message);
            return false;
        }
    }

    /**
     * Generate a unique session identifier
     */
    generateSessionId() {
        return 'voice-session-' + Date.now() + '-' + Math.random().toString(36).substr(2, 9);
    }

    /**
     * Set up event handlers for UI interactions
     */
    setupEventHandlers() {
        // Service control handlers
        this.ui.onRefreshStatus(() => this.checkServiceStatus());
        this.ui.onStartService(() => this.startService());
        this.ui.onStopService(() => this.stopService());
        this.ui.onCheckInstallation(() => this.checkInstallationStatus());

        // Voice control handlers
        this.ui.onToggleRecording(() => this.toggleRecording());
        this.ui.onSendTextCommand((text) => this.sendTextCommand(text));

        // Settings handlers
        this.ui.onVolumeChange((volume) => this.handleVolumeChange(volume));

        // History management handlers
        this.ui.onClearTranscript(() => this.clearTranscript());
        this.ui.onClearHistory(() => this.clearHistory());

        console.log('[VoiceAssistant] Event handlers set up');
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
            this.ui.showWarning('Microphone access not available. Please use HTTPS or localhost.');
        }

        return hasMediaDevices && hasMediaRecorder && hasAudioContext;
    }

    /**
     * Check the status of the voice service
     */
    async checkServiceStatus() {
        console.log('[VoiceAssistant] Checking service status');

        try {
            this.ui.updateServiceStatus('checking', 'Checking service...');

            const result = await this.api.getVoiceStatus();

            if (result.success) {
                this.updateServiceState(result);
                this.ui.updateServiceStatus(
                    this.getServiceStatusIcon(),
                    this.getServiceStatusText()
                );
            } else {
                throw new Error(result.error || 'Failed to get service status');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking service status:', error);
            this.updateServiceState({
                backend_running: false,
                backend_healthy: false
            });
            this.ui.updateServiceStatus('error', 'Service unavailable');
            this.ui.showError('Failed to check service status: ' + error.message);
        }
    }

    /**
     * Start the voice service with progress tracking
     */
    async startService() {
        console.log('[VoiceAssistant] Starting voice service');

        try {
            this.ui.updateServiceStatus('starting', 'Starting service...');
            this.state.isInstallingDependencies = true;
            this.updateUI();

            // Show initial progress
            this.ui.showInstallationProgress(true, 5, 'Checking Python environment...');

            // Start the service
            const result = await this.api.startVoiceService();

            if (result.success) {
                // Start polling for progress if dependencies are being installed
                this.startProgressPolling();

                this.ui.showSuccess('Voice service started successfully');
                await this.checkServiceStatus(); // Refresh status
            } else {
                this.stopProgressPolling();
                this.ui.hideInstallationProgress();
                this.state.isInstallingDependencies = false;
                throw new Error(result.error || 'Failed to start service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error starting service:', error);
            this.stopProgressPolling();
            this.ui.hideInstallationProgress();
            this.state.isInstallingDependencies = false;

            let errorMessage = error.message;
            if (errorMessage.includes('dependencies')) {
                errorMessage += '\n\nThis appears to be a dependency issue. The system will automatically install required packages when you start the service.';
            }

            this.ui.showError('Failed to start voice service: ' + errorMessage);
            await this.checkServiceStatus();
        }
    }

    /**
     * Stop the voice service
     */
    async stopService() {
        console.log('[VoiceAssistant] Stopping voice service');

        try {
            this.ui.updateServiceStatus('stopping', 'Stopping service...');

            const result = await this.api.stopVoiceService();

            if (result.success) {
                this.ui.showSuccess('Voice service stopped successfully');
                await this.checkServiceStatus();
            } else {
                throw new Error(result.error || 'Failed to stop service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error stopping service:', error);
            this.ui.showError('Failed to stop voice service: ' + error.message);
            await this.checkServiceStatus();
        }
    }

    /**
     * Toggle voice recording
     */
    async toggleRecording() {
        if (this.state.isRecording) {
            await this.stopRecording();
        } else {
            await this.startRecording();
        }
    }

    /**
     * Start voice recording
     */
    async startRecording() {
        if (!this.state.serviceRunning || !this.state.serviceHealthy) {
            this.ui.showError('Voice service is not available. Please start the service first.');
            return;
        }

        if (this.state.isProcessing) {
            this.ui.showError('Please wait for current processing to complete.');
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

            // Set up MediaRecorder
            let mimeType = this.getBestMimeType();
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
                this.ui.showError('Recording error: ' + event.error.message);
                this.state.isRecording = false;
                this.updateUI();
            };

            // Start recording
            this.mediaRecorder.start();
            this.state.isRecording = true;
            this.updateUI();

            // Auto-stop after configured duration
            this.recordingTimeout = setTimeout(() => {
                if (this.state.isRecording) {
                    console.log('[VoiceAssistant] Auto-stopping recording after timeout');
                    this.stopRecording();
                }
            }, this.config.maxRecordingDuration);

        } catch (error) {
            console.error('[VoiceAssistant] Error starting recording:', error);
            this.ui.showError('Failed to start recording: ' + error.message);
            this.state.isRecording = false;
            this.updateUI();
        }
    }

    /**
     * Stop voice recording
     */
    async stopRecording() {
        if (!this.state.isRecording || !this.mediaRecorder) {
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

            this.state.isRecording = false;
            this.updateUI();

        } catch (error) {
            console.error('[VoiceAssistant] Error stopping recording:', error);
            this.ui.showError('Error stopping recording: ' + error.message);
        }
    }

    /**
     * Process recorded audio through the voice pipeline
     */
    async processAudio(audioBlob) {
        if (this.state.isProcessing) {
            return;
        }

        console.log('[VoiceAssistant] Processing audio blob:', audioBlob.size, 'bytes');

        try {
            this.state.isProcessing = true;
            this.updateUI();

            // Validate audio size
            if (audioBlob.size > this.config.maxAudioSize) {
                throw new Error('Audio file too large');
            }

            // Convert audio blob to base64
            const base64Audio = await this.blobToBase64(audioBlob);
            const audioData = base64Audio.split(',')[1];

            // Get current settings
            const settings = this.ui.getCurrentSettings();

            const payload = {
                session_id: this.sessionId,
                audio_data: audioData,
                language: settings.language,
                voice: settings.voice,
                volume: settings.volume
            };

            console.log('[VoiceAssistant] Sending audio for processing');

            const result = await this.api.processVoiceInput(payload);

            if (result.success) {
                // Update transcript
                if (result.transcription) {
                    this.ui.updateTranscript(result.transcription);
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

                this.ui.showSuccess('Voice command processed successfully');
            } else {
                throw new Error(result.error || 'Failed to process voice input');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Error processing audio:', error);
            this.ui.showError('Failed to process voice input: ' + error.message);
        } finally {
            this.state.isProcessing = false;
            this.updateUI();
        }
    }

    /**
     * Send text command
     */
    async sendTextCommand(text) {
        if (!text || !text.trim()) {
            this.ui.showError('Please enter a text command');
            return;
        }

        console.log('[VoiceAssistant] Sending text command:', text);

        try {
            this.state.isProcessing = true;
            this.updateUI();

            // Get current settings
            const settings = this.ui.getCurrentSettings();

            const payload = {
                session_id: this.sessionId,
                text: text.trim(),
                voice: settings.voice,
                language: settings.language,
                volume: settings.volume
            };

            const result = await this.api.processTextCommand(payload);

            if (result.success) {
                // Clear input
                this.ui.clearTextInput();

                // Update transcript
                this.ui.updateTranscript(text);

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

                this.ui.showSuccess('Text command processed successfully');
            } else {
                throw new Error(result.error || 'Failed to process text command');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Error processing text command:', error);
            this.ui.showError('Failed to process text command: ' + error.message);
        } finally {
            this.state.isProcessing = false;
            this.updateUI();
        }
    }

    /**
     * Check installation status
     */
    async checkInstallationStatus() {
        console.log('[VoiceAssistant] Checking installation status');

        try {
            this.ui.showInstallationDetails(true);
            this.ui.updateInstallationInfo('Checking Python environment and dependencies...');

            const result = await this.api.checkInstallationStatus();

            if (result.success) {
                this.ui.displayInstallationResults(result);
            } else {
                this.ui.showError('Failed to check installation status: ' + result.error);
                this.ui.showInstallationDetails(false);
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking installation status:', error);
            this.ui.showError('Error checking installation status: ' + error.message);
            this.ui.showInstallationDetails(false);
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
     * Add item to command history
     */
    addToHistory(item) {
        this.commandHistory.unshift(item);

        // Limit history size
        if (this.commandHistory.length > this.maxHistoryItems) {
            this.commandHistory = this.commandHistory.slice(0, this.maxHistoryItems);
        }

        this.ui.updateHistoryDisplay(this.commandHistory);
    }

    /**
     * Clear transcript
     */
    clearTranscript() {
        this.ui.clearTranscript();
    }

    /**
     * Clear command history
     */
    clearHistory() {
        this.commandHistory = [];
        this.ui.updateHistoryDisplay(this.commandHistory);
    }

    /**
     * Handle volume change
     */
    handleVolumeChange(volume) {
        console.log('[VoiceAssistant] Volume changed to:', volume);
        // Volume is handled per-request, no persistent state needed
    }

    /**
     * Start progress polling for installation
     */
    startProgressPolling() {
        if (this.progressPollingInterval) {
            clearInterval(this.progressPollingInterval);
        }

        console.log('[VoiceAssistant] Starting progress polling');

        this.progressPollingInterval = setInterval(async () => {
            try {
                const progressResult = await this.api.getInstallationProgress();

                if (progressResult.success) {
                    this.ui.updateRealTimeProgress(progressResult);

                    // Stop polling if complete or error
                    if (progressResult.is_complete || progressResult.has_error) {
                        this.stopProgressPolling();

                        if (progressResult.has_error) {
                            this.ui.showError('Installation failed: ' + progressResult.error_message);
                            this.ui.hideInstallationProgress();
                        } else {
                            // Hide progress after delay
                            setTimeout(() => {
                                this.ui.hideInstallationProgress();
                                this.state.isInstallingDependencies = false;
                                this.updateUI();
                            }, 3000);
                        }
                    }
                } else {
                    this.stopProgressPolling();
                }
            } catch (error) {
                console.warn('[VoiceAssistant] Progress polling error:', error);
                // Continue polling despite errors
            }
        }, 1000); // Poll every second
    }

    /**
     * Stop progress polling
     */
    stopProgressPolling() {
        if (this.progressPollingInterval) {
            clearInterval(this.progressPollingInterval);
            this.progressPollingInterval = null;
            console.log('[VoiceAssistant] Stopped progress polling');
        }
    }

    /**
     * Start health monitoring
     */
    startHealthMonitoring() {
        if (this.healthMonitoringInterval) {
            clearInterval(this.healthMonitoringInterval);
        }

        this.healthMonitoringInterval = setInterval(async () => {
            try {
                await this.checkServiceStatus();
            } catch (error) {
                console.debug('[VoiceAssistant] Health monitoring error:', error);
                // Don't show errors for automatic health checks
            }
        }, this.config.healthCheckInterval);

        console.log('[VoiceAssistant] Health monitoring started');
    }

    /**
     * Update service state from API response
     */
    updateServiceState(result) {
        this.state.serviceRunning = result.backend_running || false;
        this.state.serviceHealthy = result.backend_healthy || false;

        console.log('[VoiceAssistant] Service state updated:', {
            running: this.state.serviceRunning,
            healthy: this.state.serviceHealthy
        });
    }

    /**
     * Get service status icon
     */
    getServiceStatusIcon() {
        if (this.state.serviceRunning && this.state.serviceHealthy) {
            return 'online';
        } else if (this.state.serviceRunning && !this.state.serviceHealthy) {
            return 'warning';
        } else {
            return 'offline';
        }
    }

    /**
     * Get service status text
     */
    getServiceStatusText() {
        if (this.state.serviceRunning && this.state.serviceHealthy) {
            return 'Service online';
        } else if (this.state.serviceRunning && !this.state.serviceHealthy) {
            return 'Service starting...';
        } else {
            return 'Service offline';
        }
    }

    /**
     * Update UI based on current state
     */
    updateUI() {
        this.ui.updateState(this.state);
    }

    /**
     * Get the best MIME type for recording
     */
    getBestMimeType() {
        const types = [
            'audio/webm;codecs=opus',
            'audio/webm',
            'audio/wav'
        ];

        for (const type of types) {
            if (MediaRecorder.isTypeSupported(type)) {
                return type;
            }
        }

        return 'audio/wav'; // fallback
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
     * Clean up resources
     */
    destroy() {
        try {
            // Stop any ongoing recording
            if (this.state.isRecording) {
                this.stopRecording();
            }

            // Clear intervals
            if (this.progressPollingInterval) {
                clearInterval(this.progressPollingInterval);
            }

            if (this.healthMonitoringInterval) {
                clearInterval(this.healthMonitoringInterval);
            }

            if (this.recordingTimeout) {
                clearTimeout(this.recordingTimeout);
            }

            // Clean up audio context
            if (this.audioContext) {
                this.audioContext.close();
            }

            console.log('[VoiceAssistant] Core module destroyed');
        } catch (error) {
            console.error('[VoiceAssistant] Error during cleanup:', error);
        }
    }
}

// Export for global access
window.VoiceAssistantCore = VoiceAssistantCore;
