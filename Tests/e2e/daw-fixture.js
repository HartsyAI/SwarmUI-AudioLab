/**
 * Browser e2e fixture for the Audio Lab DAW: seeds the open session with synthetic tracks, no models needed.
 *
 * Usage (browser console, or the browser tool's javascript runner, on a page with the Audio Lab tab):
 *   1. Paste this file, then run:  await dawFixture({ tracks: 4, seconds: 120 })
 *   2. It opens the DAW and adds one track per call to AudioDaw.open(), each a distinct tone with a slow beat, so
 *      waveforms are visibly different and the project is long enough to scroll at the default zoom.
 *
 * Only uses the DAW's public entry point (AudioDaw.open), so it works on any build of the extension.
 */
async function dawFixture({ tracks = 3, seconds = 60, sampleRate = 8000 } = {}) {
    /** A mono 16-bit WAV of a tone with a pulsing envelope, as a blob URL. */
    function toneUrl(freq, secs, beatHz) {
        const n = Math.floor(secs * sampleRate);
        const buf = new ArrayBuffer(44 + n * 2);
        const v = new DataView(buf);
        const str = (o, s) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
        str(0, 'RIFF'); v.setUint32(4, 36 + n * 2, true); str(8, 'WAVE'); str(12, 'fmt ');
        v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
        v.setUint32(24, sampleRate, true); v.setUint32(28, sampleRate * 2, true);
        v.setUint16(32, 2, true); v.setUint16(34, 16, true); str(36, 'data'); v.setUint32(40, n * 2, true);
        for (let i = 0; i < n; i++) {
            const t = i / sampleRate;
            const env = 0.35 + 0.35 * Math.sin(2 * Math.PI * beatHz * t);
            v.setInt16(44 + i * 2, Math.round(Math.sin(2 * Math.PI * freq * t) * env * 32767), true);
        }
        return URL.createObjectURL(new Blob([buf], { type: 'audio/wav' }));
    }
    const freqs = [220, 330, 440, 550, 660, 880];
    for (let i = 0; i < tracks; i++) {
        await AudioDaw.open(toneUrl(freqs[i % freqs.length], seconds, 0.5 + i * 0.25));
    }
    return { tracks, seconds };
}
