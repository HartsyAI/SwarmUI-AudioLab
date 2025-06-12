/**
 * Voice Assistant API Module - Updated for New UI
 * Handles all communication with the SwarmUI backend API.
 * Provides a clean interface for voice processing operations with improved error handling.
 */

class VoiceAssistantAPI {
    constructor() {
        this.apiTimeout = 45000; // 45 seconds
        this.retryAttempts = 3;
        this.retryDelay = 1000; // 1 second
        this.progressPollingActive = false;

        console.log('[VoiceAssistant] API module initialized');
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
     * Process voice input (used for both STT and STS modes)
     */
    async processVoiceInput(payload) {
        return await this.makeAPICall('ProcessVoiceInput', payload);
    }

    /**
     * Process text command (used for TTS mode)
     */
    async processTextCommand(payload) {
        return await this.makeAPICall('ProcessTextCommand', payload);
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
     * TODO: Create dedicated STT testing endpoint
     * This would be useful for pure STT testing without command processing
     */
    async processSTTOnly(audioData, language = 'en-US') {
        // TODO: Add new C# API endpoint for STT-only testing
        // For now, use the existing voice input endpoint
        return await this.processVoiceInput({
            audio_data: audioData,
            language: language,
            session_id: `stt-test-${Date.now()}`
        });
    }

    /**
     * TODO: Create dedicated TTS testing endpoint
     * This would be useful for pure TTS testing without command processing
     */
    async processTTSOnly(text, voice = 'default', language = 'en-US', volume = 0.8) {
        // TODO: Add new C# API endpoint for TTS-only testing
        // For now, use the existing text command endpoint
        return await this.processTextCommand({
            text: text,
            voice: voice,
            language: language,
            volume: volume,
            session_id: `tts-test-${Date.now()}`
        });
    }

    /**
     * TODO: Create dependencies update endpoint
     * This would allow real-time dependency status updates
     */
    async updateDependencyStatus(packageName, status) {
        // TODO: Add new C# API endpoint for dependency status updates
        console.log('[VoiceAssistant] TODO: Implement dependency status update endpoint');
        return { success: false, error: 'Endpoint not implemented yet' };
    }

    /**
     * TODO: Create health check with detailed service info
     * This would provide more granular health information
     */
    async getDetailedHealthStatus() {
        // TODO: Add new C# API endpoint for detailed health status
        // For now, use the existing status endpoint
        return await this.getVoiceStatus();
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
            'StartVoiceService': 120000, // 2 minutes for service start (includes potential installation)
            'ProcessVoiceInput': 60000,  // 1 minute for voice processing
            'ProcessTextCommand': 30000, // 30 seconds for text processing
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
        // Check if response has required structure
        return data && typeof data === 'object' && data.hasOwnProperty('success');
    }

    /**
     * Determine if a response should trigger a retry
     */
    shouldRetryResponse(data, methodName) {
        // Don't retry for most response failures
        if (data && data.success === false) {
            const error = data.error || data.message || '';

            // Only retry for specific error types
            const retryableErrors = [
                'timeout', 'network', 'connection', 'service not available',
                'backend not responding', 'service starting'
            ];

            return retryableErrors.some(retryableError =>
                error.toLowerCase().includes(retryableError)
            );
        }

        // Retry for completely invalid response structures
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

        // Don't retry progress polling errors (they're frequent and expected)
        if (methodName === 'GetInstallationProgress' || methodName === 'GetVoiceStatus') {
            return false;
        }

        return true;
    }

    /**
     * Health check with minimal error handling for monitoring
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
     * Batch API calls with comprehensive error handling
     */
    async batchAPICall(calls) {
        const results = {};
        const errors = {};
        const promises = [];

        for (const [name, { method, payload }] of Object.entries(calls)) {
            const promise = this.makeAPICall(method, payload)
                .then(result => {
                    results[name] = result;
                })
                .catch(error => {
                    console.error(`[VoiceAssistant] Batch call ${name} failed:`, error);
                    errors[name] = error.message;
                });

            promises.push(promise);
        }

        // Wait for all calls to complete
        await Promise.allSettled(promises);

        return { results, errors };
    }

    /**
     * Stream-like API call for long-running operations with progress tracking
     */
    async streamAPICall(methodName, payload, onProgress = null, onComplete = null) {
        try {
            console.log(`[VoiceAssistant] Starting stream API call: ${methodName}`);

            // Start the operation
            const initialResult = await this.makeAPICall(methodName, payload);

            if (!initialResult.success) {
                throw new Error(initialResult.error || 'Operation failed to start');
            }

            // If it completed immediately, call onComplete
            if (onComplete && (initialResult.is_complete || initialResult.completed)) {
                onComplete(initialResult);
                return initialResult;
            }

            // Start progress polling if handler provided
            if (onProgress && (methodName === 'StartVoiceService')) {
                this.startProgressPolling(onProgress, onComplete);
            }

            return initialResult;

        } catch (error) {
            console.error(`[VoiceAssistant] Stream API call ${methodName} failed:`, error);
            throw error;
        }
    }

    /**
     * Start progress polling for installation/long operations
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

                    // Stop polling if complete or error
                    if (progressResult.is_complete || progressResult.has_error) {
                        clearInterval(pollInterval);
                        this.progressPollingActive = false;

                        if (onComplete) {
                            onComplete(progressResult);
                        }
                    }
                } else {
                    // If progress polling fails, stop it
                    console.warn('[VoiceAssistant] Progress polling failed, stopping');
                    clearInterval(pollInterval);
                    this.progressPollingActive = false;
                }
            } catch (error) {
                console.warn('[VoiceAssistant] Progress polling error:', error);
                // Continue polling despite errors unless it's a critical failure
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

        // Check if it looks like base64
        if (!/^[A-Za-z0-9+/]*={0,2}$/.test(audioData)) {
            throw new Error('Invalid audio data: not valid base64');
        }

        // Check size (approximate)
        const sizeBytes = (audioData.length * 3) / 4;
        const maxSize = 50 * 1024 * 1024; // 50MB

        if (sizeBytes > maxSize) {
            throw new Error(`Audio data too large: ${Math.round(sizeBytes / 1024 / 1024)}MB (max 50MB)`);
        }

        return true;
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
     * Enhanced API call with validation
     */
    async processVoiceInputValidated(audioData, language = 'en-US', voice = 'default', volume = 0.8) {
        // Validate inputs
        this.validateAudioData(audioData);

        const payload = {
            session_id: `voice-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
            audio_data: audioData,
            language: language,
            voice: voice,
            volume: volume
        };

        return await this.processVoiceInput(payload);
    }

    /**
     * Enhanced text command with validation
     */
    async processTextCommandValidated(text, voice = 'default', language = 'en-US', volume = 0.8) {
        // Validate inputs
        this.validateTextInput(text);

        const payload = {
            session_id: `text-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
            text: text,
            voice: voice,
            language: language,
            volume: volume
        };

        return await this.processTextCommand(payload);
    }

    /**
     * Get API call statistics for monitoring
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
            this.apiTimeout = Math.max(5000, Math.min(300000, options.timeout)); // 5s to 5min
        }

        if (options.retryAttempts !== undefined) {
            this.retryAttempts = Math.max(1, Math.min(10, options.retryAttempts)); // 1 to 10
        }

        if (options.retryDelay) {
            this.retryDelay = Math.max(100, Math.min(10000, options.retryDelay)); // 100ms to 10s
        }

        console.log('[VoiceAssistant] API configuration updated:', {
            timeout: this.apiTimeout,
            retryAttempts: this.retryAttempts,
            retryDelay: this.retryDelay
        });
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
            // Test basic connectivity
            const startTime = Date.now();
            const statusResponse = await this.getVoiceStatus();
            const latency = Date.now() - startTime;

            results.latency = latency;
            results.status = statusResponse.success;

            if (statusResponse.success) {
                results.service_running = statusResponse.backend_running || false;
                results.service_healthy = statusResponse.backend_healthy || false;

                // Test dependency status
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
     * Emergency stop for all ongoing operations
     */
    emergencyStop() {
        try {
            console.log('[VoiceAssistant] Emergency stop triggered');

            // Stop progress polling
            this.stopProgressPolling();

            // TODO: Add API call to stop any ongoing processing
            // this.makeAPICall('EmergencyStop', {}).catch(() => {});

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
