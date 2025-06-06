// Voice Assistant Frontend for SwarmUI
// This script provides the UI and logic for the voice assistant functionality

class VoiceAssistant {
    constructor() {
        this.isRecording = false;
        this.mediaRecorder = null;
        this.audioChunks = [];
        this.audioContext = null;
        this.sessionId = this.generateSessionId();
        this.voiceEnabled = false;
        
        // UI Elements
        this.ui = {
            container: null,
            button: null,
            status: null,
            transcription: null,
            error: null
        };
        
        this.initializeUI();
        this.initializeEventListeners();
        
        // Check voice status on load
        this.checkVoiceStatus();
    }
    
    // Generate a unique session ID
    generateSessionId() {
        return 'va-' + Math.random().toString(36).substr(2, 9);
    }
    
    // Initialize the UI elements
    initializeUI() {
        // Create container
        this.ui.container = document.createElement('div');
        this.ui.container.className = 'voice-assistant-container';
        
        // Create button
        this.ui.button = document.createElement('button');
        this.ui.button.id = 'voiceAssistantButton';
        this.ui.button.className = 'voice-assistant-button';
        this.ui.button.innerHTML = '🎤 Start Voice';
        
        // Create status indicator
        this.ui.status = document.createElement('div');
        this.ui.status.className = 'voice-status';
        this.ui.status.textContent = 'Voice Assistant is ready';
        
        // Create transcription display
        this.ui.transcription = document.createElement('div');
        this.ui.transcription.className = 'voice-transcription';
        
        // Create error display
        this.ui.error = document.createElement('div');
        this.ui.error.className = 'voice-error';
        
        // Assemble UI
        this.ui.container.appendChild(this.ui.button);
        this.ui.container.appendChild(this.ui.status);
        this.ui.container.appendChild(this.ui.transcription);
        this.ui.container.appendChild(this.ui.error);
        
        // Add to page
        const targetElement = document.querySelector('.gradio-container') || document.body;
        targetElement.appendChild(this.ui.container);
    }
    
    // Initialize event listeners
    initializeEventListeners() {
        this.ui.button.addEventListener('click', () => {
            if (this.isRecording) {
                this.stopRecording();
            } else {
                this.startRecording();
            }
        });
    }
    
    // Check voice service status
    async checkVoiceStatus() {
        try {
            const response = await fetch('/API/voice_status');
            const data = await response.json();
            
            if (data.success) {
                this.voiceEnabled = data.backend_running && data.backend_healthy;
                this.updateUIStatus();
            }
        } catch (error) {
            console.error('Error checking voice status:', error);
            this.showError('Failed to connect to voice service');
        }
    }
    
    // Update UI based on current state
    updateUIStatus() {
        if (!this.voiceEnabled) {
            this.ui.status.textContent = 'Voice service not available';
            this.ui.button.disabled = true;
            return;
        }
        
        if (this.isRecording) {
            this.ui.button.innerHTML = '⏹ Stop Recording';
            this.ui.status.textContent = 'Listening...';
            this.ui.button.classList.add('recording');
        } else {
            this.ui.button.innerHTML = '🎤 Start Voice';
            this.ui.status.textContent = 'Ready';
            this.ui.button.classList.remove('recording');
        }
    }
    
    // Start recording audio
    async startRecording() {
        if (!this.voiceEnabled) {
            this.showError('Voice service is not available');
            return;
        }
        
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            this.mediaRecorder = new MediaRecorder(stream);
            this.audioChunks = [];
            
            this.mediaRecorder.ondataavailable = (event) => {
                if (event.data.size > 0) {
                    this.audioChunks.push(event.data);
                }
            };
            
            this.mediaRecorder.onstop = async () => {
                const audioBlob = new Blob(this.audioChunks, { type: 'audio/wav' });
                await this.processAudio(audioBlob);
                
                // Stop all tracks in the stream
                stream.getTracks().forEach(track => track.stop());
            };
            
            // Start recording
            this.mediaRecorder.start();
            this.isRecording = true;
            this.updateUIStatus();
            
            // Auto-stop after 30 seconds of silence
            this.autoStopTimeout = setTimeout(() => {
                if (this.isRecording) {
                    this.stopRecording();
                }
            }, 30000);
            
        } catch (error) {
            console.error('Error starting recording:', error);
            this.showError('Could not access microphone');
            this.isRecording = false;
            this.updateUIStatus();
        }
    }
    
    // Stop recording
    stopRecording() {
        if (this.mediaRecorder && this.isRecording) {
            clearTimeout(this.autoStopTimeout);
            
            try {
                if (this.mediaRecorder.state !== 'inactive') {
                    this.mediaRecorder.stop();
                }
                this.isRecording = false;
                this.updateUIStatus();
            } catch (error) {
                console.error('Error stopping recording:', error);
                this.showError('Error stopping recording');
            }
        }
    }
    
    // Process recorded audio
    async processAudio(audioBlob) {
        this.ui.status.textContent = 'Processing...';
        this.ui.transcription.textContent = '';
        
        try {
            // Convert blob to base64
            const base64Audio = await this.blobToBase64(audioBlob);
            
            // Call the API
            const response = await fetch('/API/process_voice_input', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    session_id: this.sessionId,
                    audio_data: base64Audio.split(',')[1] // Remove data URL prefix
                })
            });
            
            const result = await response.json();
            
            if (result.success) {
                // Update UI with transcription
                if (result.transcription) {
                    this.ui.transcription.textContent = `You said: ${result.transcription}`;
                }
                
                // Play TTS response if available
                if (result.audio_response) {
                    await this.playAudioResponse(result.audio_response);
                }
                
                this.ui.status.textContent = 'Done';
            } else {
                this.showError(result.error || 'Failed to process voice input');
            }
            
        } catch (error) {
            console.error('Error processing audio:', error);
            this.showError('Failed to process audio');
        }
    }
    
    // Play audio response from TTS
    async playAudioResponse(base64Audio) {
        try {
            // Create audio context if it doesn't exist
            if (!this.audioContext) {
                const AudioContext = window.AudioContext || window.webkitAudioContext;
                this.audioContext = new AudioContext();
            }
            
            // Decode base64 audio
            const audioData = Uint8Array.from(atob(base64Audio), c => c.charCodeAt(0));
            const audioBuffer = await this.audioContext.decodeAudioData(audioData.buffer);
            
            // Create audio source and connect to output
            const source = this.audioContext.createBufferSource();
            source.buffer = audioBuffer;
            source.connect(this.audioContext.destination);
            
            // Play the audio
            source.start(0);
            
        } catch (error) {
            console.error('Error playing audio response:', error);
        }
    }
    
    // Convert blob to base64
    blobToBase64(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result);
            reader.onerror = reject;
            reader.readAsDataURL(blob);
        });
    }
    
    // Show error message
    showError(message) {
        this.ui.error.textContent = message;
        setTimeout(() => {
            this.ui.error.textContent = '';
        }, 5000);
    }
}

// Initialize when the page loads
document.addEventListener('DOMContentLoaded', () => {
    // Check if we're in a compatible browser
    if (!navigator.mediaDevices || !window.MediaRecorder) {
        console.error('Voice Assistant requires a browser with MediaRecorder support');
        return;
    }
    
    // Initialize the voice assistant
    window.voiceAssistant = new VoiceAssistant();
});
