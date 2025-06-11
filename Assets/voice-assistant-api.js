/**
 * Voice Assistant API Module
 * Handles all communication with the SwarmUI backend API.
 * Provides a clean interface for voice processing operations.
 */

class VoiceAssistantAPI {
    constructor() {
        this.apiTimeout = 45000; // 45 seconds
        this.retryAttempts = 3;
        this.retryDelay = 1000; // 1 second

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
     * Process voice input
     */
    async processVoiceInput(payload) {
        return await this.makeAPICall('ProcessVoiceInput', payload);
    }

    /**
     * Process text command
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
     * Make API call using SwarmUI's genericRequest with retry logic
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

                    if (attempt < this.retryAttempts) {
                        console.log(`[VoiceAssistant] Retrying ${methodName} (attempt ${attempt + 1})`);
                        setTimeout(() => {
                            this.makeAPICall(methodName, payload, attempt + 1)
                                .then(resolve)
                                .catch(reject);
                        }, this.retryDelay * attempt);
                    } else {
                        reject(new Error(`API call ${methodName} timed out after ${this.retryAttempts} attempts`));
                    }
                }, this.apiTimeout);

                genericRequest(methodName, payload, (data) => {
                    clearTimeout(timeoutId);
                    const duration = Date.now() - startTime;

                    console.log(`[VoiceAssistant] API response for ${methodName} (${duration}ms):`, data);

                    // Validate response
                    if (this.isValidResponse(data)) {
                        resolve(data);
                    } else {
                        const error = new Error(`Invalid response from ${methodName}: ${JSON.stringify(data)}`);

                        if (attempt < this.retryAttempts && this.shouldRetry(data)) {
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

                    if (attempt < this.retryAttempts && this.shouldRetryError(error)) {
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
     * Validate API response structure
     */
    isValidResponse(data) {
        // Check if response has required structure
        return data && typeof data === 'object' && data.hasOwnProperty('success');
    }

    /**
     * Determine if a response should trigger a retry
     */
    shouldRetry(data) {
        // Don't retry if we got a valid response structure but it indicates failure
        if (data && data.success === false) {
            // Only retry for specific error types
            const error = data.error || '';
            return error.includes('timeout') ||
                error.includes('network') ||
                error.includes('connection') ||
                error.includes('service not available');
        }

        // Retry for invalid response structures
        return true;
    }

    /**
     * Determine if an error should trigger a retry
     */
    shouldRetryError(error) {
        const errorMessage = (error.message || error || '').toLowerCase();

        // Retry for network-related errors
        return errorMessage.includes('network') ||
            errorMessage.includes('timeout') ||
            errorMessage.includes('connection') ||
            errorMessage.includes('fetch') ||
            errorMessage.includes('xhr');
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
     * Batch API calls with error handling
     */
    async batchAPICall(calls) {
        const results = {};
        const errors = {};

        for (const [name, { method, payload }] of Object.entries(calls)) {
            try {
                results[name] = await this.makeAPICall(method, payload);
            } catch (error) {
                console.error(`[VoiceAssistant] Batch call ${name} failed:`, error);
                errors[name] = error.message;
            }
        }

        return { results, errors };
    }

    /**
     * Stream-like API call for long-running operations
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

            // If we have progress tracking, start polling
            if (onProgress) {
                const pollInterval = setInterval(async () => {
                    try {
                        const progressResult = await this.makeAPICall('GetInstallationProgress', {});

                        if (progressResult.success) {
                            onProgress(progressResult);

                            // Stop polling if complete
                            if (progressResult.is_complete || progressResult.has_error) {
                                clearInterval(pollInterval);
                                if (onComplete) {
                                    onComplete(progressResult);
                                }
                            }
                        }
                    } catch (error) {
                        console.warn('[VoiceAssistant] Progress polling error:', error);
                        // Don't stop polling for minor errors
                    }
                }, 1000);

                // Auto-stop polling after 30 minutes
                setTimeout(() => {
                    clearInterval(pollInterval);
                    console.warn('[VoiceAssistant] Progress polling timed out');
                }, 30 * 60 * 1000);
            }

            return initialResult;

        } catch (error) {
            console.error(`[VoiceAssistant] Stream API call ${methodName} failed:`, error);
            throw error;
        }
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
     * Get API call statistics
     */
    getStatistics() {
        // This could be enhanced to track call counts, response times, etc.
        return {
            apiTimeout: this.apiTimeout,
            retryAttempts: this.retryAttempts,
            retryDelay: this.retryDelay
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
     * Test API connectivity
     */
    async testConnectivity() {
        console.log('[VoiceAssistant] Testing API connectivity...');

        const results = {
            status: false,
            latency: null,
            error: null
        };

        try {
            const startTime = Date.now();
            const response = await this.getVoiceStatus();
            const latency = Date.now() - startTime;

            results.status = response.success;
            results.latency = latency;

            if (response.success) {
                console.log(`[VoiceAssistant] API connectivity test passed (${latency}ms)`);
            } else {
                results.error = response.error || 'Unknown error';
                console.warn(`[VoiceAssistant] API connectivity test failed: ${results.error}`);
            }
        } catch (error) {
            results.error = error.message;
            console.error('[VoiceAssistant] API connectivity test error:', error);
        }

        return results;
    }
}

// Export for global access
window.VoiceAssistantAPI = VoiceAssistantAPI;
