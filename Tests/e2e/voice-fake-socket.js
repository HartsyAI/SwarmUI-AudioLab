/**
 * Browser e2e fixture for the Voice Agent tab: stands in for the microphone and the server's AudioLabVoiceSession
 * socket, so every call state, bubble, notice and the exports can be driven with no speech models and no LLM.
 *
 * Usage (paste into the browser console or the browser tool's JavaScript runner on a page with the Audio Lab tab,
 * then open the DAW and the Voice Agent tab):
 *   1. voiceFake.install()                       // fake getUserMedia + a WebSocket stub for the voice route only
 *   2. press Start                                // the stub records the setup frame in voiceFake.setup
 *   3. drive the server side:
 *        voiceFake.state('Listening')
 *        voiceFake.say('user', 'what time is it', 1)
 *        voiceFake.say('assistant', 'It is noon.', 1)
 *        voiceFake.pcm(1, 600)                    // 600 ms of a tone as a turn-tagged PCM16 24 kHz frame
 *        voiceFake.send({ metrics: { turnId: 1, 'voice.stt.ms': 139, 'voice.llm.ttft_ms': 65, 'voice.tts.first_chunk_ms': 142, 'voice.turn.total_ms': 1226 } })
 *        voiceFake.drop(1006, 'connection lost')   // server closes the socket
 *   4. voiceFake.script() plays a whole scripted call; voiceFake.uninstall() restores the real WebSocket.
 */
const voiceFake = (() => {
    const RealWebSocket = window.WebSocket;
    const realGetUserMedia = navigator.mediaDevices?.getUserMedia?.bind(navigator.mediaDevices);
    let socket = null;
    let micCtx = null;

    class FakeSocket {
        constructor(url) {
            this.url = url;
            this.readyState = 0; // CONNECTING
            this.binaryType = 'blob';
            this.sent = [];
            this.onopen = this.onmessage = this.onclose = this.onerror = null;
            socket = this;
            setTimeout(() => {
                this.readyState = 1;
                this.onopen?.({});
            }, 20);
        }
        send(data) {
            if (typeof data === 'string') {
                const parsed = JSON.parse(data);
                if (parsed.end) {
                    this.sent.push(parsed);
                    return;
                }
                voiceFake.setup = parsed;
            }
            this.sent.push(typeof data === 'string' ? JSON.parse(data) : { binary: data.byteLength });
        }
        close() {
            this.readyState = 3;
        }
        deliver(data) {
            this.onmessage?.({ data });
        }
    }

    /** Install the stubs. `micError` makes getUserMedia fail with that DOMException name, to test the denied paths. */
    function install({ micError = '' } = {}) {
        window.WebSocket = function (url, ...rest) {
            return /AudioLabVoiceSession/.test(String(url)) ? new FakeSocket(url) : new RealWebSocket(url, ...rest);
        };
        window.WebSocket.OPEN = 1;
        window.WebSocket.CONNECTING = 0;
        window.WebSocket.CLOSED = 3;
        navigator.mediaDevices.getUserMedia = async () => {
            if (micError) {
                throw new DOMException('stub', micError);
            }
            micCtx = micCtx || new AudioContext();
            const osc = micCtx.createOscillator();
            osc.frequency.value = 220;
            const dest = micCtx.createMediaStreamDestination();
            osc.connect(dest);
            osc.start();
            return dest.stream;
        };
        return 'installed';
    }

    function uninstall() {
        window.WebSocket = RealWebSocket;
        if (realGetUserMedia) {
            navigator.mediaDevices.getUserMedia = realGetUserMedia;
        }
        return 'restored';
    }

    function send(obj) {
        socket.deliver(JSON.stringify(obj));
    }

    function state(name) {
        send({ state: name });
    }

    function say(role, text, turnId) {
        send({ transcript: { role, text, turnId } });
    }

    /** A turn-tagged PCM16 24 kHz frame of a sine tone: 4-byte little-endian turn id, then samples. */
    function pcm(turnId, ms, freq = 330) {
        const n = Math.round(24000 * ms / 1000);
        const buf = new ArrayBuffer(4 + n * 2);
        const view = new DataView(buf);
        view.setInt32(0, turnId, true);
        for (let i = 0; i < n; i++) {
            view.setInt16(4 + i * 2, Math.round(Math.sin(2 * Math.PI * freq * i / 24000) * 9000), true);
        }
        socket.deliver(buf);
    }

    function drop(code = 1006, reason = '') {
        const closing = socket;
        closing.readyState = 3;
        closing.onclose?.({ code, reason });
    }

    const wait = (ms) => new Promise(r => setTimeout(r, ms));

    /** A whole call: warm-up, a plain turn with sound and latency, a tool turn, a barge-in, a notice, then an error. */
    async function script() {
        state('Warming');
        await wait(300);
        state('Listening');
        await wait(300);
        say('user', 'What time is it?', 1);
        state('Thinking');
        await wait(300);
        say('assistant', 'It is twelve oh five.', 1);
        state('Speaking');
        pcm(1, 700);
        send({ metrics: { turnId: 1, 'voice.stt.ms': 139, 'voice.llm.ttft_ms': 65, 'voice.tts.first_chunk_ms': 142, 'voice.turn.total_ms': 1226 } });
        await wait(900);
        state('Listening');
        say('user', 'Check the weather in Paris.', 2);
        state('ToolRunning');
        send({ tool_call: { id: 'c1', name: 'get_weather', arguments: '{"city":"Paris"}', turnId: 2 } });
        await wait(300);
        send({ tool_result: { id: 'c1', name: 'get_weather', result: '18 C, clear', turnId: 2 } });
        say('assistant', 'It is eighteen degrees and clear in Paris.', 2);
        say('assistant', 'Anything else?', 2);
        state('Speaking');
        pcm(2, 3000, 262);
        await wait(500);
        say('user', 'Stop, never mind', 3);
        send({ bargein: { turnId: 2 } });
        state('Listening');
        send({ notice: 'Voice model is still warming up.' });
        send({ notice: 'Voice model is still warming up.' });
        await wait(300);
        send({ error: 'The assistant model did not answer in time.' });
        return 'scripted';
    }

    return { install, uninstall, send, state, say, pcm, drop, script, setup: null, get socket() { return socket; } };
})();
