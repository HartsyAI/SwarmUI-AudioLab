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

    /** @returns {Promise<Object>} Status of all registered providers. */
    async function getAllProvidersStatus() {
        return await callAPI('GetAllProvidersStatus');
    }

    /** @returns {Promise<Object>} Installation status for all providers. */
    async function getInstallationStatus() {
        return await callAPI('GetInstallationStatus');
    }

    /**
     * Install dependencies for a specific provider.
     * @param {string} providerId - e.g. "chatterbox_tts", "whisper_stt"
     */
    /**
     * Process audio through a specific provider.
     * @param {string} providerId
     * @param {Object} args
     */
    async function processAudio(providerId, args = {}) {
        return await callAPI('ProcessAudio', { provider_id: providerId, args });
    }

    /**
     * Process Speech-to-Text. Defaults handled by C# backend.
     * @param {string} audioData - base64 encoded audio
     * @param {Object} [options]
     * @param {string} [options.language]
     * @param {string} [options.providerId]
     * @param {string} [options.model] - model variant id (e.g. Whisper "turbo"); server default when omitted
     * @param {string} [options.task] - Whisper: "transcribe" or "translate"
     * @param {boolean} [options.wordTimestamps] - add `words: [{word, start, end}]` (seconds) when the model can time words
     * @param {string} [options.alignText] - time this known text instead of transcribing (implies wordTimestamps; one 30 s window)
     */
    async function processSTT(audioData, options = {}) {
        const payload = { audio_data: audioData };
        if (options.language) payload.language = options.language;
        if (options.providerId) payload.provider_id = options.providerId;
        if (options.model) payload.model = options.model;
        if (options.task) payload.task = options.task;
        if (options.wordTimestamps) payload.word_timestamps = true;
        if (options.alignText) payload.align_text = options.alignText;
        return await callAPI('ProcessSTT', payload);
    }

    /**
     * Time the words of a vocal clip. The model that transcribes and the model that times words are separate
     * choices: with `lyrics` only the aligner runs; without them the transcriber runs, and a transcriber that
     * cannot time words has its text timed by the aligner.
     * @param {string} audioData - base64 encoded vocal audio (at most one 30 s window when lyrics are given)
     * @param {Object} [options]
     * @param {string} [options.lyrics] - the words sung, when known
     * @param {string} [options.language]
     * @param {string} [options.sttProviderId] - transcriber provider (defaults to the aligner)
     * @param {string} [options.sttModel] - transcriber model variant
     * @param {string} [options.alignProviderId] - aligner provider (defaults to Whisper)
     * @param {string} [options.alignModel] - aligner model variant
     * @returns {Promise<{success: boolean, source: string, text: string, words: Array<{word: string, start: number, end: number}>, duration: number}>}
     */
    async function alignLyrics(audioData, options = {}) {
        const payload = { audio_data: audioData };
        if (options.lyrics) payload.lyrics = options.lyrics;
        if (options.language) payload.language = options.language;
        if (options.sttProviderId) payload.stt_provider_id = options.sttProviderId;
        if (options.sttModel) payload.stt_model = options.sttModel;
        if (options.alignProviderId) payload.align_provider_id = options.alignProviderId;
        if (options.alignModel) payload.align_model = options.alignModel;
        return await callAPI('AudioLabAlignLyrics', payload);
    }

    /**
     * Process Text-to-Speech. Defaults handled by C# backend.
     * @param {string} text
     * @param {Object} [options]
     * @param {string} [options.voice]
     * @param {string} [options.language]
     * @param {number} [options.volume]
     * @param {number} [options.speed]
     * @param {number} [options.pitch]
     * @param {string} [options.format]
     * @param {string} [options.providerId]
     */
    async function processTTS(text, options = {}) {
        const payload = { text };
        if (options.voice) payload.voice = options.voice;
        if (options.language) payload.language = options.language;
        if (options.volume !== undefined) payload.volume = options.volume;
        if (options.speed !== undefined || options.pitch !== undefined || options.format) {
            payload.options = {};
            if (options.speed !== undefined) payload.options.speed = options.speed;
            if (options.pitch !== undefined) payload.options.pitch = options.pitch;
            if (options.format) payload.options.format = options.format;
        }
        if (options.providerId) payload.provider_id = options.providerId;
        return await callAPI('ProcessTTS', payload);
    }

    /**
     * Process a chained workflow (e.g. STT → TTS pipeline).
     * @param {string} inputData
     * @param {string} inputType
     * @param {Array} steps
     */
    async function processWorkflow(inputData, inputType, steps) {
        return await callAPI('ProcessWorkflow', {
            input_data: inputData,
            input_type: inputType,
            workflow_type: 'custom',
            steps
        });
    }

    /**
     * Combine video with an audio track via ffmpeg.
     * @param {string} videoData - base64 encoded video
     * @param {string} audioData - base64 encoded audio
     * @param {string} [mode='replace'] - "replace" or "mix"
     */
    async function combineVideoAudio(videoData, audioData, mode = 'replace') {
        return await callAPI('CombineVideoAudio', {
            video_data: videoData,
            audio_data: audioData,
            mode
        });
    }

    /**
     * Extract audio track from a video file.
     * @param {string} videoData - base64 encoded video
     */
    async function extractAudioFromVideo(videoData) {
        return await callAPI('ExtractAudioFromVideo', { video_data: videoData });
    }

    /**
     * Client-side size guard to prevent sending oversized payloads.
     * @param {string} audioData - base64 encoded audio
     * @throws {Error} If data is invalid or exceeds 50MB
     */
    function validateAudioData(audioData) {
        if (!audioData || typeof audioData !== 'string') {
            throw new Error('Audio data must be a non-empty string');
        }
        const sizeBytes = (audioData.length * 3) / 4;
        if (sizeBytes > 50 * 1024 * 1024) {
            throw new Error(`Audio data too large: ${Math.round(sizeBytes / 1024 / 1024)}MB (max 50MB)`);
        }
    }

    return {
        callAPI,
        getAllProvidersStatus,
        getInstallationStatus,
        processAudio,
        processSTT,
        alignLyrics,
        processTTS,
        processWorkflow,
        combineVideoAudio,
        extractAudioFromVideo,
        validateAudioData
    };
})();
