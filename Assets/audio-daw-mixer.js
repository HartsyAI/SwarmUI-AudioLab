/**
 * AudioDawMixer — Mixer panel for the DAW bottom panel.
 * Renders per-track horizontal rows with volume fader, pan, mute/solo, and dB readout.
 * Uses horizontal layout (rows) for better fit in the bottom panel.
 * Reuses SwarmUI utilities: createDiv(), createSpan().
 */
const AudioDawMixer = (() => {
    'use strict';

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

        // Track rows
        for (const track of state.tracks) {
            mixer.appendChild(buildChannelRow(track, state, onStateChange));
        }

        // Master row (separator + row)
        const sep = createDiv(null, 'daw-mixer-separator');
        mixer.appendChild(sep);
        mixer.appendChild(buildMasterRow(state, onStateChange));

        container.appendChild(mixer);
    }

    function buildChannelRow(track, state, onStateChange) {
        const row = createDiv(null, 'daw-mixer-row');
        row.dataset.trackId = track.id;

        // Color indicator
        const colorBar = createDiv(null, 'daw-mixer-color');
        colorBar.style.background = track.color;
        row.appendChild(colorBar);

        // Track name
        const label = createDiv(null, 'daw-mixer-label');
        label.textContent = track.name;
        label.title = track.name;
        row.appendChild(label);

        // Mute / Solo buttons
        const btns = createDiv(null, 'daw-mixer-btns');

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
        row.appendChild(btns);

        // Volume fader (horizontal)
        const volGroup = createDiv(null, 'daw-mixer-vol-group');
        const volLabel = createSpan(null, 'daw-mixer-vol-label');
        volLabel.textContent = 'Vol';
        const fader = document.createElement('input');
        fader.type = 'range';
        fader.className = 'daw-mixer-fader';
        fader.min = '0';
        fader.max = '1';
        fader.step = '0.01';
        fader.value = track.volume;
        fader.title = `Volume: ${Math.round(track.volume * 100)}%`;
        const dbLabel = createSpan(null, 'daw-mixer-db');
        dbLabel.textContent = volumeToDb(track.volume);
        fader.addEventListener('input', (e) => {
            track.volume = parseFloat(e.target.value);
            fader.title = `Volume: ${Math.round(track.volume * 100)}%`;
            dbLabel.textContent = volumeToDb(track.volume);
            if (onStateChange) onStateChange('volume', track);
        });
        volGroup.appendChild(volLabel);
        volGroup.appendChild(fader);
        volGroup.appendChild(dbLabel);
        row.appendChild(volGroup);

        // Pan slider
        const panGroup = createDiv(null, 'daw-mixer-pan-group');
        const panL = createSpan(null, 'daw-mixer-pan-label');
        panL.textContent = 'L';
        const panSlider = document.createElement('input');
        panSlider.type = 'range';
        panSlider.className = 'daw-mixer-pan';
        panSlider.min = '-1';
        panSlider.max = '1';
        panSlider.step = '0.1';
        panSlider.value = track.pan;
        panSlider.title = `Pan: ${panString(track.pan)}`;
        const panR = createSpan(null, 'daw-mixer-pan-label');
        panR.textContent = 'R';
        panSlider.addEventListener('input', (e) => {
            track.pan = parseFloat(e.target.value);
            panSlider.title = `Pan: ${panString(track.pan)}`;
        });
        panGroup.appendChild(panL);
        panGroup.appendChild(panSlider);
        panGroup.appendChild(panR);
        row.appendChild(panGroup);

        return row;
    }

    function buildMasterRow(state, onStateChange) {
        const row = createDiv(null, 'daw-mixer-row master');

        const colorBar = createDiv(null, 'daw-mixer-color');
        colorBar.style.background = 'var(--emphasis)';
        row.appendChild(colorBar);

        const label = createDiv(null, 'daw-mixer-label');
        label.textContent = 'Master';
        label.style.fontWeight = '600';
        row.appendChild(label);

        // Spacer where M/S buttons would be
        const spacer = createDiv(null, 'daw-mixer-btns');
        row.appendChild(spacer);

        // Master volume
        const volGroup = createDiv(null, 'daw-mixer-vol-group');
        const volLabel = createSpan(null, 'daw-mixer-vol-label');
        volLabel.textContent = 'Vol';
        const fader = document.createElement('input');
        fader.type = 'range';
        fader.className = 'daw-mixer-fader';
        fader.min = '0';
        fader.max = '1';
        fader.step = '0.01';
        fader.value = state.masterVolume;
        fader.title = `Master volume: ${Math.round(state.masterVolume * 100)}%`;
        const dbLabel = createSpan(null, 'daw-mixer-db');
        dbLabel.textContent = volumeToDb(state.masterVolume);
        fader.addEventListener('input', (e) => {
            const val = parseFloat(e.target.value);
            fader.title = `Master volume: ${Math.round(val * 100)}%`;
            dbLabel.textContent = volumeToDb(val);
            if (onStateChange) onStateChange('masterVolume', val);
        });
        volGroup.appendChild(volLabel);
        volGroup.appendChild(fader);
        volGroup.appendChild(dbLabel);
        row.appendChild(volGroup);

        return row;
    }

    function volumeToDb(vol) {
        if (vol <= 0) return '-\u221E dB';
        const db = 20 * Math.log10(vol);
        return db.toFixed(1) + ' dB';
    }

    function panString(val) {
        if (val === 0) return 'Center';
        return val < 0 ? `${Math.round(Math.abs(val) * 100)}% L` : `${Math.round(val * 100)}% R`;
    }

    function destroy() {
        lastContainer = null;
    }

    return { render, destroy };
})();
