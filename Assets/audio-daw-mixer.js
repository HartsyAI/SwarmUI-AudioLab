/**
 * AudioDawMixer — Mixer panel for the DAW bottom panel.
 * Renders per-track vertical faders with mute/solo buttons, meters, and a master bus.
 * Reuses SwarmUI utilities: createDiv(), createSpan().
 */
const AudioDawMixer = (() => {
    'use strict';

    let analyserNodes = new Map(); // trackId -> AnalyserNode
    let meterRafId = null;
    let lastContainer = null;

    /**
     * Render the mixer into a container element.
     * @param {HTMLElement} container - Target DOM element
     * @param {Object} state - DAW state (tracks, masterVolume)
     * @param {Function} onStateChange - Callback for state changes (prop, value)
     */
    function render(container, state, onStateChange) {
        container.innerHTML = '';
        lastContainer = container;
        const mixer = createDiv(null, 'daw-mixer');

        for (const track of state.tracks) {
            mixer.appendChild(buildChannel(track, state, onStateChange));
        }

        // Master channel
        mixer.appendChild(buildMasterChannel(state, onStateChange));

        container.appendChild(mixer);
    }

    function buildChannel(track, state, onStateChange) {
        const ch = createDiv(null, 'daw-mixer-channel');
        ch.dataset.trackId = track.id;
        ch.style.borderTop = `3px solid ${track.color}`;

        // Label
        const label = createDiv(null, 'daw-mixer-label');
        label.textContent = track.name;
        label.title = track.name;
        ch.appendChild(label);

        // Fader + meter row
        const faderRow = createDiv(null, 'daw-mixer-fader-wrap');

        const fader = document.createElement('input');
        fader.type = 'range';
        fader.className = 'daw-mixer-fader';
        fader.min = '0';
        fader.max = '1';
        fader.step = '0.01';
        fader.value = track.volume;
        fader.title = `${track.name} volume`;
        fader.addEventListener('input', (e) => {
            track.volume = parseFloat(e.target.value);
            dbLabel.textContent = volumeToDb(track.volume);
            if (onStateChange) onStateChange('volume', track);
        });
        faderRow.appendChild(fader);

        ch.appendChild(faderRow);

        // dB readout
        const dbLabel = createDiv(null, 'daw-mixer-db');
        dbLabel.textContent = volumeToDb(track.volume);
        ch.appendChild(dbLabel);

        // Pan knob (simple slider)
        const panWrap = createDiv(null, 'daw-mixer-pan-wrap');
        panWrap.style.cssText = 'display:flex;align-items:center;gap:2px;';
        const panLabel = createSpan(null, 'daw-mixer-db');
        panLabel.textContent = 'L';
        panLabel.style.fontSize = '0.55rem';
        const panSlider = document.createElement('input');
        panSlider.type = 'range';
        panSlider.min = '-1';
        panSlider.max = '1';
        panSlider.step = '0.1';
        panSlider.value = track.pan;
        panSlider.style.cssText = 'width:40px;height:3px;accent-color:var(--emphasis);cursor:pointer;';
        panSlider.title = `Pan: ${track.pan}`;
        panSlider.addEventListener('input', (e) => {
            track.pan = parseFloat(e.target.value);
            panSlider.title = `Pan: ${track.pan}`;
        });
        const panRight = createSpan(null, 'daw-mixer-db');
        panRight.textContent = 'R';
        panRight.style.fontSize = '0.55rem';
        panWrap.appendChild(panLabel);
        panWrap.appendChild(panSlider);
        panWrap.appendChild(panRight);
        ch.appendChild(panWrap);

        // Mute / Solo buttons
        const btns = createDiv(null, 'daw-mixer-btns');

        const muteBtn = document.createElement('button');
        muteBtn.className = 'daw-track-btn' + (track.muted ? ' active-mute' : '');
        muteBtn.textContent = 'M';
        muteBtn.title = 'Mute';
        muteBtn.addEventListener('click', () => {
            track.muted = !track.muted;
            muteBtn.classList.toggle('active-mute', track.muted);
            if (onStateChange) onStateChange('mute', track);
        });
        btns.appendChild(muteBtn);

        const soloBtn = document.createElement('button');
        soloBtn.className = 'daw-track-btn' + (track.soloed ? ' active-solo' : '');
        soloBtn.textContent = 'S';
        soloBtn.title = 'Solo';
        soloBtn.addEventListener('click', () => {
            track.soloed = !track.soloed;
            soloBtn.classList.toggle('active-solo', track.soloed);
            if (onStateChange) onStateChange('solo', track);
        });
        btns.appendChild(soloBtn);

        ch.appendChild(btns);

        return ch;
    }

    function buildMasterChannel(state, onStateChange) {
        const ch = createDiv(null, 'daw-mixer-channel master');

        const label = createDiv(null, 'daw-mixer-label');
        label.textContent = 'Master';
        ch.appendChild(label);

        const faderWrap = createDiv(null, 'daw-mixer-fader-wrap');
        const fader = document.createElement('input');
        fader.type = 'range';
        fader.className = 'daw-mixer-fader';
        fader.min = '0';
        fader.max = '1';
        fader.step = '0.01';
        fader.value = state.masterVolume;
        fader.title = 'Master volume';
        const dbLabel = createDiv(null, 'daw-mixer-db');
        dbLabel.textContent = volumeToDb(state.masterVolume);
        fader.addEventListener('input', (e) => {
            const val = parseFloat(e.target.value);
            dbLabel.textContent = volumeToDb(val);
            if (onStateChange) onStateChange('masterVolume', val);
        });
        faderWrap.appendChild(fader);
        ch.appendChild(faderWrap);
        ch.appendChild(dbLabel);

        return ch;
    }

    function volumeToDb(vol) {
        if (vol <= 0) return '-inf';
        const db = 20 * Math.log10(vol);
        return db.toFixed(1) + ' dB';
    }

    function destroy() {
        if (meterRafId) { cancelAnimationFrame(meterRafId); meterRafId = null; }
        analyserNodes.clear();
        lastContainer = null;
    }

    return { render, destroy };
})();
