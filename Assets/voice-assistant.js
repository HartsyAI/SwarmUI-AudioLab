/**
 * Voice Assistant Frontend for SwarmUI
 * Provides voice-controlled image generation and UI interaction
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
        console.log('[VoiceAssistant] Initializing SwarmUI Voice Assistant');

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
            'installationProgress', 'progressBar', 'installationStatus', 'installationDetails', 'installationInfo'
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

        return hasMediaDevices && hasMediaRecorder && hasAudioContext;
    }

    /**
     * Check the status of the voice service
     */
    async checkServiceStatus() {
        console.log('[VoiceAssistant] Checking service status');

        try {
            this.updateStatusIcon('checking');

            const result = await this.makeAPICall('GetVoiceStatus', {});

            if (result.success) {
                this.serviceRunning = result.backend_running || false;
                this.serviceHealthy = result.backend_healthy || false;

                console.log('[VoiceAssistant] Service status:', {
                    running: this.serviceRunning,
                    healthy: this.serviceHealthy
                });

                this.updateUI();
            } else {
                throw new Error(result.error || 'Failed to get service status');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking service status:', error);
            this.serviceRunning = false;
            this.serviceHealthy = false;
            this.updateStatusIcon('error');
            this.updateStatusText('Service unavailable');
            this.showError('Failed to check service status: ' + error.message);
        }
    }

    /**
     * Start the voice service
     */
    async startService() {
        console.log('[VoiceAssistant] Starting voice service');

        try {
            this.updateStatusIcon('starting');
            this.updateStatusText('Starting service...');
            this.updateUI();

            // Step 1: Check installation status
            this.showSuccess('Checking installation status...');
            this.showInstallationProgress(true, 10, 'Checking Python environment...');

            const installStatus = await this.makeAPICall('CheckInstallationStatus', {});

            if (installStatus.success) {
                this.updateInstallationProgress(20, 'Python environment detected');

                if (!installStatus.dependencies_installed) {
                    // Show installation progress
                    this.showSuccess('Installing dependencies automatically... This may take several minutes on first run.');
                    this.updateInstallationProgress(30, 'Installing core packages (FastAPI, NumPy, etc.)...');

                    console.log('[VoiceAssistant] Dependencies need installation:', installStatus.installation_details);

                    // Simulate progress during installation
                    this.simulateInstallationProgress();
                } else {
                    this.updateInstallationProgress(80, 'Dependencies already installed');
                }
            } else {
                this.updateInstallationProgress(15, 'Warning: Could not fully check installation status');
            }

            // Step 2: Start the service (this will install dependencies if needed)
            this.updateInstallationProgress(90, 'Starting voice service backend...');
            const result = await this.makeAPICall('StartVoiceService', {});

            if (result.success) {
                this.updateInstallationProgress(100, 'Voice service started successfully!');

                // Hide progress after a delay
                setTimeout(() => {
                    this.showInstallationProgress(false);
                }, 2000);

                if (result.first_time_setup) {
                    this.showSuccess('Voice service started successfully! Dependencies were installed automatically.');
                } else {
                    this.showSuccess('Voice service started successfully');
                }
                await this.checkServiceStatus(); // Refresh status
            } else {
                this.showInstallationProgress(false);
                throw new Error(result.error || 'Failed to start service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error starting service:', error);
            this.showInstallationProgress(false);

            // Provide helpful error messages based on common issues
            let errorMessage = error.message;
            if (errorMessage.includes('dependencies')) {
                errorMessage += '\n\nThis appears to be a dependency issue. The system will automatically install required packages when you start the service. This process may take 5-10 minutes on first run.';
            } else if (errorMessage.includes('Python')) {
                errorMessage += '\n\nThis appears to be a Python environment issue. Please ensure SwarmUI is properly installed with its Python backend.';
            } else if (errorMessage.includes('timeout') || errorMessage.includes('time')) {
                errorMessage += '\n\nThe installation process may be taking longer than expected. Please wait a few more minutes and try again.';
            }

            this.showError('Failed to start voice service: ' + errorMessage);
            await this.checkServiceStatus(); // Refresh status even on failure
        }
    }

    /**
     * Simulate installation progress for better user feedback
     */
    simulateInstallationProgress() {
        const steps = [
            { progress: 35, text: 'Installing FastAPI and web server components...' },
            { progress: 45, text: 'Installing NumPy and scientific computing libraries...' },
            { progress: 55, text: 'Installing Speech-to-Text library (RealtimeSTT)...' },
            { progress: 70, text: 'Installing Text-to-Speech library (ChatterboxTTS)...' },
            { progress: 80, text: 'Finalizing installation and testing imports...' },
        ];

        let currentStep = 0;
        const stepInterval = setInterval(() => {
            if (currentStep < steps.length) {
                const step = steps[currentStep];
                this.updateInstallationProgress(step.progress, step.text);
                currentStep++;
            } else {
                clearInterval(stepInterval);
            }
        }, 3000); // Update every 3 seconds

        // Clear the interval after 20 seconds max
        setTimeout(() => clearInterval(stepInterval), 20000);
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

            // Set up MediaRecorder
            this.mediaRecorder = new MediaRecorder(stream, {
                mimeType: 'audio/webm;codecs=opus'
            });

            this.audioChunks = [];

            this.mediaRecorder.ondataavailable = (event) => {
                if (event.data.size > 0) {
                    this.audioChunks.push(event.data);
                }
            };

            this.mediaRecorder.onstop = async () => {
                console.log('[VoiceAssistant] Recording stopped, processing audio');
                const audioBlob = new Blob(this.audioChunks, { type: 'audio/webm' });
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
                    Click "Start Service" to automatically install them.
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
            this.elements.startService.disabled = this.serviceRunning;
        }
        if (this.elements.stopService) {
            this.elements.stopService.disabled = !this.serviceRunning;
        }

        // Update recording button
        const recordButton = this.elements.toggleRecording;
        if (recordButton) {
            const canRecord = this.serviceRunning && this.serviceHealthy && !this.isProcessing;
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
            this.elements.sendTextCommand.disabled = this.isProcessing;
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

            // Auto-hide after 5 seconds
            setTimeout(() => this.hideError(), 5000);
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

            // Auto-hide after 3 seconds
            setTimeout(() => this.hideSuccess(), 3000);
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

// Initialize when script loads
(() => {
    console.log('[VoiceAssistant] Voice Assistant script loaded');
    window.swarmVoiceAssistant = new SwarmVoiceAssistant();
})();
