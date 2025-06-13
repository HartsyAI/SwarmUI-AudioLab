/**
 * Voice Assistant Core - Refactored for Generic Endpoints
 * Handles application state, voice recording, audio processing, and service coordination.
 * Acts as the central hub between UI and API components with support for multiple modes.
 */

class VoiceAssistantCore {
    constructor() {
        // Application state
        this.state = {
            // Service state
            serviceRunning: false,
            serviceHealthy: false,
            isInstalling: false,
            dependenciesInstalled: false,

            // Mode state
            currentMode: 'stt',

            // Activity state
            isRecording: false,
            isProcessing: false,
            isSpeaking: false,

            // Browser capabilities
            hasMediaDevices: false,
            hasMediaRecorder: false,
            hasAudioContext: false,
            canRecord: false
        };

        // Audio context and recording
        this.mediaRecorder = null;
        this.audioChunks = [];
        this.audioContext = null;
        this.recordingTimeout = null;
        this.currentAudio = null;

        // Session management
        this.sessionId = this.generateSessionId();

        // Dependencies tracking
        this.dependencies = [];
        this.dependencyCheckInterval = null;

        // Configuration
        this.config = {
            maxRecordingDuration: 30000, // 30 seconds
            maxAudioSize: 50 * 1024 * 1024, // 50MB
            autoStopTimeout: 30000,
            healthCheckInterval: 30000,
            dependencyCheckInterval: 10000,
            progressPollingInterval: 1000
        };

        // Module dependencies
        this.api = null;
        this.ui = null;

        // Progress tracking
        this.progressPollingTimer = null;
        this.healthCheckTimer = null;

        console.log('[VoiceAssistant] Core module initialized with generic endpoint support');
    }

    /**
     * Initialize the voice assistant with dependencies
     */
    async initialize(apiModule, uiModule) {
        try {
            console.log('[VoiceAssistant] Initializing Voice Assistant Core v2.0 with generic endpoints');

            // Store dependencies
            this.api = apiModule;
            this.ui = uiModule;

            // Set up cross-module communication
            this.setupEventHandlers();

            // Check browser compatibility (but don't fail if recording not available)
            this.checkBrowserCompatibility();

            // Initial service status check
            await this.checkServiceStatus();

            // Check dependencies status
            await this.checkDependenciesStatus();

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
        this.ui.onRefreshServiceStatus(() => this.checkServiceStatus());
        this.ui.onStartService(() => this.startService());
        this.ui.onStopService(() => this.stopService());
        this.ui.onConfirmInstallation(() => this.startInstallation());

        // Mode switching
        this.ui.onSwitchMode((mode) => this.switchMode(mode));

        // STT mode handlers
        this.ui.onSTTStartRecording(() => this.startSTTRecording());
        this.ui.onSTTStopRecording(() => this.stopSTTRecording());

        // TTS mode handlers
        this.ui.onTTSSpeak((text) => this.speakText(text));
        this.ui.onTTSStop(() => this.stopSpeaking());

        // STS mode handlers
        this.ui.onSTSStartConversation(() => this.startSTSConversation());
        this.ui.onSTSStopConversation(() => this.stopSTSConversation());

        // Utility handlers
        this.ui.onClearConsole(() => this.clearConsole());
        this.ui.onClearHistory(() => this.clearHistory());

        console.log('[VoiceAssistant] Event handlers set up');
    }

    /**
     * Check browser compatibility for voice features
     * Now returns boolean but doesn't fail initialization completely
     */
    checkBrowserCompatibility() {
        const hasMediaDevices = !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        const hasMediaRecorder = !!(window.MediaRecorder);
        const hasAudioContext = !!(window.AudioContext || window.webkitAudioContext);

        // Update state
        this.state.hasMediaDevices = hasMediaDevices;
        this.state.hasMediaRecorder = hasMediaRecorder;
        this.state.hasAudioContext = hasAudioContext;
        this.state.canRecord = hasMediaDevices && hasMediaRecorder && hasAudioContext;

        console.log('[VoiceAssistant] Browser compatibility:', {
            mediaDevices: hasMediaDevices,
            mediaRecorder: hasMediaRecorder,
            audioContext: hasAudioContext,
            canRecord: this.state.canRecord
        });

        if (!hasMediaDevices) {
            this.ui.showWarning('Microphone access not available. Voice recording features will be disabled. Please use HTTPS or localhost for full functionality.');
            this.ui.addConsoleMessage('warning', 'Microphone access not available - voice recording disabled');
        } else if (!this.state.canRecord) {
            this.ui.showWarning('Some voice features may not work properly in this browser.');
            this.ui.addConsoleMessage('warning', 'Limited voice support detected');
        } else {
            this.ui.addConsoleMessage('success', 'Full voice support available');
        }

        // Always return true to allow service management even without recording
        return true;
    }

    /**
     * Switch between modes (STT/TTS/STS/Commands)
     */
    switchMode(mode) {
        // Stop any current activity when switching modes
        this.stopAllActivity();

        this.state.currentMode = mode;
        this.ui.addConsoleMessage('info', `Switched to ${mode.toUpperCase()} mode`);

        // Update UI state
        this.updateUI();
    }

    /**
     * Stop all current audio activity
     */
    stopAllActivity() {
        // Stop recording if active
        if (this.state.isRecording) {
            this.stopRecording();
        }

        // Stop speaking if active
        if (this.state.isSpeaking) {
            this.stopSpeaking();
        }

        // Reset processing state
        this.state.isProcessing = false;
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
                    this.getServiceStatusText(),
                    this.state.serviceHealthy,
                    this.state.serviceRunning
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
            this.ui.addConsoleMessage('error', 'Failed to check service status: ' + error.message);
        }

        this.updateUI();
    }

    /**
     * Check dependencies installation status
     */
    async checkDependenciesStatus() {
        console.log('[VoiceAssistant] Checking dependencies status');

        try {
            const result = await this.api.checkInstallationStatus();

            if (result.success) {
                this.state.dependenciesInstalled = result.dependencies_installed || false;
                this.parseDependenciesFromResult(result);
                this.ui.updateDependenciesDisplay(this.dependencies);

                // Update primary button state
                this.ui.updatePrimaryServiceButton(
                    this.state.serviceHealthy,
                    this.state.serviceRunning,
                    this.state.dependenciesInstalled
                );
            } else {
                this.ui.addConsoleMessage('warning', 'Could not check dependencies: ' + result.error);
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error checking dependencies:', error);
            this.ui.addConsoleMessage('error', 'Error checking dependencies: ' + error.message);
        }
    }

    /**
     * Parse dependencies from installation status result
     */
    parseDependenciesFromResult(result) {
        this.dependencies = [];

        if (result.installation_details) {
            const details = result.installation_details;

            // Core packages
            if (details.core_packages) {
                Object.entries(details.core_packages).forEach(([name, installed]) => {
                    this.dependencies.push({
                        name: name,
                        category: 'core',
                        status: installed ? 'installed' : 'missing'
                    });
                });
            }

            // STT packages
            if (details.stt_packages) {
                Object.entries(details.stt_packages).forEach(([name, installed]) => {
                    this.dependencies.push({
                        name: name,
                        category: 'ai',
                        status: installed ? 'installed' : 'missing'
                    });
                });
            }

            // TTS packages
            if (details.tts_packages) {
                Object.entries(details.tts_packages).forEach(([name, installed]) => {
                    this.dependencies.push({
                        name: name,
                        category: 'ai',
                        status: installed ? 'installed' : 'missing'
                    });
                });
            }
        }

        // Add default dependencies if none found
        if (this.dependencies.length === 0) {
            this.dependencies = [
                { name: 'FastAPI', category: 'core', status: 'missing' },
                { name: 'NumPy', category: 'core', status: 'missing' },
                { name: 'PyTorch', category: 'core', status: 'missing' },
                { name: 'RealtimeSTT', category: 'ai', status: 'missing' },
                { name: 'Chatterbox TTS', category: 'ai', status: 'missing' }
            ];
        }
    }

    /**
     * Start the voice service or show installation modal
     */
    async startService() {
        if (!this.state.dependenciesInstalled) {
            this.ui.showInstallationModal();
            return;
        }

        console.log('[VoiceAssistant] Starting voice service');

        try {
            this.ui.updateServiceStatus('starting', 'Starting service...');
            this.ui.addConsoleMessage('info', 'Starting voice service...');

            const result = await this.api.startVoiceService();

            if (result.success) {
                this.ui.showSuccess('Voice service started successfully');
                this.ui.addConsoleMessage('success', 'Voice service started');
                await this.checkServiceStatus();
            } else {
                throw new Error(result.error || 'Failed to start service');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error starting service:', error);
            this.ui.showError('Failed to start voice service: ' + error.message);
            await this.checkServiceStatus();
        }
    }

    /**
     * Stop the voice service
     */
    async stopService() {
        console.log('[VoiceAssistant] Stopping voice service');

        try {
            // Stop any current activity
            this.stopAllActivity();

            this.ui.updateServiceStatus('stopping', 'Stopping service...');
            this.ui.addConsoleMessage('info', 'Stopping voice service...');

            const result = await this.api.stopVoiceService();

            if (result.success) {
                this.ui.showSuccess('Voice service stopped successfully');
                this.ui.addConsoleMessage('success', 'Voice service stopped');
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
     * Start installation process
     */
    async startInstallation() {
        console.log('[VoiceAssistant] Starting dependency installation');

        try {
            this.state.isInstalling = true;
            this.updateUI();

            // Show progress modal
            this.ui.showInstallationProgressModal();
            this.ui.updateInstallationProgress(0, 'Starting installation...', '', '', 'Beginning dependency installation...\n');

            this.ui.addConsoleMessage('info', 'Starting dependency installation');

            // Start the installation
            const result = await this.api.startVoiceService();

            if (result.success) {
                // Start progress polling
                this.startProgressPolling();
                this.ui.addConsoleMessage('info', 'Installation in progress');
            } else {
                this.state.isInstalling = false;
                this.ui.hideModal('installationProgressModal');
                throw new Error(result.error || 'Failed to start installation');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Installation failed:', error);
            this.state.isInstalling = false;
            this.ui.hideModal('installationProgressModal');
            this.ui.showError('Installation failed: ' + error.message);
            this.updateUI();
        }
    }

    /**
     * Start progress polling for installation
     */
    startProgressPolling() {
        if (this.progressPollingTimer) {
            clearInterval(this.progressPollingTimer);
        }

        console.log('[VoiceAssistant] Starting installation progress polling');

        this.progressPollingTimer = setInterval(async () => {
            try {
                const progressResult = await this.api.getInstallationProgress();

                if (progressResult.success) {
                    // Update UI with progress
                    this.ui.updateInstallationProgress(
                        progressResult.progress || 0,
                        progressResult.current_step || 'Installing...',
                        progressResult.current_package || '',
                        progressResult.download_progress ? `${progressResult.download_progress}%` : '',
                        progressResult.status_message || ''
                    );

                    // Update console
                    if (progressResult.status_message) {
                        this.ui.addConsoleMessage('info', progressResult.status_message);
                    }

                    // Check if complete or error
                    if (progressResult.is_complete) {
                        this.stopProgressPolling();
                        this.state.isInstalling = false;
                        this.ui.showSuccess('Dependencies installed successfully!');
                        this.ui.addConsoleMessage('success', 'Installation completed successfully');

                        // Hide modal after delay
                        setTimeout(() => {
                            this.ui.hideModal('installationProgressModal');
                        }, 3000);

                        // Refresh status
                        await this.checkServiceStatus();
                        await this.checkDependenciesStatus();
                    } else if (progressResult.has_error) {
                        this.stopProgressPolling();
                        this.state.isInstalling = false;
                        this.ui.hideModal('installationProgressModal');
                        this.ui.showError('Installation failed: ' + progressResult.error_message);
                        this.ui.addConsoleMessage('error', 'Installation failed: ' + progressResult.error_message);
                    }
                } else {
                    // Polling failed, stop trying
                    this.stopProgressPolling();
                    this.ui.addConsoleMessage('warning', 'Lost connection to installation progress');
                }
            } catch (error) {
                console.warn('[VoiceAssistant] Progress polling error:', error);
                // Continue polling despite errors
            }
        }, this.config.progressPollingInterval);
    }

    /**
     * Stop progress polling
     */
    stopProgressPolling() {
        if (this.progressPollingTimer) {
            clearInterval(this.progressPollingTimer);
            this.progressPollingTimer = null;
            console.log('[VoiceAssistant] Stopped installation progress polling');
        }
    }

    /**
     * Start STT recording
     */
    async startSTTRecording() {
        if (!this.ensureRecordingCapable()) return;
        if (!this.ensureServiceReady()) return;

        console.log('[VoiceAssistant] Starting STT recording');

        try {
            await this.startRecording();
            this.ui.updateSTTRecordingState(true, false);
            this.ui.addConsoleMessage('info', 'STT: Started listening');
        } catch (error) {
            console.error('[VoiceAssistant] STT recording failed:', error);
            this.ui.showError('Failed to start recording: ' + error.message);
        }
    }

    /**
     * Stop STT recording
     */
    async stopSTTRecording() {
        console.log('[VoiceAssistant] Stopping STT recording');

        try {
            await this.stopRecording();
            this.ui.updateSTTRecordingState(false, true);
            this.ui.addConsoleMessage('info', 'STT: Processing audio');
        } catch (error) {
            console.error('[VoiceAssistant] STT stop failed:', error);
            this.ui.showError('Failed to stop recording: ' + error.message);
        }
    }

    /**
     * Speak text using TTS
     */
    async speakText(text) {
        if (!this.ensureServiceReady()) return;

        console.log('[VoiceAssistant] Speaking text via TTS:', text.substring(0, 50) + '...');
        console.log('[VoiceAssistant] [DEBUG] TTS call trace:', new Error().stack);
        console.log('[VoiceAssistant] [DEBUG] Current isSpeaking state:', this.state.isSpeaking);

        try {
            this.state.isSpeaking = true;
            this.ui.updateTTSControlsState(true, false);
            this.ui.addConsoleMessage('info', `TTS: Speaking "${text.substring(0, 30)}..."`);

            // Get current settings from UI
            const settings = this.ui.getCurrentSettings();

            // Use the new pure TTS endpoint
            const result = await this.api.processTTS(
                text,
                settings.tts.voice,
                'en-US', // TODO: Get from settings
                settings.tts.volume
            );

            if (result.success) {
                // Play TTS response if available
                if (result.audio_data) {
                    await this.playAudioResponse(result.audio_data);
                }

                this.ui.addHistoryItem('tts', text, 'Text spoken successfully');
                this.ui.showSuccess('Text spoken successfully');
                this.ui.addConsoleMessage('success', 'TTS: Text spoken successfully');
            } else {
                throw new Error(result.error || result.message || 'TTS failed');
            }
        } catch (error) {
            console.error('[VoiceAssistant] TTS failed:', error);
            this.ui.showError('Text-to-speech failed: ' + error.message);
            this.ui.addConsoleMessage('error', 'TTS: Failed - ' + error.message);
        } finally {
            this.state.isSpeaking = false;
            this.ui.updateTTSControlsState(false, true);
            this.updateUI();
        }
    }

    /**
     * Stop current TTS playback
     */
    stopSpeaking() {
        console.log('[VoiceAssistant] Stopping TTS playback');

        try {
            // Stop current audio if playing
            if (this.currentAudio) {
                this.currentAudio.pause();
                this.currentAudio.currentTime = 0;
                this.currentAudio = null;
            }

            this.state.isSpeaking = false;
            this.ui.updateTTSControlsState(false, true);
            this.ui.addConsoleMessage('info', 'TTS: Playback stopped');
            this.updateUI();
        } catch (error) {
            console.error('[VoiceAssistant] Error stopping TTS:', error);
        }
    }

    /**
     * Start STS conversation
     */
    async startSTSConversation() {
        if (!this.ensureRecordingCapable()) return;
        if (!this.ensureServiceReady()) return;

        console.log('[VoiceAssistant] Starting STS conversation');

        try {
            await this.startRecording();
            this.ui.updateSTSConversationState(true);
            this.ui.addConsoleMessage('info', 'STS: Started conversation');
        } catch (error) {
            console.error('[VoiceAssistant] STS conversation failed:', error);
            this.ui.showError('Failed to start conversation: ' + error.message);
        }
    }

    /**
     * Stop STS conversation
     */
    async stopSTSConversation() {
        console.log('[VoiceAssistant] Stopping STS conversation');

        try {
            await this.stopRecording();
            this.ui.updateSTSConversationState(false);
            this.ui.addConsoleMessage('info', 'STS: Conversation stopped');
        } catch (error) {
            console.error('[VoiceAssistant] STS stop failed:', error);
            this.ui.showError('Failed to stop conversation: ' + error.message);
        }
    }

    /**
     * Start voice recording (common for STT and STS)
     */
    async startRecording() {
        if (this.state.isRecording) {
            return;
        }

        if (!this.state.canRecord) {
            throw new Error('Voice recording not available in this browser or connection');
        }

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
     * Stop voice recording (common for STT and STS)
     */
    async stopRecording() {
        if (!this.state.isRecording || !this.mediaRecorder) {
            return;
        }

        try {
            if (this.recordingTimeout) {
                clearTimeout(this.recordingTimeout);
                this.recordingTimeout = null;
            }

            if (this.mediaRecorder.state !== 'inactive') {
                this.mediaRecorder.stop();
            }

            this.state.isRecording = false;

        } catch (error) {
            console.error('[VoiceAssistant] Error stopping recording:', error);
            this.ui.showError('Error stopping recording: ' + error.message);
        }
    }

    /**
     * Process recorded audio based on current mode
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

            if (this.state.currentMode === 'stt') {
                await this.processSTTAudio(audioData, settings);
            } else if (this.state.currentMode === 'sts') {
                await this.processSTSAudio(audioData, settings);
            }

        } catch (error) {
            console.error('[VoiceAssistant] Error processing audio:', error);
            this.ui.showError('Failed to process audio: ' + error.message);
        } finally {
            this.state.isProcessing = false;
            this.updateUI();
        }
    }

    /**
     * Process audio for STT mode using the new pure STT endpoint
     */
    async processSTTAudio(audioData, settings) {
        // Use the new pure STT endpoint
        const result = await this.api.processSTT(
            audioData,
            settings.stt.language,
            {
                returnConfidence: true,
                returnAlternatives: false,
                modelPreference: 'accuracy'
            }
        );

        if (result.success && result.transcription) {
            this.ui.updateSTTTranscription(result.transcription, result.confidence);
            this.ui.addHistoryItem('stt', 'Audio input', result.transcription);
            this.ui.addConsoleMessage('success', `STT: "${result.transcription}"`);
        } else {
            throw new Error(result.error || result.message || 'STT failed');
        }

        this.ui.updateSTTRecordingState(false, false);
    }

    /**
     * Process audio for STS mode using the new speech-to-speech convenience method
     */
    async processSTSAudio(audioData, settings) {
        // Use the new speech-to-speech convenience method
        const result = await this.api.processSpeechToSpeech(
            audioData,
            settings.stt.language,
            settings.tts.voice,
            settings.tts.volume,
            {
                stt: {
                    returnConfidence: true,
                    returnAlternatives: false
                },
                tts: {
                    speed: 1.0,
                    pitch: 1.0,
                    format: 'wav'
                }
            }
        );

        if (result.success) {
            // Extract results from pipeline
            const sttResult = result.pipeline_results?.stt;
            const ttsResult = result.pipeline_results?.tts;

            const transcription = sttResult?.transcription || 'Audio processed';
            const aiResponse = 'Echo: ' + transcription; // Simple echo for now

            // Update conversation display
            this.ui.updateSTSConversationState(
                false,
                transcription,
                aiResponse
            );

            // Add to history
            this.ui.addHistoryItem('sts', transcription, aiResponse);

            // Play TTS response if available
            if (ttsResult?.audio_data) {
                await this.playAudioResponse(ttsResult.audio_data);
            }

            this.ui.addConsoleMessage('success', 'STS: Conversation completed');
        } else {
            throw new Error(result.error || result.message || 'STS failed');
        }
    }

    /**
     * Play TTS audio response
     */
    async playAudioResponse(base64Audio) {
        try {
            console.log('[VoiceAssistant] Playing TTS audio response');

            // Create audio blob from base64
            const audioData = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));
            const audioBlob = new Blob([audioData], { type: 'audio/wav' });
            const audioUrl = URL.createObjectURL(audioBlob);

            // Create and play audio element
            this.currentAudio = new Audio(audioUrl);

            this.currentAudio.onended = () => {
                URL.revokeObjectURL(audioUrl);
                this.currentAudio = null;
            };

            this.currentAudio.onerror = (error) => {
                console.error('[VoiceAssistant] Audio playback error:', error);
                URL.revokeObjectURL(audioUrl);
                this.currentAudio = null;
            };

            await this.currentAudio.play();
            console.log('[VoiceAssistant] TTS audio playback started');

        } catch (error) {
            console.error('[VoiceAssistant] Error playing TTS audio:', error);
            // Don't show error to user for audio playback issues
        }
    }

    /**
     * Start STT recording using server-side recording
     */
    async startSTTRecording() {
        if (!this.ensureServiceReady()) return;

        console.log('[VoiceAssistant] Starting server-side STT recording');

        try {
            this.state.isRecording = true;
            this.ui.updateSTTRecordingState(true, false);
            this.ui.addConsoleMessage('info', 'STT: Started server-side recording');

            // Get current settings from UI
            const settings = this.ui.getCurrentSettings();
            const duration = 10; // 10 seconds default

            // Start server-side recording
            const result = await this.api.completeServerRecording(
                duration,
                settings.stt.language,
                'stt',
                {},
                (progress) => this.handleRecordingProgress(progress)
            );

            if (result.success) {
                if (result.transcription) {
                    this.ui.updateSTTTranscription(result.transcription, 0.9); // Server-side confidence
                    this.ui.addHistoryItem('stt', 'Server recording', result.transcription);
                    this.ui.addConsoleMessage('success', `STT: "${result.transcription}"`);
                } else {
                    throw new Error('No transcription received from server recording');
                }
            } else {
                throw new Error(result.error || result.message || 'Server recording failed');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Server STT recording failed:', error);
            this.ui.showError('Server recording failed: ' + error.message);
            this.ui.addConsoleMessage('error', 'STT: Server recording failed - ' + error.message);
        } finally {
            this.state.isRecording = false;
            this.ui.updateSTTRecordingState(false, false);
            this.updateUI();
        }
    }

    /**
     * Stop STT recording (for server-side, this is handled automatically)
     */
    async stopSTTRecording() {
        if (!this.state.isRecording) return;

        console.log('[VoiceAssistant] Stopping server-side STT recording');
        
        try {
            // For server-side recording, we can try to stop early
            const result = await this.api.stopServerRecording();
            
            if (result.success && result.transcription) {
                this.ui.updateSTTTranscription(result.transcription, 0.9);
                this.ui.addHistoryItem('stt', 'Server recording (stopped early)', result.transcription);
                this.ui.addConsoleMessage('success', `STT: "${result.transcription}"`);
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error stopping server recording:', error);
        } finally {
            this.state.isRecording = false;
            this.ui.updateSTTRecordingState(false, false);
            this.updateUI();
        }
    }

    /**
     * Start STS conversation using server-side recording
     */
    async startSTSConversation() {
        if (!this.ensureServiceReady()) return;

        console.log('[VoiceAssistant] Starting server-side STS conversation');

        try {
            this.state.isRecording = true;
            this.ui.updateSTSConversationState(true);
            this.ui.addConsoleMessage('info', 'STS: Started server-side conversation');

            // Get current settings from UI
            const settings = this.ui.getCurrentSettings();
            const duration = 10; // 10 seconds default

            // Start server-side STS recording
            const result = await this.api.completeServerRecording(
                duration,
                settings.stt.language,
                'sts',
                {
                    tts_voice: settings.tts.voice,
                    tts_volume: settings.tts.volume
                },
                (progress) => this.handleRecordingProgress(progress)
            );

            if (result.success) {
                const transcription = result.transcription || 'Audio processed';
                const aiResponse = result.ai_response || 'Echo: ' + transcription;

                // Update conversation display
                this.ui.updateSTSConversationState(
                    false,
                    transcription,
                    aiResponse
                );

                // Add to history
                this.ui.addHistoryItem('sts', transcription, aiResponse);

                // Play TTS response if available
                if (result.audio_data) {
                    await this.playAudioResponse(result.audio_data);
                }

                this.ui.addConsoleMessage('success', 'STS: Server conversation completed');
            } else {
                throw new Error(result.error || result.message || 'Server STS failed');
            }

        } catch (error) {
            console.error('[VoiceAssistant] Server STS conversation failed:', error);
            this.ui.showError('Server conversation failed: ' + error.message);
            this.ui.addConsoleMessage('error', 'STS: Server conversation failed - ' + error.message);
        } finally {
            this.state.isRecording = false;
            this.ui.updateSTSConversationState(false);
            this.updateUI();
        }
    }

    /**
     * Stop STS conversation (for server-side, this is handled automatically)
     */
    async stopSTSConversation() {
        if (!this.state.isRecording) return;

        console.log('[VoiceAssistant] Stopping server-side STS conversation');
        
        try {
            // For server-side recording, we can try to stop early
            const result = await this.api.stopServerRecording();
            
            if (result.success) {
                const transcription = result.transcription || 'Audio processed';
                const aiResponse = result.ai_response || 'Echo: ' + transcription;

                this.ui.updateSTSConversationState(false, transcription, aiResponse);
                this.ui.addHistoryItem('sts', transcription, aiResponse);

                if (result.audio_data) {
                    await this.playAudioResponse(result.audio_data);
                }

                this.ui.addConsoleMessage('success', 'STS: Server conversation completed');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error stopping server STS:', error);
        } finally {
            this.state.isRecording = false;
            this.ui.updateSTSConversationState(false);
            this.updateUI();
        }
    }

    /**
     * Handle recording progress updates from server
     */
    handleRecordingProgress(progress) {
        try {
            if (progress.type === 'started') {
                this.ui.addConsoleMessage('info', 'Server recording started');
            } else if (progress.type === 'progress') {
                const data = progress.data;
                if (data.is_recording) {
                    const elapsed = data.elapsed_seconds || 0;
                    const total = data.total_duration || 10;
                    this.ui.addConsoleMessage('info', `Recording: ${elapsed}/${total} seconds`);
                } else if (data.status === 'processing') {
                    this.ui.addConsoleMessage('info', 'Processing recorded audio...');
                    if (this.state.currentMode === 'stt') {
                        this.ui.updateSTTRecordingState(false, true);
                    } else if (this.state.currentMode === 'sts') {
                        this.ui.updateSTSConversationState(false);
                    }
                }
            } else if (progress.type === 'completed') {
                this.ui.addConsoleMessage('success', 'Server recording completed');
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error handling recording progress:', error);
        }
    }

    /**
     * Remove browser-based recording methods since we're using server-side recording
     * These methods are no longer needed but kept as stubs for compatibility
     */
    async startRecording() {
        // Deprecated: Now handled by server-side recording
        console.warn('[VoiceAssistant] startRecording() is deprecated, use server-side recording methods');
    }

    async stopRecording() {
        // Deprecated: Now handled by server-side recording
        console.warn('[VoiceAssistant] stopRecording() is deprecated, use server-side recording methods');
    }

    async processAudio(audioBlob) {
        // Deprecated: Now handled by server-side recording
        console.warn('[VoiceAssistant] processAudio() is deprecated, server handles audio processing');
    }

    /**
     * Update the ensureRecordingCapable method to always return true for server-side recording
     */
    ensureRecordingCapable() {
        // Server-side recording is always available if the service is running
        return this.ensureServiceReady();
    }

    /**
     * Clear console output
     */
    clearConsole() {
        this.ui.addConsoleMessage('info', 'Console cleared');
    }

    /**
     * Clear session history
     */
    clearHistory() {
        this.ui.addConsoleMessage('info', 'Session history cleared');
    }

    /**
     * Start health monitoring
     */
    startHealthMonitoring() {
        if (this.healthCheckTimer) {
            clearInterval(this.healthCheckTimer);
        }

        this.healthCheckTimer = setInterval(async () => {
            try {
                if (!this.state.isInstalling) {
                    await this.checkServiceStatus();
                }
            } catch (error) {
                console.debug('[VoiceAssistant] Health monitoring error:', error);
                // Don't show errors for automatic health checks
            }
        }, this.config.healthCheckInterval);

        console.log('[VoiceAssistant] Health monitoring started');
    }

    /**
     * Ensure service is ready for operations
     */
    ensureServiceReady() {
        if (!this.state.serviceRunning || !this.state.serviceHealthy) {
            this.ui.showError('Voice service is not available. Please start the service first.');
            return false;
        }

        if (this.state.isInstalling) {
            this.ui.showError('Dependencies are being installed. Please wait for completion.');
            return false;
        }

        if (this.state.isProcessing) {
            this.ui.showError('Please wait for current processing to complete.');
            return false;
        }

        return true;
    }

    /**
     * Ensure recording capability is available
     */
    ensureRecordingCapable() {
        if (!this.state.canRecord) {
            this.ui.showError('Voice recording is not available. Please use HTTPS or localhost and ensure microphone permissions are granted.');
            return false;
        }
        return true;
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
        if (this.state.isInstalling) {
            return 'installing';
        } else if (this.state.serviceRunning && this.state.serviceHealthy) {
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
        if (this.state.isInstalling) {
            return 'Installing dependencies...';
        } else if (this.state.serviceRunning && this.state.serviceHealthy) {
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
            // Stop any ongoing activity
            this.stopAllActivity();

            // Clear intervals
            if (this.progressPollingTimer) {
                clearInterval(this.progressPollingTimer);
            }

            if (this.healthCheckTimer) {
                clearInterval(this.healthCheckTimer);
            }

            if (this.dependencyCheckInterval) {
                clearInterval(this.dependencyCheckInterval);
            }

            if (this.recordingTimeout) {
                clearTimeout(this.recordingTimeout);
            }

            // Clean up audio context
            if (this.audioContext) {
                this.audioContext.close();
            }

            // Clean up current audio
            if (this.currentAudio) {
                this.currentAudio.pause();
                this.currentAudio = null;
            }

            console.log('[VoiceAssistant] Core module destroyed');
        } catch (error) {
            console.error('[VoiceAssistant] Error during cleanup:', error);
        }
    }
}

// Export for global access
window.VoiceAssistantCore = VoiceAssistantCore;
