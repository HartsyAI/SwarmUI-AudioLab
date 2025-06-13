/**
 * Voice Assistant API Module - Refactored with Generic Endpoints
 * Modern, reusable API interface for STT, TTS, and pipeline processing.
 * Clean separation of concerns with composable services.
 */

class VoiceAssistantAPI {
    constructor() {
        this.apiTimeout = 45000; // 45 seconds
        this.retryAttempts = 3;
        this.retryDelay = 1000; // 1 second
        this.progressPollingActive = false;

        console.log('[VoiceAssistant] API module initialized with generic endpoints');
    }

    /**
     * Pure Speech-to-Text processing endpoint
     * @param {string} audioData - Base64 encoded audio data
     * @param {string} language - Language code (e.g., 'en-US')
     * @param {Object} options - STT processing options
     * @returns {Promise<Object>} STT response with transcription and metadata
     */
    async processSTT(audioData, language = 'en-US', options = {}) {
        const payload = {
            audio_data: audioData,
            language: language,
            options: {
                return_confidence: options.returnConfidence ?? true,
                return_alternatives: options.returnAlternatives ?? false,
                model_preference: options.modelPreference ?? 'accuracy',
                ...options.custom
            }
        };

        return await this.makeAPICall('ProcessSTT', payload);
    }

    /**
     * Pure Text-to-Speech processing endpoint
     * @param {string} text - Text to convert to speech
     * @param {string} voice - Voice identifier
     * @param {string} language - Language code
     * @param {number} volume - Volume level (0.0 to 1.0)
     * @param {Object} options - TTS processing options
     * @returns {Promise<Object>} TTS response with audio data and metadata
     */
    async processTTS(text, voice = 'default', language = 'en-US', volume = 0.8, options = {}) {
        const payload = {
            text: text,
            voice: voice,
            language: language,
            volume: volume,
            options: {
                speed: options.speed ?? 1.0,
                pitch: options.pitch ?? 1.0,
                format: options.format ?? 'wav',
                ...options.custom
            }
        };

        return await this.makeAPICall('ProcessTTS', payload);
    }

    /**
     * Universal configurable pipeline processing endpoint
     * @param {string} inputType - Type of input ('audio' or 'text')
     * @param {string} inputData - Input data (base64 audio or text string)
     * @param {Array} pipelineSteps - Array of pipeline step configurations
     * @param {string} sessionId - Optional session identifier
     * @returns {Promise<Object>} Pipeline response with results from all steps
     */
    async processPipeline(inputType, inputData, pipelineSteps, sessionId = null) {
        const payload = {
            input_type: inputType,
            input_data: inputData,
            pipeline_steps: pipelineSteps,
            session_id: sessionId || `pipeline-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`
        };

        return await this.makeAPICall('ProcessPipeline', payload);
    }

    /**
     * Convenience method for Speech-to-Speech processing
     * @param {string} audioData - Base64 encoded audio data
     * @param {string} language - Language code
     * @param {string} voice - Voice for TTS output
     * @param {number} volume - Volume level
     * @param {Object} options - Combined STT and TTS options
     * @returns {Promise<Object>} Pipeline response with STT and TTS results
     */
    async processSpeechToSpeech(audioData, language = 'en-US', voice = 'default', volume = 0.8, options = {}) {
        const pipelineSteps = [
            {
                type: 'stt',
                enabled: true,
                config: {
                    language: language,
                    options: options.stt || {}
                }
            },
            {
                type: 'tts',
                enabled: true,
                config: {
                    voice: voice,
                    language: language,
                    volume: volume,
                    options: options.tts || {}
                }
            }
        ];

        return await this.processPipeline('audio', audioData, pipelineSteps);
    }

    /**
     * Convenience method for Voice Commands processing (future)
     * @param {string} audioData - Base64 encoded audio data
     * @param {string} language - Language code
     * @param {string} voice - Voice for TTS response
     * @param {number} volume - Volume level
     * @param {Object} commandOptions - Command processing options
     * @returns {Promise<Object>} Pipeline response with STT, command, and TTS results
     */
    async processVoiceCommand(audioData, language = 'en-US', voice = 'default', volume = 0.8, commandOptions = {}) {
        const pipelineSteps = [
            {
                type: 'stt',
                enabled: true,
                config: {
                    language: language,
                    options: {}
                }
            },
            {
                type: 'command_processing',
                enabled: true,
                config: {
                    processor: commandOptions.processor || 'swarmui_commands',
                    options: commandOptions
                }
            },
            {
                type: 'tts',
                enabled: true,
                config: {
                    voice: voice,
                    language: language,
                    volume: volume,
                    options: {}
                }
            }
        ];

        return await this.processPipeline('audio', audioData, pipelineSteps);
    }

    /**
     * Get voice service status
     */
    async getVoiceStatus() {
        return await this.makeAPICall('GetVoiceStatus', {});
    }

    /**
     * Start voice service
     */
    async startVoiceService() {
        return await this.makeAPICall('StartVoiceService', {});
    }

    /**
     * Stop voice service
     */
    async stopVoiceService() {
        return await this.makeAPICall('StopVoiceService', {});
    }

    /**
     * Check installation status
     */
    async checkInstallationStatus() {
        return await this.makeAPICall('CheckInstallationStatus', {});
    }

    /**
     * Get installation progress
     */
    async getInstallationProgress() {
        return await this.makeAPICall('GetInstallationProgress', {});
    }

    /**
     * Pipeline Step Builder Utilities
     * Helper methods to create common pipeline step configurations
     */
    
    /**
     * Create STT pipeline step configuration
     * @param {string} language - Language code
     * @param {Object} options - STT options
     * @returns {Object} STT step configuration
     */
    createSTTStep(language = 'en-US', options = {}) {
        return {
            type: 'stt',
            enabled: true,
            config: {
                language: language,
                options: {
                    return_confidence: options.returnConfidence ?? true,
                    return_alternatives: options.returnAlternatives ?? false,
                    model_preference: options.modelPreference ?? 'accuracy',
                    ...options.custom
                }
            }
        };
    }

    /**
     * Create TTS pipeline step configuration
     * @param {string} voice - Voice identifier
     * @param {string} language - Language code
     * @param {number} volume - Volume level
     * @param {Object} options - TTS options
     * @returns {Object} TTS step configuration
     */
    createTTSStep(voice = 'default', language = 'en-US', volume = 0.8, options = {}) {
        return {
            type: 'tts',
            enabled: true,
            config: {
                voice: voice,
                language: language,
                volume: volume,
                options: {
                    speed: options.speed ?? 1.0,
                    pitch: options.pitch ?? 1.0,
                    format: options.format ?? 'wav',
                    ...options.custom
                }
            }
        };
    }

    /**
     * Create command processing pipeline step configuration
     * @param {string} processor - Command processor type
     * @param {Object} options - Command processing options
     * @returns {Object} Command processing step configuration
     */
    createCommandStep(processor = 'swarmui_commands', options = {}) {
        return {
            type: 'command_processing',
            enabled: true,
            config: {
                processor: processor,
                options: options
            }
        };
    }

    /**
     * Make API call using SwarmUI's genericRequest with enhanced retry logic
     */
    async makeAPICall(methodName, payload, attempt = 1) {
        return new Promise((resolve, reject) => {
            console.log(`[VoiceAssistant] API call: ${methodName} (attempt ${attempt})`, payload);

            try {
                const startTime = Date.now();

                // Set up timeout
                const timeoutId = setTimeout(() => {
                    const duration = Date.now() - startTime;
                    console.error(`[VoiceAssistant] API call ${methodName} timed out after ${duration}ms`);

                    if (attempt < this.retryAttempts && this.shouldRetryTimeout(methodName)) {
                        console.log(`[VoiceAssistant] Retrying ${methodName} (attempt ${attempt + 1})`);
                        setTimeout(() => {
                            this.makeAPICall(methodName, payload, attempt + 1)
                                .then(resolve)
                                .catch(reject);
                        }, this.retryDelay * attempt);
                    } else {
                        reject(new Error(`API call ${methodName} timed out after ${this.retryAttempts} attempts`));
                    }
                }, this.getTimeoutForMethod(methodName));

                genericRequest(methodName, payload, (data) => {
                    clearTimeout(timeoutId);
                    const duration = Date.now() - startTime;

                    console.log(`[VoiceAssistant] API response for ${methodName} (${duration}ms):`, data);

                    // Validate response
                    if (this.isValidResponse(data)) {
                        resolve(data);
                    } else {
                        const error = new Error(`Invalid response from ${methodName}: ${JSON.stringify(data)}`);

                        if (attempt < this.retryAttempts && this.shouldRetryResponse(data, methodName)) {
                            console.log(`[VoiceAssistant] Retrying ${methodName} due to invalid response (attempt ${attempt + 1})`);
                            setTimeout(() => {
                                this.makeAPICall(methodName, payload, attempt + 1)
                                    .then(resolve)
                                    .catch(reject);
                            }, this.retryDelay * attempt);
                        } else {
                            reject(error);
                        }
                    }
                }, (error) => {
                    clearTimeout(timeoutId);
                    console.error(`[VoiceAssistant] API error for ${methodName}:`, error);

                    if (attempt < this.retryAttempts && this.shouldRetryError(error, methodName)) {
                        console.log(`[VoiceAssistant] Retrying ${methodName} due to error (attempt ${attempt + 1})`);
                        setTimeout(() => {
                            this.makeAPICall(methodName, payload, attempt + 1)
                                .then(resolve)
                                .catch(reject);
                        }, this.retryDelay * attempt);
                    } else {
                        reject(new Error(`API call ${methodName} failed: ${error.message || error}`));
                    }
                });
            } catch (error) {
                console.error(`[VoiceAssistant] API request error for ${methodName}:`, error);
                reject(error);
            }
        });
    }

    /**
     * Get timeout duration based on method type
     */
    getTimeoutForMethod(methodName) {
        const timeouts = {
            'StartVoiceService': 120000, // 2 minutes for service start
            'ProcessSTT': 60000,         // 1 minute for STT processing
            'ProcessTTS': 30000,         // 30 seconds for TTS processing
            'ProcessPipeline': 90000,    // 1.5 minutes for pipeline processing
            'GetInstallationProgress': 5000, // 5 seconds for progress checks
            'GetVoiceStatus': 10000,     // 10 seconds for status checks
            'CheckInstallationStatus': 15000, // 15 seconds for dependency checks
            'StopVoiceService': 15000    // 15 seconds for service stop
        };

        return timeouts[methodName] || this.apiTimeout;
    }

    /**
     * Determine if a timeout should trigger a retry based on method
     */
    shouldRetryTimeout(methodName) {
        const noRetryMethods = ['GetInstallationProgress', 'GetVoiceStatus'];
        return !noRetryMethods.includes(methodName);
    }

    /**
     * Validate API response structure
     */
    isValidResponse(data) {
        return data && typeof data === 'object' && data.hasOwnProperty('success');
    }

    /**
     * Determine if a response should trigger a retry
     */
    shouldRetryResponse(data, methodName) {
        if (data && data.success === false) {
            const error = data.error || data.message || '';
            const retryableErrors = [
                'timeout', 'network', 'connection', 'service not available',
                'backend not responding', 'service starting'
            ];
            return retryableErrors.some(retryableError =>
                error.toLowerCase().includes(retryableError)
            );
        }
        return !this.isValidResponse(data);
    }

    /**
     * Determine if an error should trigger a retry
     */
    shouldRetryError(error, methodName) {
        const errorMessage = (error.message || error || '').toLowerCase();

        // Always retry network-related errors
        const networkErrors = [
            'network', 'timeout', 'connection', 'fetch', 'xhr',
            'cors', 'dns', 'socket', 'refused'
        ];

        if (networkErrors.some(netError => errorMessage.includes(netError))) {
            return true;
        }

        // Don't retry client errors (4xx) except for specific cases
        if (errorMessage.includes('400') || errorMessage.includes('401') ||
            errorMessage.includes('403') || errorMessage.includes('404')) {
            return false;
        }

        // Retry server errors (5xx)
        if (errorMessage.includes('500') || errorMessage.includes('502') ||
            errorMessage.includes('503') || errorMessage.includes('504')) {
            return true;
        }

        // Don't retry progress polling errors
        if (methodName === 'GetInstallationProgress' || methodName === 'GetVoiceStatus') {
            return false;
        }

        return true;
    }

    /**
     * Health check with minimal error handling
     */
    async quickHealthCheck() {
        try {
            const result = await this.makeAPICall('GetVoiceStatus', {});
            return result.success && result.backend_running && result.backend_healthy;
        } catch (error) {
            console.debug('[VoiceAssistant] Quick health check failed:', error.message);
            return false;
        }
    }

    /**
     * Test API connectivity with comprehensive diagnostics
     */
    async testConnectivity() {
        console.log('[VoiceAssistant] Testing API connectivity...');

        const results = {
            status: false,
            latency: null,
            error: null,
            service_running: false,
            service_healthy: false,
            dependencies_installed: false
        };

        try {
            const startTime = Date.now();
            const statusResponse = await this.getVoiceStatus();
            const latency = Date.now() - startTime;

            results.latency = latency;
            results.status = statusResponse.success;

            if (statusResponse.success) {
                results.service_running = statusResponse.backend_running || false;
                results.service_healthy = statusResponse.backend_healthy || false;

                try {
                    const depResponse = await this.checkInstallationStatus();
                    results.dependencies_installed = depResponse.dependencies_installed || false;
                } catch (depError) {
                    console.warn('[VoiceAssistant] Dependency check failed during connectivity test:', depError);
                }

                console.log(`[VoiceAssistant] API connectivity test passed (${latency}ms)`);
            } else {
                results.error = statusResponse.error || 'Unknown error';
                console.warn(`[VoiceAssistant] API connectivity test failed: ${results.error}`);
            }
        } catch (error) {
            results.error = error.message;
            console.error('[VoiceAssistant] API connectivity test error:', error);
        }

        return results;
    }

    /**
     * Start progress polling for installation
     */
    startProgressPolling(onProgress, onComplete = null) {
        if (this.progressPollingActive) {
            console.warn('[VoiceAssistant] Progress polling already active');
            return;
        }

        this.progressPollingActive = true;
        console.log('[VoiceAssistant] Starting progress polling');

        const pollInterval = setInterval(async () => {
            try {
                if (!this.progressPollingActive) {
                    clearInterval(pollInterval);
                    return;
                }

                const progressResult = await this.makeAPICall('GetInstallationProgress', {});

                if (progressResult.success) {
                    onProgress(progressResult);

                    if (progressResult.is_complete || progressResult.has_error) {
                        clearInterval(pollInterval);
                        this.progressPollingActive = false;

                        if (onComplete) {
                            onComplete(progressResult);
                        }
                    }
                } else {
                    console.warn('[VoiceAssistant] Progress polling failed, stopping');
                    clearInterval(pollInterval);
                    this.progressPollingActive = false;
                }
            } catch (error) {
                console.warn('[VoiceAssistant] Progress polling error:', error);
                if (error.message.includes('not found') || error.message.includes('404')) {
                    clearInterval(pollInterval);
                    this.progressPollingActive = false;
                }
            }
        }, 1000);

        // Auto-stop polling after 30 minutes (safety measure)
        setTimeout(() => {
            if (this.progressPollingActive) {
                clearInterval(pollInterval);
                this.progressPollingActive = false;
                console.warn('[VoiceAssistant] Progress polling auto-stopped after timeout');
            }
        }, 30 * 60 * 1000);
    }

    /**
     * Stop progress polling
     */
    stopProgressPolling() {
        this.progressPollingActive = false;
        console.log('[VoiceAssistant] Progress polling stopped');
    }

    /**
     * Validate audio data before sending
     */
    validateAudioData(audioData) {
        if (!audioData || typeof audioData !== 'string') {
            throw new Error('Invalid audio data: must be a base64 string');
        }

        if (!/^[A-Za-z0-9+/]*={0,2}$/.test(audioData)) {
            throw new Error('Invalid audio data: not valid base64');
        }

        const sizeBytes = (audioData.length * 3) / 4;
        const maxSize = 50 * 1024 * 1024; // 50MB

        if (sizeBytes > maxSize) {
            throw new Error(`Audio data too large: ${Math.round(sizeBytes / 1024 / 1024)}MB (max 50MB)`);
        }

        return true;
    }

    /**
     * Server-Side Recording Methods
     * These methods handle recording on the server, bypassing browser security restrictions
     */

    /**
     * Start server-side recording
     * @param {number} duration - Recording duration in seconds (1-30)
     * @param {string} language - Language code (e.g., 'en-US')
     * @param {string} mode - Recording mode ('stt', 'sts', or 'raw')
     * @param {Object} options - Additional recording options
     * @returns {Promise<Object>} Recording start response
     */
    async startServerRecording(duration = 10, language = 'en-US', mode = 'stt', options = {}) {
        const payload = {
            duration: Math.max(1, Math.min(30, duration)), // Clamp to 1-30 seconds
            language: language,
            mode: mode,
            options: options
        };

        return await this.makeAPICall('StartServerRecording', payload);
    }

    /**
     * Stop server-side recording and get results
     * @returns {Promise<Object>} Recording stop response with processed audio
     */
    async stopServerRecording() {
        return await this.makeAPICall('StopServerRecording', {});
    }

    /**
     * Get server-side recording status
     * @returns {Promise<Object>} Recording status response
     */
    async getRecordingStatus() {
        return await this.makeAPICall('GetRecordingStatus', {});
    }

    /**
     * Convenience method for server-side STT recording
     * @param {number} duration - Recording duration in seconds
     * @param {string} language - Language code
     * @param {Object} options - STT options
     * @returns {Promise<Object>} STT recording response
     */
    async recordSTTOnServer(duration = 10, language = 'en-US', options = {}) {
        return await this.startServerRecording(duration, language, 'stt', options);
    }

    /**
     * Convenience method for server-side STS recording
     * @param {number} duration - Recording duration in seconds
     * @param {string} language - Language code
     * @param {string} voice - Voice for TTS response
     * @param {number} volume - Volume level
     * @returns {Promise<Object>} STS recording response
     */
    async recordSTSOnServer(duration = 10, language = 'en-US', voice = 'default', volume = 0.8) {
        const options = {
            tts_voice: voice,
            tts_volume: volume
        };
        return await this.startServerRecording(duration, language, 'sts', options);
    }

    /**
     * Poll recording status until completion
     * @param {Function} onProgress - Progress callback function
     * @param {number} pollInterval - Polling interval in milliseconds
     * @returns {Promise<Object>} Final recording result
     */
    async pollRecordingUntilComplete(onProgress = null, pollInterval = 1000) {
        return new Promise((resolve, reject) => {
            const poll = async () => {
                try {
                    const status = await this.getRecordingStatus();
                    
                    if (status.success) {
                        if (onProgress) {
                            onProgress(status);
                        }

                        if (status.status === 'completed' || status.status === 'error') {
                            // Recording finished, get final results
                            const result = await this.stopServerRecording();
                            resolve(result);
                        } else if (status.is_recording || status.status === 'recording' || status.status === 'processing') {
                            // Still recording or processing, continue polling
                            setTimeout(poll, pollInterval);
                        } else {
                            // Unknown status, try to stop
                            const result = await this.stopServerRecording();
                            resolve(result);
                        }
                    } else {
                        reject(new Error(status.error || 'Recording status check failed'));
                    }
                } catch (error) {
                    reject(error);
                }
            };

            poll();
        });
    }

    /**
     * Complete server-side recording workflow
     * Starts recording, polls for completion, and returns final result
     * @param {number} duration - Recording duration in seconds
     * @param {string} language - Language code
     * @param {string} mode - Recording mode
     * @param {Object} options - Recording options
     * @param {Function} onProgress - Progress callback
     * @returns {Promise<Object>} Complete recording result
     */
    async completeServerRecording(duration = 10, language = 'en-US', mode = 'stt', options = {}, onProgress = null) {
        try {
            // Start recording
            const startResult = await this.startServerRecording(duration, language, mode, options);
            
            if (!startResult.success) {
                throw new Error(startResult.error || 'Failed to start server recording');
            }

            if (onProgress) {
                onProgress({ type: 'started', data: startResult });
            }

            // Poll until completion
            const finalResult = await this.pollRecordingUntilComplete((status) => {
                if (onProgress) {
                    onProgress({ type: 'progress', data: status });
                }
            });

            if (onProgress) {
                onProgress({ type: 'completed', data: finalResult });
            }

            return finalResult;

        } catch (error) {
            // Try to stop recording on error
            try {
                await this.stopServerRecording();
            } catch (stopError) {
                console.warn('[VoiceAssistant] Failed to stop recording after error:', stopError);
            }
            
            throw error;
        }
    }

    /**
     * Validate text input
     */
    validateTextInput(text) {
        if (!text || typeof text !== 'string') {
            throw new Error('Invalid text input: must be a non-empty string');
        }

        const trimmed = text.trim();
        if (trimmed.length === 0) {
            throw new Error('Invalid text input: cannot be empty');
        }

        if (trimmed.length > 1000) {
            throw new Error('Text input too long: maximum 1000 characters');
        }

        return true;
    }

    /**
     * Get API call statistics
     */
    getStatistics() {
        return {
            apiTimeout: this.apiTimeout,
            retryAttempts: this.retryAttempts,
            retryDelay: this.retryDelay,
            progressPollingActive: this.progressPollingActive
        };
    }

    /**
     * Configure API settings
     */
    configure(options = {}) {
        if (options.timeout) {
            this.apiTimeout = Math.max(5000, Math.min(300000, options.timeout));
        }

        if (options.retryAttempts !== undefined) {
            this.retryAttempts = Math.max(1, Math.min(10, options.retryAttempts));
        }

        if (options.retryDelay) {
            this.retryDelay = Math.max(100, Math.min(10000, options.retryDelay));
        }

        console.log('[VoiceAssistant] API configuration updated:', {
            timeout: this.apiTimeout,
            retryAttempts: this.retryAttempts,
            retryDelay: this.retryDelay
        });
    }

    /**
     * Emergency stop for all ongoing operations
     */
    emergencyStop() {
        try {
            console.log('[VoiceAssistant] Emergency stop triggered');
            this.stopProgressPolling();
            console.log('[VoiceAssistant] Emergency stop completed');
        } catch (error) {
            console.error('[VoiceAssistant] Error during emergency stop:', error);
        }
    }

    /**
     * Cleanup method for graceful shutdown
     */
    cleanup() {
        try {
            this.stopProgressPolling();
            console.log('[VoiceAssistant] API module cleaned up');
        } catch (error) {
            console.error('[VoiceAssistant] Error during API cleanup:', error);
        }
    }
}

// Export for global access
window.VoiceAssistantAPI = VoiceAssistantAPI;
