/**
 * AudioDawInput: live audio inputs for the DAW (mics, interfaces, instruments plugged into one).
 * Opens one shared stream per device, routes a chosen channel layout to each track, and captures takes with an
 * AudioWorklet on the AudioContext clock, so a take lands sample-accurately against the transport.
 * ctx-agnostic helpers; the DAW owns tracks, the mixer graph and the transport.
 */
const AudioDawInput = (() => {
    'use strict';

    const WORKLET_URL = 'ExtensionFile/AudioLab/Assets/audio-daw-recorder-worklet.js';
    const workletLoads = new WeakMap(); // ctx -> Promise
    const streams = new Map(); // `${deviceId}|${voice}` -> { stream, refs, promise }

    /** What this browser/page can do. Microphone access needs a secure context (HTTPS or localhost). */
    function support() {
        return {
            mic: !!navigator.mediaDevices?.getUserMedia,
            worklet: typeof AudioWorkletNode !== 'undefined',
            secure: !!window.isSecureContext
        };
    }

    /** Audio input devices. Labels stay empty until the page has been granted microphone access once. */
    async function listDevices() {
        if (!navigator.mediaDevices?.enumerateDevices) return [];
        try {
            return (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audioinput');
        }
        catch (_) {
            return [];
        }
    }

    /** 'granted' | 'denied' | 'prompt' (Firefox has no microphone permission query, so it reports 'prompt'). */
    async function permissionState() {
        try {
            return (await navigator.permissions.query({ name: 'microphone' })).state;
        }
        catch (_) {
            return 'prompt';
        }
    }

    /** One-shot grant so device labels unlock; opens the default mic briefly and releases it. */
    async function requestAccess() {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        stream.getTracks().forEach(t => t.stop());
        return true;
    }

    /**
     * Open (or share) the stream for a device. Recording quality by default: no echo cancellation, noise
     * suppression or auto gain, which would color an instrument; voice mode turns them back on.
     * @returns {Promise<{stream: MediaStream, release: function()}>}
     */
    async function openStream(deviceId, { voiceMode = false } = {}) {
        const key = `${deviceId || 'default'}|${voiceMode ? 1 : 0}`;
        let entry = streams.get(key);
        if (!entry) {
            const constraints = {
                channelCount: { ideal: 2 },
                sampleRate: { ideal: 48000 },
                echoCancellation: voiceMode,
                noiseSuppression: voiceMode,
                autoGainControl: voiceMode
            };
            if (deviceId) constraints.deviceId = { exact: deviceId };
            entry = { stream: null, refs: 0, promise: navigator.mediaDevices.getUserMedia({ audio: constraints }) };
            streams.set(key, entry);
            try {
                entry.stream = await entry.promise;
            }
            catch (err) {
                streams.delete(key);
                throw err;
            }
        }
        else if (!entry.stream) {
            await entry.promise;
        }
        entry.refs++;
        let released = false;
        return {
            stream: entry.stream,
            release: () => {
                if (released) return;
                released = true;
                entry.refs--;
                if (entry.refs <= 0) {
                    entry.stream.getTracks().forEach(t => t.stop());
                    streams.delete(key);
                }
            }
        };
    }

    /** Channels the device delivers (1 for most mics, 2 for stereo interfaces). */
    function streamChannels(stream) {
        const settings = stream?.getAudioTracks()[0]?.getSettings() || {};
        return Math.max(1, settings.channelCount || 1);
    }

    /** Input latency the browser reports for the device, in seconds (Chrome fills this in; others may not). */
    function streamLatency(stream) {
        const settings = stream?.getAudioTracks()[0]?.getSettings() || {};
        return typeof settings.latency === 'number' && settings.latency >= 0 ? settings.latency : 0;
    }

    /** Device label for a stream, for display. */
    function streamLabel(stream) {
        return stream?.getAudioTracks()[0]?.label || '';
    }

    function ensureWorklet(ctx) {
        let p = workletLoads.get(ctx);
        if (!p) {
            p = ctx.audioWorklet.addModule(WORKLET_URL);
            workletLoads.set(ctx, p);
            p.catch(() => workletLoads.delete(ctx));
        }
        return p;
    }

    /**
     * Route a stream into the graph with a channel choice.
     * @param {string|number} channels - 'stereo', 'mono' (sum), or a 1-based channel number
     * @returns {{output: AudioNode, captureChannels: number, dispose: function()}} output carries the chosen
     *   signal (2 channels for stereo, 1 otherwise; a mono signal is panned centrally by the track's panner)
     */
    function createRoute(ctx, stream, channels) {
        const source = ctx.createMediaStreamSource(stream);
        const available = streamChannels(stream);
        const nodes = [source];
        let output;
        let captureChannels = 1;
        if (channels === 'stereo') {
            const splitter = ctx.createChannelSplitter(Math.max(2, available));
            const merger = ctx.createChannelMerger(2);
            source.connect(splitter);
            splitter.connect(merger, 0, 0);
            splitter.connect(merger, available > 1 ? 1 : 0, 1);
            nodes.push(splitter, merger);
            output = merger;
            captureChannels = 2;
        }
        else if (typeof channels === 'number' && channels >= 1) {
            const splitter = ctx.createChannelSplitter(Math.max(2, available));
            const pick = ctx.createGain();
            pick.channelCount = 1;
            pick.channelCountMode = 'explicit';
            source.connect(splitter);
            splitter.connect(pick, Math.min(channels, available) - 1);
            nodes.push(splitter, pick);
            output = pick;
        }
        else {
            // Mono sum: let the explicit 1-channel node down-mix every input channel
            const sum = ctx.createGain();
            sum.channelCount = 1;
            sum.channelCountMode = 'explicit';
            sum.channelInterpretation = 'speakers';
            source.connect(sum);
            nodes.push(sum);
            output = sum;
        }
        return {
            output,
            captureChannels,
            dispose: () => {
                for (const n of nodes) {
                    try { n.disconnect(); } catch (_) {}
                }
            }
        };
    }

    /**
     * A capture node fed from `source`. It always reports input peaks (input meters) and records only between
     * start and stop. Frames are absolute AudioContext frames, so the caller can align a take to the transport.
     * @returns {Promise<{start(atFrame), stop(atFrame): Promise<{channels: Float32Array[], firstFrame: number, sampleRate: number}>, onPeak: function, dispose()}>}
     */
    async function createCapture(ctx, source, channels) {
        await ensureWorklet(ctx);
        const node = new AudioWorkletNode(ctx, 'daw-recorder', {
            numberOfInputs: 1,
            numberOfOutputs: 1,
            outputChannelCount: [1],
            channelCount: channels,
            channelCountMode: 'explicit',
            channelInterpretation: 'discrete',
            processorOptions: { channels }
        });
        // Keep the node pulled by the graph: route its (silent) output into a muted gain on the destination.
        const sink = ctx.createGain();
        sink.gain.value = 0;
        node.connect(sink);
        sink.connect(ctx.destination);
        source.connect(node);
        const handle = { onPeak: null };
        let chunks = [];
        let capturing = false;
        let pending = null; // { promise, resolve } for the stop in flight
        let lastResult = null;
        node.port.onmessage = (e) => {
            const m = e.data;
            if (m.type === 'peak') {
                if (handle.onPeak) handle.onPeak(m.value);
            }
            else if (m.type === 'data') {
                chunks.push(m);
            }
            else if (m.type === 'done') {
                let total = 0;
                for (const c of chunks) total += c.channels[0].length;
                const out = [];
                for (let ch = 0; ch < channels; ch++) {
                    const arr = new Float32Array(total);
                    let at = 0;
                    for (const c of chunks) {
                        arr.set(c.channels[ch] || c.channels[0], at);
                        at += c.channels[0].length;
                    }
                    out.push(arr);
                }
                chunks = [];
                capturing = false;
                lastResult = { channels: out, firstFrame: m.frame, sampleRate: ctx.sampleRate };
                const p = pending;
                pending = null;
                if (p) p.resolve(lastResult);
            }
        };
        handle.start = (atFrame) => {
            chunks = [];
            lastResult = null;
            capturing = true;
            node.port.postMessage({ type: 'start', atFrame });
        };
        /** Stop at a frame (or now). Calling again moves the stop; once finished it returns the same result. */
        handle.stop = (atFrame) => {
            if (!capturing) return Promise.resolve(lastResult);
            if (!pending) {
                let resolve;
                const promise = new Promise(r => { resolve = r; });
                pending = { promise, resolve };
            }
            node.port.postMessage({ type: 'stop', atFrame });
            return pending.promise;
        };
        handle.dispose = () => {
            node.port.onmessage = null;
            try { source.disconnect(node); } catch (_) {}
            try { node.disconnect(); } catch (_) {}
            try { sink.disconnect(); } catch (_) {}
        };
        return handle;
    }

    /** One metronome tick at ctx time `when` (accent = downbeat). Returns the oscillator so it can be stopped. */
    function scheduleClick(ctx, dest, when, accent, volume = 1) {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.value = accent ? 1760 : 1175;
        const peak = (accent ? 0.55 : 0.35) * volume;
        gain.gain.setValueAtTime(0.0001, when);
        gain.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak), when + 0.002);
        gain.gain.exponentialRampToValueAtTime(0.0001, when + 0.06);
        osc.connect(gain);
        gain.connect(dest);
        osc.start(when);
        osc.stop(when + 0.07);
        osc.onended = () => { try { gain.disconnect(); } catch (_) {} };
        return osc;
    }

    /** Release every open stream (DAW close). */
    function releaseAll() {
        for (const entry of streams.values()) {
            entry.stream?.getTracks().forEach(t => t.stop());
        }
        streams.clear();
    }

    return {
        support, listDevices, permissionState, requestAccess, openStream, streamChannels, streamLatency,
        streamLabel, ensureWorklet, createRoute, createCapture, scheduleClick, releaseAll
    };
})();
