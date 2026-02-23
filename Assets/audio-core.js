/**
 * AudioLab Core Module for SwarmUI
 * Handles audio recording, playback, and browser capability detection
 */
const AudioLabCore = (() => {
    'use strict';

    let mediaRecorder = null;
    let audioChunks = [];
    let currentAudio = null;
    let recordingStream = null;
    let recordingTimeout = null;

    const config = {
        maxRecordingDuration: 30000,
        audioConstraints: {
            audio: {
                sampleRate: 16000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true
            }
        }
    };

    const capabilities = {
        hasMediaDevices: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
        hasMediaRecorder: !!window.MediaRecorder,
        hasAudioContext: !!(window.AudioContext || window.webkitAudioContext)
    };

    function isRecordingSupported() {
        return capabilities.hasMediaDevices && capabilities.hasMediaRecorder;
    }

    function getBestMimeType() {
        const types = ['audio/webm;codecs=opus', 'audio/webm', 'audio/wav', 'audio/mp4'];
        for (const type of types) {
            if (MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(type)) return type;
        }
        return 'audio/wav';
    }

    async function blobToBase64(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result.split(',')[1]);
            reader.onerror = reject;
            reader.readAsDataURL(blob);
        });
    }

    async function fileToBase64(file) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result.split(',')[1]);
            reader.onerror = reject;
            reader.readAsDataURL(file);
        });
    }

    async function startRecording() {
        if (!isRecordingSupported()) throw new Error('Audio recording not supported');
        if (mediaRecorder && mediaRecorder.state === 'recording') throw new Error('Already recording');

        recordingStream = await navigator.mediaDevices.getUserMedia(config.audioConstraints);
        const mimeType = getBestMimeType();
        mediaRecorder = new MediaRecorder(recordingStream, { mimeType });
        audioChunks = [];

        mediaRecorder.ondataavailable = (e) => { if (e.data.size > 0) audioChunks.push(e.data); };
        mediaRecorder.onerror = (e) => { console.error('[AudioLabCore] Recording error:', e.error); cleanup(); };
        mediaRecorder.start();

        recordingTimeout = setTimeout(() => {
            if (mediaRecorder && mediaRecorder.state === 'recording') stopRecording();
        }, config.maxRecordingDuration);
    }

    async function stopRecording() {
        if (!mediaRecorder || mediaRecorder.state === 'inactive') throw new Error('No active recording');

        return new Promise((resolve, reject) => {
            mediaRecorder.onstop = async () => {
                try {
                    const blob = new Blob(audioChunks, { type: mediaRecorder.mimeType || getBestMimeType() });
                    const b64 = await blobToBase64(blob);
                    AudioLabAPI.validateAudioData(b64);
                    await cleanup();
                    resolve(b64);
                } catch (err) {
                    await cleanup();
                    reject(err);
                }
            };
            if (recordingTimeout) { clearTimeout(recordingTimeout); recordingTimeout = null; }
            if (mediaRecorder.state === 'recording') mediaRecorder.stop();
        });
    }

    function isRecording() {
        return mediaRecorder && mediaRecorder.state === 'recording';
    }

    async function cleanup() {
        if (recordingTimeout) { clearTimeout(recordingTimeout); recordingTimeout = null; }
        if (recordingStream) { recordingStream.getTracks().forEach(t => t.stop()); recordingStream = null; }
        if (mediaRecorder && mediaRecorder.state !== 'inactive') mediaRecorder.stop();
        mediaRecorder = null;
        audioChunks = [];
    }

    async function playAudio(base64Audio) {
        await stopAudio();
        const data = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));
        const blob = new Blob([data], { type: 'audio/wav' });
        const url = URL.createObjectURL(blob);
        currentAudio = new Audio(url);

        return new Promise((resolve, reject) => {
            currentAudio.onended = () => { URL.revokeObjectURL(url); currentAudio = null; resolve(); };
            currentAudio.onerror = (e) => { URL.revokeObjectURL(url); currentAudio = null; reject(new Error('Playback error')); };
            currentAudio.play().catch(reject);
        });
    }

    /** Create an object URL from base64 audio data for use with <audio> controls */
    function createAudioURL(base64Audio) {
        const data = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));
        const blob = new Blob([data], { type: 'audio/wav' });
        return URL.createObjectURL(blob);
    }

    async function stopAudio() {
        if (currentAudio) { currentAudio.pause(); currentAudio.currentTime = 0; currentAudio = null; }
    }

    function isPlaying() {
        return currentAudio && !currentAudio.paused;
    }

    async function emergencyStop() {
        await cleanup();
        await stopAudio();
    }

    return {
        isRecordingSupported,
        startRecording,
        stopRecording,
        isRecording,
        playAudio,
        createAudioURL,
        stopAudio,
        isPlaying,
        fileToBase64,
        emergencyStop,
        cleanup,
        getStatus: () => ({
            capabilities,
            isRecording: isRecording(),
            isPlaying: isPlaying(),
            recordingSupported: isRecordingSupported()
        })
    };
})();
