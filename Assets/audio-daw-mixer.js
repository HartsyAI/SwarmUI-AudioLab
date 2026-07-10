/**
 * AudioDawMixer — Mixer panel for the DAW bottom panel.
 * Classic vertical channel strips: pan knob, fader, dB readout, M/S, live meter,
 * plus a master strip. Exposes createKnob() for reuse (track header pan).
 * Reuses SwarmUI utilities: createDiv(), createSpan().
 */
const AudioDawMixer = (() => {
    'use strict';

    // trackId (or '__master__') -> { fill, peak } meter elements, refreshed on every render
    let meterEls = new Map();

    // ===== dB FADER TAPER =====
    // Fader position p in [0,1] maps linearly in dB over [-60, +6]; 0 dB sits at ~0.909.
    const DB_MIN = -60, DB_MAX = 6;

    /** Fader position (0..1) -> linear gain. Bottom of travel = silence. */
    function faderPosToGain(p) {
        if (p <= 0.001) return 0;
        const db = DB_MIN + (DB_MAX - DB_MIN) * p;
        return Math.pow(10, db / 20);
    }

    /** Linear gain -> fader position (0..1). */
    function gainToFaderPos(gain) {
        if (gain <= 0) return 0;
        const db = 20 * Math.log10(gain);
        return Math.max(0, Math.min(1, (db - DB_MIN) / (DB_MAX - DB_MIN)));
    }

    /** Apply the 0 dB detent: snap to exactly unity when within ~0.8 dB. */
    function detent(gain) {
        if (gain > 0) {
            const db = 20 * Math.log10(gain);
            if (Math.abs(db) < 0.8) return 1;
        }
        return gain;
    }

    /**
     * Small rotary knob widget. Drag vertically to change, double-click to reset.
     * @param {Object} opts - { value, min, max, defaultValue, title, onChange(value) }
     * @returns {HTMLElement}
     */
    function createKnob(opts = {}) {
        const min = opts.min ?? -1;
        const max = opts.max ?? 1;
        const def = opts.defaultValue ?? (min + max) / 2;
        let value = opts.value ?? def;

        const knob = createDiv(null, 'daw-knob');
        knob.title = opts.title || '';
        const dial = createDiv(null, 'daw-knob-dial');
        knob.appendChild(dial);

        const apply = () => {
            const norm = (value - min) / (max - min);
            dial.style.transform = `rotate(${(norm - 0.5) * 270}deg)`;
        };
        apply();

        knob.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            e.preventDefault();
            e.stopPropagation();
            knob.setPointerCapture(e.pointerId);
            const startY = e.clientY;
            const startVal = value;
            const onMove = (me) => {
                const dv = (startY - me.clientY) / 120 * (max - min); // full range over ~120px
                value = Math.max(min, Math.min(max, startVal + dv));
                apply();
                if (opts.onChange) opts.onChange(value);
            };
            const onUp = (ue) => {
                knob.releasePointerCapture(ue.pointerId);
                knob.removeEventListener('pointermove', onMove);
                knob.removeEventListener('pointerup', onUp);
            };
            knob.addEventListener('pointermove', onMove);
            knob.addEventListener('pointerup', onUp);
        });
        knob.addEventListener('dblclick', (e) => {
            e.stopPropagation();
            value = def;
            apply();
            if (opts.onChange) opts.onChange(value);
        });
        return knob;
    }

    /**
     * Render the mixer into a container element.
     * @param {HTMLElement} container - Target DOM element
     * @param {Object} state - DAW state (tracks, masterVolume)
     * @param {Function} onStateChange - Callback (prop, value): 'volume'|'pan'|'mute'|'solo' pass the track, 'masterVolume' passes the number
     */
    function render(container, state, onStateChange) {
        container.innerHTML = '';
        meterEls = new Map();
        const strips = createDiv(null, 'daw-mixer-strips');
        for (const track of state.tracks) {
            strips.appendChild(buildStrip(track, onStateChange));
        }
        strips.appendChild(buildMasterStrip(state, onStateChange));
        container.appendChild(strips);
    }

    function buildFader(gainValue, title, onGain) {
        const wrap = createDiv(null, 'daw-strip-fader-wrap');
        const lane = createDiv(null, 'daw-strip-fader-lane');
        // 0 dB detent mark on the fader travel
        const zeroTick = createDiv(null, 'daw-strip-zero-tick');
        zeroTick.style.bottom = (gainToFaderPos(1) * 100) + '%';
        lane.appendChild(zeroTick);
        const fader = document.createElement('input');
        fader.type = 'range';
        fader.className = 'daw-strip-fader';
        fader.min = '0';
        fader.max = '1';
        fader.step = '0.005';
        fader.value = gainToFaderPos(gainValue);
        fader.title = title + ' (dB-scaled, 0 dB detent)';
        fader.addEventListener('input', (e) => {
            const gain = detent(faderPosToGain(parseFloat(e.target.value)));
            onGain(gain);
        });
        fader.addEventListener('dblclick', () => {
            fader.value = gainToFaderPos(1);
            onGain(1);
        });
        lane.appendChild(fader);
        // OBS-style stereo meter pair + dB scale
        const meterWrap = createDiv(null, 'daw-meter-pair');
        const chans = [];
        for (let ch = 0; ch < 2; ch++) {
            const bar = createDiv(null, 'daw-meter-v');
            const mask = createDiv(null, 'daw-meter-v-mask');
            const peakTick = createDiv(null, 'daw-meter-v-peak');
            bar.appendChild(mask);
            bar.appendChild(peakTick);
            meterWrap.appendChild(bar);
            chans.push({ mask, peak: peakTick });
        }
        const scale = createDiv(null, 'daw-strip-scale');
        for (const db of [0, -12, -24, -36, -48, -60]) {
            const tick = createSpan(null, 'daw-strip-scale-tick');
            tick.textContent = db === 0 ? '0' : String(db);
            scale.appendChild(tick);
        }
        wrap.appendChild(lane);
        wrap.appendChild(meterWrap);
        wrap.appendChild(scale);
        return { wrap, fader, chans };
    }

    function buildStrip(track, onStateChange) {
        const strip = createDiv(null, 'daw-mixer-strip');
        strip.dataset.trackId = track.id;

        // Color cap + name
        const cap = createDiv(null, 'daw-strip-color');
        cap.style.background = track.color;
        strip.appendChild(cap);
        const name = createDiv(null, 'daw-strip-name');
        name.textContent = track.name;
        name.title = track.name;
        strip.appendChild(name);

        // Pan knob
        const panWrap = createDiv(null, 'daw-strip-pan');
        panWrap.appendChild(createKnob({
            value: track.pan || 0, min: -1, max: 1, defaultValue: 0,
            title: 'Pan (drag up/down, double-click to center)',
            onChange: (v) => {
                track.pan = Math.round(v * 20) / 20;
                if (onStateChange) onStateChange('pan', track);
            }
        }));
        const panLbl = createSpan(null, 'daw-strip-pan-label');
        panLbl.textContent = 'PAN';
        panWrap.appendChild(panLbl);
        strip.appendChild(panWrap);

        // Fader + meter
        const dbLabel = createDiv(null, 'daw-strip-db');
        dbLabel.textContent = volumeToDb(track.volume);
        const { wrap, chans } = buildFader(track.volume, 'Track volume', (v) => {
            track.volume = v;
            dbLabel.textContent = volumeToDb(v);
            if (onStateChange) onStateChange('volume', track);
        });
        strip.appendChild(wrap);
        strip.appendChild(dbLabel);
        meterEls.set(track.id, { chans });

        // Mute / Solo
        const btns = createDiv(null, 'daw-strip-btns');
        const muteBtn = document.createElement('button');
        muteBtn.className = 'daw-mixer-btn' + (track.muted ? ' active-mute' : '');
        muteBtn.textContent = 'M';
        muteBtn.title = 'Mute this track';
        muteBtn.addEventListener('click', () => {
            track.muted = !track.muted;
            muteBtn.classList.toggle('active-mute', track.muted);
            if (onStateChange) onStateChange('mute', track);
        });
        btns.appendChild(muteBtn);
        const soloBtn = document.createElement('button');
        soloBtn.className = 'daw-mixer-btn' + (track.soloed ? ' active-solo' : '');
        soloBtn.textContent = 'S';
        soloBtn.title = 'Solo — only play this track';
        soloBtn.addEventListener('click', () => {
            track.soloed = !track.soloed;
            soloBtn.classList.toggle('active-solo', track.soloed);
            if (onStateChange) onStateChange('solo', track);
        });
        btns.appendChild(soloBtn);
        strip.appendChild(btns);

        return strip;
    }

    function buildMasterStrip(state, onStateChange) {
        const strip = createDiv(null, 'daw-mixer-strip master');
        const cap = createDiv(null, 'daw-strip-color');
        cap.style.background = 'var(--emphasis)';
        strip.appendChild(cap);
        const name = createDiv(null, 'daw-strip-name');
        name.textContent = 'Master';
        strip.appendChild(name);

        const spacer = createDiv(null, 'daw-strip-pan');
        strip.appendChild(spacer);

        const dbLabel = createDiv(null, 'daw-strip-db');
        dbLabel.textContent = volumeToDb(state.masterVolume);
        const { wrap, chans } = buildFader(state.masterVolume, 'Master volume', (v) => {
            dbLabel.textContent = volumeToDb(v);
            if (onStateChange) onStateChange('masterVolume', v);
        });
        strip.appendChild(wrap);
        strip.appendChild(dbLabel);
        meterEls.set('__master__', { chans });

        const btns = createDiv(null, 'daw-strip-btns');
        strip.appendChild(btns);
        return strip;
    }

    function volumeToDb(vol) {
        if (vol <= 0) return '-∞ dB';
        const db = 20 * Math.log10(vol);
        return db.toFixed(1) + ' dB';
    }

    /** Live-meter hooks used by the DAW's rAF loop. Returns { chans: [{mask, peak}, ...] } or null. */
    function getMeterEl(trackId) {
        const entry = meterEls.get(trackId);
        return entry && entry.chans[0]?.mask.isConnected ? entry : null;
    }

    function resetMeters() {
        for (const entry of meterEls.values()) {
            for (const chan of entry.chans || []) {
                if (chan.mask.isConnected) {
                    chan.mask.style.height = '100%';
                    chan.peak.style.bottom = '0%';
                }
            }
        }
    }

    function destroy() {
        meterEls = new Map();
    }

    return { render, createKnob, getMeterEl, resetMeters, destroy, faderPosToGain, gainToFaderPos };
})();
