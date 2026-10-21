/**
 * DAW capture processor: records the input between two absolute frames of the AudioContext clock, so a take
 * lines up sample-accurately with the transport (MediaRecorder starts "whenever" and encodes lossy Opus).
 * Loaded with audioContext.audioWorklet.addModule(); runs on the audio thread.
 *
 * Messages in:  {type:'start', atFrame}  begin capturing at that frame (or now)
 *               {type:'stop', atFrame}   stop at that frame (or now); a 'done' follows once it has passed
 * Messages out: {type:'data', frame, channels:[Float32Array...]}  captured audio, in order
 *               {type:'peak', value}  input peak (0..1), ~30 times a second, always (drives input meters)
 *               {type:'done', frame}  capture finished; frame = first frame captured
 */
class DawRecorderProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.channelCount = Math.max(1, (options.processorOptions && options.processorOptions.channels) || 2);
        this.startFrame = -1;
        this.stopFrame = Infinity;
        this.firstFrame = -1;
        this.capturing = false;
        this.pending = null;
        this.pendingFrames = 0;
        this.pendingStart = 0;
        this.peak = 0;
        this.peakBlocks = 0;
        this.port.onmessage = (e) => {
            const m = e.data;
            if (m.type === 'start') {
                this.startFrame = Number.isFinite(m.atFrame) ? m.atFrame : currentFrame;
                this.stopFrame = Infinity;
                this.firstFrame = -1;
                this.capturing = true;
            }
            else if (m.type === 'stop') {
                this.stopFrame = Number.isFinite(m.atFrame) ? Math.max(m.atFrame, currentFrame) : currentFrame;
            }
        };
    }

    flush() {
        if (!this.pending || !this.pendingFrames) return;
        const channels = this.pending.map(c => c.slice(0, this.pendingFrames));
        this.port.postMessage({ type: 'data', frame: this.pendingStart, channels }, channels.map(c => c.buffer));
        this.pending = null;
        this.pendingFrames = 0;
    }

    process(inputs) {
        const input = inputs[0] || [];
        const n = input[0] ? input[0].length : 128;
        const blockStart = currentFrame;
        let peak = 0;
        for (const ch of input) {
            for (let i = 0; i < ch.length; i++) {
                const v = ch[i] < 0 ? -ch[i] : ch[i];
                if (v > peak) peak = v;
            }
        }
        if (peak > this.peak) this.peak = peak;
        if (++this.peakBlocks >= 12) {
            this.port.postMessage({ type: 'peak', value: this.peak });
            this.peak = 0;
            this.peakBlocks = 0;
        }
        if (!this.capturing) return true;
        const from = Math.max(0, this.startFrame - blockStart);
        const to = Math.min(n, this.stopFrame - blockStart);
        if (to > from) {
            if (this.firstFrame < 0) this.firstFrame = blockStart + from;
            if (!this.pending) {
                this.pending = [];
                for (let c = 0; c < this.channelCount; c++) this.pending.push(new Float32Array(16384));
                this.pendingStart = blockStart + from;
            }
            for (let c = 0; c < this.channelCount; c++) {
                // A mono source feeding a stereo capture repeats its only channel; silence when nothing is connected.
                const src = input[c] || input[0];
                const dst = this.pending[c];
                for (let i = from; i < to; i++) dst[this.pendingFrames + i - from] = src ? src[i] : 0;
            }
            this.pendingFrames += to - from;
            if (this.pendingFrames + 128 > 16384) this.flush();
        }
        if (blockStart + n >= this.stopFrame) {
            this.flush();
            this.port.postMessage({ type: 'done', frame: this.firstFrame });
            this.capturing = false;
            this.startFrame = -1;
        }
        return true;
    }
}

registerProcessor('daw-recorder', DawRecorderProcessor);
