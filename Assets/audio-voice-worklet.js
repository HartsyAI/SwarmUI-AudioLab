/**
 * AudioWorkletProcessor that turns the microphone's Float32 render quanta (fixed at 128 samples by the Web
 * Audio spec) into fixed-size PCM16 little-endian frames, posted back to the main thread as transferable
 * ArrayBuffers. Runs on the audio rendering thread, not the main thread -- the only place Web Audio lets a
 * script see raw samples without blocking UI work.
 *
 * Loaded via audioContext.audioWorklet.addModule(...); never referenced as a <script> tag (see audio-voice.js's
 * own derivation of this file's URL from its own <script src>, same technique audio-daw-score.js uses for its
 * soundfont path).
 */
class VoiceCaptureProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const frameSize = (options && options.processorOptions && options.processorOptions.frameSize) || 960;
        this._frame = new Float32Array(frameSize);
        this._offset = 0;
    }

    process(inputs) {
        const input = inputs[0];
        const channel = input && input[0];
        if (!channel || channel.length === 0) {
            return true; // keep the node alive even through a silent/disconnected render quantum
        }
        for (let i = 0; i < channel.length; i++) {
            this._frame[this._offset++] = channel[i];
            if (this._offset >= this._frame.length) {
                const pcm16 = new Int16Array(this._frame.length);
                for (let j = 0; j < this._frame.length; j++) {
                    const sample = Math.max(-1, Math.min(1, this._frame[j]));
                    pcm16[j] = sample < 0 ? sample * 32768 : sample * 32767;
                }
                this.port.postMessage(pcm16.buffer, [pcm16.buffer]);
                this._offset = 0;
            }
        }
        return true;
    }
}

registerProcessor('voice-capture-processor', VoiceCaptureProcessor);
