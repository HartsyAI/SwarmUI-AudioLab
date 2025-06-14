/**
 * Voice Assistant Core Module for SwarmUI
 * Handles audio recording, playback, and core voice processing logic
 */

const VoiceCore = (() => {
    'use strict';

    // Private state
    let isInitialized = false;
    let mediaRecorder = null;
    let audioChunks = [];
    let currentAudio = null;
    let recordingStream = null;
    let recordingTimeout = null;

    // Configuration
    const config = {
        maxRecordingDuration: 30000, // 30 seconds
        audioConstraints: {
            audio: {
                sampleRate: 16000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true
            }
        }
    };

    // Browser capability detection
    const capabilities = {
        hasMediaDevices: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
        hasMediaRecorder: !!window.MediaRecorder,
        hasAudioContext: !!(window.AudioContext || window.webkitAudioContext)
    };

    /**
     * Initialize the core module
     */
    function init() {
        if (isInitialized) return;

        checkBrowserSupport();
        isInitialized = true;
        console.log('[VoiceCore] Initialized with capabilities:', capabilities);
    }

    /**
     * Check browser support for voice features
     */
    function checkBrowserSupport() {
        if (!capabilities.hasMediaDevices) {
            console.warn('[VoiceCore] MediaDevices not available - voice recording disabled');
        }
        if (!capabilities.hasMediaRecorder) {
            console.warn('[VoiceCore] MediaRecorder not available - recording may not work');
        }
        if (!capabilities.hasAudioContext) {
            console.warn('[VoiceCore] AudioContext not available - audio processing limited');
        }
    }

    /**
     * Check if recording is supported in current browser/context
     * @returns {boolean} True if recording is supported
     */
    function isRecordingSupported() {
        return capabilities.hasMediaDevices && capabilities.hasMediaRecorder;
    }

    /**
     * Get best supported MIME type for recording
     * @returns {string} Best MIME type for MediaRecorder
     */
    function getBestMimeType() {
        const types = [
            'audio/webm;codecs=opus',
            'audio/webm',
            'audio/wav',
            'audio/mp4'
        ];

        for (const type of types) {
            if (MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(type)) {
                return type;
            }
        }

        return 'audio/wav'; // fallback
    }

    /**
     * Convert blob to base64 string
     * @param {Blob} blob - Audio blob to convert
     * @returns {Promise<string>} Base64 encoded audio data (without data: prefix)
     */
    async function blobToBase64(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => {
                // Remove data:audio/wav;base64, prefix
                const base64 = reader.result.split(',')[1];
                resolve(base64);
            };
            reader.onerror = reject;
            reader.readAsDataURL(blob);
        });
    }

    /**
     * Start audio recording
     * @returns {Promise<void>}
     * @throws {Error} If recording cannot be started
     */
    async function startRecording() {
        if (!isRecordingSupported()) {
            throw new Error('Audio recording not supported in this browser/context');
        }

        if (mediaRecorder && mediaRecorder.state === 'recording') {
            throw new Error('Recording already in progress');
        }

        try {
            // Request microphone access
            recordingStream = await navigator.mediaDevices.getUserMedia(config.audioConstraints);

            // Set up MediaRecorder
            const mimeType = getBestMimeType();
            mediaRecorder = new MediaRecorder(recordingStream, { mimeType });
            audioChunks = [];

            // Set up event handlers
            mediaRecorder.ondataavailable = (event) => {
                if (event.data.size > 0) {
                    audioChunks.push(event.data);
                }
            };

            mediaRecorder.onerror = (event) => {
                console.error('[VoiceCore] MediaRecorder error:', event.error);
                stopRecording();
                throw new Error(`Recording error: ${event.error.message}`);
            };

            // Start recording
            mediaRecorder.start();

            // Set auto-stop timeout
            recordingTimeout = setTimeout(() => {
                if (mediaRecorder && mediaRecorder.state === 'recording') {
                    console.log('[VoiceCore] Auto-stopping recording after timeout');
                    stopRecording();
                }
            }, config.maxRecordingDuration);

            console.log('[VoiceCore] Recording started');
        } catch (error) {
            await cleanup();
            throw new Error(`Failed to start recording: ${error.message}`);
        }
    }

    /**
     * Stop audio recording and return recorded data
     * @returns {Promise<string>} Base64 encoded audio data
     */
    async function stopRecording() {
        if (!mediaRecorder || mediaRecorder.state === 'inactive') {
            throw new Error('No active recording to stop');
        }

        return new Promise((resolve, reject) => {
            const handleStop = async () => {
                try {
                    // Create audio blob from chunks
                    const mimeType = mediaRecorder.mimeType || getBestMimeType();
                    const audioBlob = new Blob(audioChunks, { type: mimeType });

                    // Convert to base64
                    const base64Audio = await blobToBase64(audioBlob);

                    // Validate size
                    VoiceAPI.validateAudioData(base64Audio);

                    await cleanup();
                    console.log('[VoiceCore] Recording stopped, audio size:', Math.round(audioBlob.size / 1024), 'KB');
                    resolve(base64Audio);
                } catch (error) {
                    await cleanup();
                    reject(error);
                }
            };

            mediaRecorder.onstop = handleStop;

            // Clear timeout
            if (recordingTimeout) {
                clearTimeout(recordingTimeout);
                recordingTimeout = null;
            }

            // Stop recording
            if (mediaRecorder.state === 'recording') {
                mediaRecorder.stop();
            } else {
                handleStop(); // Already stopped
            }
        });
    }

    /**
     * Check if currently recording
     * @returns {boolean} True if recording is active
     */
    function isRecording() {
        return mediaRecorder && mediaRecorder.state === 'recording';
    }

    /**
     * Clean up recording resources
     */
    async function cleanup() {
        // Clear timeout
        if (recordingTimeout) {
            clearTimeout(recordingTimeout);
            recordingTimeout = null;
        }

        // Stop media tracks
        if (recordingStream) {
            recordingStream.getTracks().forEach(track => track.stop());
            recordingStream = null;
        }

        // Reset recorder
        if (mediaRecorder) {
            if (mediaRecorder.state !== 'inactive') {
                mediaRecorder.stop();
            }
            mediaRecorder = null;
        }

        audioChunks = [];
    }

    /**
     * Play TTS audio from base64 data
     * @param {string} base64Audio - Base64 encoded audio data
     * @returns {Promise<void>} Resolves when audio finishes playing
     */
    async function playAudio(base64Audio) {
        try {
            // Stop any currently playing audio
            await stopAudio();

            // Create audio blob and URL
            const audioData = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));
            const audioBlob = new Blob([audioData], { type: 'audio/wav' });
            const audioUrl = URL.createObjectURL(audioBlob);

            // Create and configure audio element
            currentAudio = new Audio(audioUrl);

            return new Promise((resolve, reject) => {
                currentAudio.onended = () => {
                    URL.revokeObjectURL(audioUrl);
                    currentAudio = null;
                    resolve();
                };

                currentAudio.onerror = (error) => {
                    URL.revokeObjectURL(audioUrl);
                    currentAudio = null;
                    reject(new Error(`Audio playback error: ${error.message || 'Unknown error'}`));
                };

                currentAudio.play().catch(reject);
            });

        } catch (error) {
            throw new Error(`Failed to play audio: ${error.message}`);
        }
    }

    /**
     * Stop any currently playing audio
     */
    async function stopAudio() {
        if (currentAudio) {
            currentAudio.pause();
            currentAudio.currentTime = 0;
            currentAudio = null;
        }
    }

    /**
     * Check if audio is currently playing
     * @returns {boolean} True if audio is playing
     */
    function isPlaying() {
        return currentAudio && !currentAudio.paused;
    }

    /**
     * Process STT workflow
     * @param {Object} options - STT options
     * @returns {Promise<Object>} STT result
     */
    async function processSTTWorkflow(options = {}) {
        let audioData;

        try {
            // Start recording
            await startRecording();

            // Wait for user to stop or timeout
            // Note: In practice, this would be triggered by user action
            // For now, we return a promise that resolves when stopRecording is called

        } catch (error) {
            await cleanup();
            throw error;
        }
    }

    /**
     * Process TTS workflow
     * @param {string} text - Text to speak
     * @param {Object} options - TTS options
     * @returns {Promise<void>}
     */
    async function processTTSWorkflow(text, options = {}) {
        try {
            // Validate input
            VoiceAPI.validateText(text);

            // Call TTS API
            const result = await VoiceAPI.processTTS(text, options);

            if (!result.audio_data) {
                throw new Error('No audio data received from TTS service');
            }

            // Play the audio
            await playAudio(result.audio_data);

            return result;
        } catch (error) {
            throw new Error(`TTS workflow failed: ${error.message}`);
        }
    }

    /**
     * Process STS workflow
     * @param {Object} options - STS options  
     * @returns {Promise<Object>} STS result
     */
    async function processSTSWorkflow(options = {}) {
        let audioData;

        try {
            // Record audio
            await startRecording();
            // Note: stopRecording would be called by UI when user stops

        } catch (error) {
            await cleanup();
            throw error;
        }
    }

    /**
     * Emergency stop all voice operations
     */
    async function emergencyStop() {
        try {
            await cleanup();
            await stopAudio();
            console.log('[VoiceCore] Emergency stop completed');
        } catch (error) {
            console.error('[VoiceCore] Error during emergency stop:', error);
        }
    }

    /**
     * Get current voice core status
     * @returns {Object} Status information
     */
    function getStatus() {
        return {
            initialized: isInitialized,
            capabilities,
            isRecording: isRecording(),
            isPlaying: isPlaying(),
            recordingSupported: isRecordingSupported()
        };
    }

    // Public API
    return {
        init,

        // Capabilities
        isRecordingSupported,
        getStatus,

        // Recording
        startRecording,
        stopRecording,
        isRecording,

        // Playback
        playAudio,
        stopAudio,
        isPlaying,

        // Workflows
        processTTSWorkflow,

        // Utilities
        emergencyStop,
        cleanup
    };
})();

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', VoiceCore.init);
} else {
    VoiceCore.init();
}
