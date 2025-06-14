/**
 * Voice Assistant API Module for SwarmUI
 * Handles all C# backend communication using SwarmUI's genericRequest pattern
 */

const VoiceAPI = (() => {
    'use strict';

    // Private API state
    let isInitialized = false;

    /**
     * Initialize the API module
     */
    function init() {
        if (isInitialized) return;
        isInitialized = true;
        console.log('[VoiceAPI] Initialized');
    }

    /**
     * Generic API call wrapper using SwarmUI's genericRequest
     * @param {string} endpoint - The API endpoint name
     * @param {Object} data - Request data
     * @returns {Promise<Object>} API response
     */
    async function callAPI(endpoint, data = {}) {
        return new Promise((resolve, reject) => {
            genericRequest(endpoint, data, (response) => {
                if (response.success) {
                    resolve(response);
                } else {
                    const error = new Error(response.error || `API call ${endpoint} failed`);
                    error.response = response;
                    reject(error);
                }
            }, (error) => {
                reject(new Error(`Network error calling ${endpoint}: ${error}`));
            });
        });
    }

    // ===== SERVICE MANAGEMENT API =====

    /**
     * Get voice service status
     * @returns {Promise<Object>} Service status information
     */
    async function getServiceStatus() {
        return await callAPI('GetVoiceServiceStatus');
    }

    /**
     * Start the voice service
     * @returns {Promise<Object>} Start operation result
     */
    async function startService() {
        return await callAPI('StartVoiceService');
    }

    /**
     * Stop the voice service
     * @returns {Promise<Object>} Stop operation result
     */
    async function stopService() {
        return await callAPI('StopVoiceService');
    }

    /**
     * Get dependency installation status
     * @returns {Promise<Object>} Dependency status information
     */
    async function getDependencyStatus() {
        return await callAPI('GetVoiceDependencyStatus');
    }

    /**
     * Install voice dependencies
     * @returns {Promise<Object>} Installation start result
     */
    async function installDependencies() {
        return await callAPI('InstallVoiceDependencies');
    }

    /**
     * Get installation progress
     * @returns {Promise<Object>} Installation progress information
     */
    async function getInstallationProgress() {
        return await callAPI('GetVoiceInstallationProgress');
    }

    // ===== VOICE PROCESSING API =====

    /**
     * Process Speech-to-Text
     * @param {string} audioData - Base64 encoded audio data
     * @param {Object} options - STT options (language, etc.)
     * @returns {Promise<Object>} STT result with transcription
     */
    async function processSTT(audioData, options = {}) {
        const data = {
            audio_data: audioData,
            language: options.language || 'en-US',
            return_confidence: options.returnConfidence !== false
        };
        return await callAPI('ProcessSpeechToText', data);
    }

    /**
     * Process Text-to-Speech
     * @param {string} text - Text to convert to speech
     * @param {Object} options - TTS options (voice, volume, etc.)
     * @returns {Promise<Object>} TTS result with audio data
     */
    async function processTTS(text, options = {}) {
        const data = {
            text: text,
            voice: options.voice || 'default',
            volume: options.volume || 0.8,
            language: options.language || 'en-US'
        };
        return await callAPI('ProcessTextToSpeech', data);
    }

    /**
     * Process Speech-to-Speech (STT + TTS pipeline)
     * @param {string} audioData - Base64 encoded audio data
     * @param {Object} options - STS options
     * @returns {Promise<Object>} STS result with transcription and audio response
     */
    async function processSTS(audioData, options = {}) {
        const data = {
            audio_data: audioData,
            stt_language: options.sttLanguage || 'en-US',
            tts_voice: options.ttsVoice || 'default',
            tts_volume: options.ttsVolume || 0.8,
            tts_language: options.ttsLanguage || 'en-US'
        };
        return await callAPI('ProcessSpeechToSpeech', data);
    }

    // ===== UTILITY FUNCTIONS =====

    /**
     * Test API connectivity
     * @returns {Promise<boolean>} True if API is responding
     */
    async function testConnectivity() {
        try {
            await getServiceStatus();
            return true;
        } catch (error) {
            console.warn('[VoiceAPI] Connectivity test failed:', error.message);
            return false;
        }
    }

    /**
     * Validate audio data before sending
     * @param {string} audioData - Base64 audio data to validate
     * @throws {Error} If audio data is invalid
     */
    function validateAudioData(audioData) {
        if (!audioData || typeof audioData !== 'string') {
            throw new Error('Audio data must be a non-empty string');
        }

        if (!/^[A-Za-z0-9+/]*={0,2}$/.test(audioData)) {
            throw new Error('Audio data must be valid base64');
        }

        const sizeBytes = (audioData.length * 3) / 4;
        const maxSize = 50 * 1024 * 1024; // 50MB limit

        if (sizeBytes > maxSize) {
            throw new Error(`Audio data too large: ${Math.round(sizeBytes / 1024 / 1024)}MB (max 50MB)`);
        }
    }

    /**
     * Validate text input
     * @param {string} text - Text to validate
     * @throws {Error} If text is invalid
     */
    function validateText(text) {
        if (!text || typeof text !== 'string') {
            throw new Error('Text must be a non-empty string');
        }

        const trimmed = text.trim();
        if (trimmed.length === 0) {
            throw new Error('Text cannot be empty');
        }

        if (trimmed.length > 1000) {
            throw new Error('Text too long: maximum 1000 characters');
        }
    }

    // Public API
    return {
        init,

        // Service Management
        getServiceStatus,
        startService,
        stopService,
        getDependencyStatus,
        installDependencies,
        getInstallationProgress,

        // Voice Processing
        processSTT,
        processTTS,
        processSTS,

        // Utilities
        testConnectivity,
        validateAudioData,
        validateText
    };
})();

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', VoiceAPI.init);
} else {
    VoiceAPI.init();
}
