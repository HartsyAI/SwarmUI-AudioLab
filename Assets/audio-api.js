/**
 * AudioLab API Module for SwarmUI
 * Handles all C# backend communication using SwarmUI's genericRequest pattern
 */
const AudioLabAPI = (() => {
    'use strict';

    /**
     * Generic API call wrapper using SwarmUI's genericRequest
     * @param {string} endpoint - API endpoint name (must match C# API.RegisterAPICall)
     * @param {Object} data - Request payload
     * @returns {Promise<Object>}
     */
    function callAPI(endpoint, data = {}) {
        return new Promise((resolve, reject) => {
            genericRequest(endpoint, data, (response) => {
                resolve(response);
            }, 0, (error) => {
                reject(new Error(`API ${endpoint}: ${error}`));
            });
        });
    }

    // ===== PROVIDER STATUS =====

    /** Get status of all registered providers.
     *  Maps to C# endpoint: GetAllProvidersStatus */
    async function getAllProvidersStatus() {
        return await callAPI('GetAllProvidersStatus');
    }

    /** Get installation status for all providers (checks which deps are installed).
     *  Maps to C# endpoint: GetInstallationStatus */
    async function getInstallationStatus() {
        return await callAPI('GetInstallationStatus');
    }

    // ===== INSTALLATION =====

    /** Install dependencies for a specific provider.
     *  Maps to C# endpoint: InstallProviderDependencies
     *  @param {string} providerId - e.g. "chatterbox_tts", "whisper_stt" */
    async function installProviderDependencies(providerId) {
        return await callAPI('InstallProviderDependencies', { provider_id: providerId });
    }

    /** Get real-time installation progress.
     *  Maps to C# endpoint: GetInstallationProgress */
    async function getInstallationProgress() {
        return await callAPI('GetInstallationProgress');
    }

    // ===== AUDIO PROCESSING =====

    /** Process audio through a specific provider (generic).
     *  Maps to C# endpoint: ProcessAudio
     *  @param {string} providerId
     *  @param {Object} args */
    async function processAudio(providerId, args = {}) {
        return await callAPI('ProcessAudio', { provider_id: providerId, args });
    }

    /** Process Speech-to-Text.
     *  Maps to C# endpoint: ProcessSTT
     *  @param {string} audioData - base64
     *  @param {Object} options - { language } */
    async function processSTT(audioData, options = {}) {
        return await callAPI('ProcessSTT', {
            audio_data: audioData,
            language: options.language || 'en-US',
            options: {
                return_confidence: options.returnConfidence !== false,
                return_alternatives: options.returnAlternatives || false,
                model_preference: options.modelPreference || 'accuracy'
            }
        });
    }

    /** Process Text-to-Speech.
     *  Maps to C# endpoint: ProcessTTS
     *  @param {string} text
     *  @param {Object} options - { voice, language, volume, speed, pitch, format } */
    async function processTTS(text, options = {}) {
        return await callAPI('ProcessTTS', {
            text: text,
            voice: options.voice || 'default',
            language: options.language || 'en-US',
            volume: options.volume ?? 0.8,
            options: {
                speed: options.speed ?? 1.0,
                pitch: options.pitch ?? 1.0,
                format: options.format || 'wav'
            }
        });
    }

    /** Process a chained workflow (STT -> TTS pipeline).
     *  Maps to C# endpoint: ProcessWorkflow */
    async function processWorkflow(inputData, inputType, steps) {
        return await callAPI('ProcessWorkflow', {
            input_data: inputData,
            input_type: inputType,
            workflow_type: 'custom',
            steps: steps
        });
    }

    // ===== VALIDATION =====

    function validateAudioData(audioData) {
        if (!audioData || typeof audioData !== 'string') {
            throw new Error('Audio data must be a non-empty string');
        }
        const sizeBytes = (audioData.length * 3) / 4;
        if (sizeBytes > 50 * 1024 * 1024) {
            throw new Error(`Audio data too large: ${Math.round(sizeBytes / 1024 / 1024)}MB (max 50MB)`);
        }
    }

    function validateText(text) {
        if (!text || typeof text !== 'string' || text.trim().length === 0) {
            throw new Error('Text cannot be empty');
        }
        if (text.trim().length > 1000) {
            throw new Error('Text too long: maximum 1000 characters');
        }
    }

    return {
        // Provider Status
        getAllProvidersStatus,
        getInstallationStatus,
        // Installation
        installProviderDependencies,
        getInstallationProgress,
        // Audio Processing
        processAudio,
        processSTT,
        processTTS,
        processWorkflow,
        // Validation
        validateAudioData,
        validateText
    };
})();
