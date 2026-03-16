/**
 * AudioLab — Entry point for the audio editor.
 * Delegates to AudioDaw (multi-track DAW) when available,
 * providing the same public API: AudioLab.open(src), AudioLab.close().
 *
 * Also retains legacy helpers used by audio-integration.js:
 * isParamActive(), getDuration().
 */
const AudioLab = (() => {
    'use strict';

    /**
     * Open the audio editor with the given source.
     * @param {string} audioSrc - URL, data: URI, or blob URL
     */
    function open(audioSrc) {
        if (typeof AudioDaw !== 'undefined') {
            AudioDaw.open(audioSrc);
        } else {
            console.error('[AudioLab] AudioDaw module not loaded');
        }
    }

    function close() {
        if (typeof AudioDaw !== 'undefined') {
            AudioDaw.close();
        }
    }

    /** Check if a model parameter input is currently visible/active. */
    function isParamActive(paramId) {
        const el = document.getElementById(`input_${paramId}`);
        if (!el) return false;
        const wrapper = el.closest('.auto-input');
        if (!wrapper) return true;
        return wrapper.style.display !== 'none';
    }

    /** Get duration of currently loaded audio (0 if nothing loaded). */
    function getDuration() {
        return 0; // DAW tracks have individual durations — not a single value
    }

    return { open, close, isParamActive, getDuration };
})();
