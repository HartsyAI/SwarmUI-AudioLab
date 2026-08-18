/**
 * AudioLab — Wake Word tab.
 *
 * Drives the wake-word listener: status, settings, the live detection feed, word training and speaker
 * management. The detection feed rides a WebSocket that the server holds open, so this file never polls.
 */

'use strict';

const WakeWordUI = {
    /** Live detection socket, or null when disconnected. */
    socket: null,
    /** Reconnect delay in ms, doubled on each failure. */
    backoff: 1000,
    /** Set once the tab has been opened, so we don't connect for users who never look at it. */
    started: false,

    /** Formats a detection into one feed row. */
    renderDetection(d) {
        const time = d.detected_at ? new Date(d.detected_at).toLocaleTimeString() : '';
        const parts = [`<strong>${escapeHtml(d.word ?? '?')}</strong>`];
        if (typeof d.score === 'number') { parts.push(`<span class="text-muted">${d.score.toFixed(3)}</span>`); }
        if (d.device_id) { parts.push(`<span class="badge bg-secondary">${escapeHtml(d.device_id)}</span>`); }
        if (d.speaker) { parts.push(`<span class="badge bg-info">${escapeHtml(d.speaker)}</span>`); }
        if (d.route) { parts.push(`<span class="badge bg-dark">${escapeHtml(d.route)}</span>`); }
        let html = `<div class="wakeword-feed-row"><span class="text-muted small">${escapeHtml(time)}</span> ${parts.join(' ')}`;
        if (d.transcript) { html += `<div class="wakeword-transcript">${escapeHtml(d.transcript)}</div>`; }
        return html + '</div>';
    },

    addDetection(d) {
        const feed = getRequiredElementById('wakeword_feed');
        feed.insertAdjacentHTML('afterbegin', this.renderDetection(d));
        while (feed.childElementCount > 50) { feed.lastElementChild.remove(); }
    },

    setFeedState(text, cls) {
        const el = getRequiredElementById('wakeword_feed_state');
        el.innerText = text;
        el.className = `small ${cls ?? 'text-muted'}`;
    },

    /**
     * Opens the detection stream.
     *
     * Uses makeWSRequest so a stale session_id is refreshed and retried by core rather than silently failing,
     * then attaches its own onclose: core does not set one, and this feed is meant to survive the listener
     * being stopped and started again without a page reload.
     */
    connectFeed() {
        if (this.socket) { return; }
        this.setFeedState('Connecting\u2026');
        const socket = makeWSRequest('AudioLabWakeEvents', {}, data => {
            if (data.subscribed) {
                this.backoff = 1000;
                this.setFeedState(data.running ? `Connected \u2014 listening on port ${data.port}.` : 'Connected \u2014 listener is stopped.', 'text-success');
            }
            else if (data.detection) { this.addDetection(data.detection); }
        }, 0, err => this.setFeedState(`Event socket error: ${err}`, 'text-danger'));
        if (!socket) { return; }
        this.socket = socket;
        socket.addEventListener('close', () => {
            this.socket = null;
            this.setFeedState(`Disconnected \u2014 retrying in ${Math.round(this.backoff / 1000)}s.`, 'text-warning');
            setTimeout(() => this.connectFeed(), this.backoff);
            this.backoff = Math.min(this.backoff * 2, 30000);
        });
    },

    refreshStatus() {
        genericRequest('AudioLabWakeStatus', {}, data => {
            const badge = getRequiredElementById('wakeword_state_badge');
            badge.innerText = data.running ? 'running' : 'stopped';
            badge.className = `badge ${data.running ? 'bg-success' : 'bg-secondary'}`;
            const devices = (data.devices ?? []);
            getRequiredElementById('wakeword_status_body').innerHTML =
                `<div><strong>Port:</strong> ${data.running ? data.port : '—'}</div>`
                + `<div><strong>Models:</strong> <code>${escapeHtml(data.model_root ?? '')}</code></div>`
                + `<div><strong>Satellites:</strong> ${devices.length === 0 ? '<em>none connected</em>' : devices.map(escapeHtml).join(', ')}</div>`;
        }, 0, e => getRequiredElementById('wakeword_status_body').innerHTML = `<span class="text-danger">${escapeHtml(e)}</span>`);
    },

    refreshWords() {
        genericRequest('AudioLabWakeListWords', {}, data => {
            const words = data.words ?? [];
            if (words.length === 0) {
                getRequiredElementById('wakeword_words').innerHTML = '<em>No wake words loaded. Start the listener, or train one below.</em>';
                return;
            }
            let html = '<table class="table table-sm"><thead><tr><th>Word</th><th>Threshold</th><th>Route</th><th>Required speaker</th><th></th></tr></thead><tbody>';
            for (const w of words) {
                const id = escapeHtml(w.name);
                html += `<tr>
                    <td><code>${id}</code></td>
                    <td><input type="number" step="0.01" min="0" max="1" class="form-control form-control-sm" id="wakeword_thresh_${id}" value="${w.threshold}"></td>
                    <td><input type="text" class="form-control form-control-sm" id="wakeword_route_${id}" value="${escapeHtml(w.route ?? '')}"></td>
                    <td><input type="text" class="form-control form-control-sm" id="wakeword_speaker_${id}" value="${escapeHtml(w.required_speaker ?? '')}"></td>
                    <td><button class="btn btn-sm btn-primary" onclick="WakeWordUI.saveWord('${id}')">Save</button></td>
                </tr>`;
            }
            getRequiredElementById('wakeword_words').innerHTML = html + '</tbody></table>';
        }, 0, e => getRequiredElementById('wakeword_words').innerHTML = `<span class="text-danger">${escapeHtml(e)}</span>`);
    },

    saveWord(name) {
        genericRequest('AudioLabWakeConfigureWord', {
            word: name,
            threshold: parseFloat(getRequiredElementById(`wakeword_thresh_${name}`).value),
            route: getRequiredElementById(`wakeword_route_${name}`).value,
            required_speaker: getRequiredElementById(`wakeword_speaker_${name}`).value,
        }, data => {
            if (data.success) { this.refreshWords(); }
            else { alert(`Could not save '${name}': ${data.error}`); }
        });
    },

    refreshSettings() {
        genericRequest('AudioLabWakeGetSettings', {}, data => {
            const s = data.settings ?? {};
            getRequiredElementById('wakeword_setting_enabled').checked = !!s.Enabled;
            getRequiredElementById('wakeword_setting_port').value = s.Port ?? 10800;
            getRequiredElementById('wakeword_setting_bind').value = s.BindAddress ?? '0.0.0.0';
            getRequiredElementById('wakeword_setting_transcribe').checked = s.TranscribeOnDetection !== false;
            getRequiredElementById('wakeword_setting_speakers').checked = s.IdentifySpeakers !== false;
            getRequiredElementById('wakeword_setting_model').value = s.TranscribeModel ?? 'whisper';
            getRequiredElementById('wakeword_setting_tcp').checked = s.EnableTcpListener !== false;
            getRequiredElementById('wakeword_setting_token').value = s.AuthToken ?? '';
        });
    },

    saveSettings() {
        genericRequest('AudioLabWakeSaveSettings', {
            settings: {
                Enabled: getRequiredElementById('wakeword_setting_enabled').checked,
                Port: parseInt(getRequiredElementById('wakeword_setting_port').value),
                BindAddress: getRequiredElementById('wakeword_setting_bind').value,
                TranscribeOnDetection: getRequiredElementById('wakeword_setting_transcribe').checked,
                IdentifySpeakers: getRequiredElementById('wakeword_setting_speakers').checked,
                TranscribeModel: getRequiredElementById('wakeword_setting_model').value,
                EnableTcpListener: getRequiredElementById('wakeword_setting_tcp').checked,
                AuthToken: getRequiredElementById('wakeword_setting_token').value,
            }
        }, data => {
            if (!data.success) { alert(`Could not save settings: ${data.error}`); }
            this.refreshStatus();
        });
    },

    trainWord() {
        const phrase = getRequiredElementById('wakeword_train_phrase').value.trim();
        if (!phrase) { alert('Enter a phrase to train.'); return; }
        const progress = getRequiredElementById('wakeword_train_progress');
        progress.innerHTML = '<em>Starting…</em>';
        makeWSRequest('AudioLabWakeTrainWord', {
            phrase: phrase,
            negative_audio: getRequiredElementById('wakeword_train_negatives').value.trim(),
            negative_phrases: getRequiredElementById('wakeword_train_negphrases').value.trim(),
        }, data => {
            if (data.status) {
                progress.insertAdjacentHTML('beforeend', `<div class="small">${escapeHtml(data.status)}</div>`);
            }
            else if (data.success) {
                const perHour = Math.round(data.false_accepts_per_hour);
                let summary = `<div class="text-success"><strong>Trained '${escapeHtml(data.name)}'</strong></div>`
                    + `<div>Held-out recall ${(data.recall * 100).toFixed(1)}% at threshold ${data.suggested_threshold.toFixed(2)}</div>`
                    + `<div>False accepts: ${perHour}/hour</div>`;
                if (perHour > 5) {
                    summary += `<div class="text-warning">${perHour} false accepts/hour is too high to live with. Point the negative audio folder at hours of real room audio.</div>`;
                }
                progress.insertAdjacentHTML('beforeend', summary);
                this.refreshWords();
            }
            else if (data.error) {
                progress.insertAdjacentHTML('beforeend', `<div class="text-danger">${escapeHtml(data.error)}</div>`);
            }
        });
    },

    refreshSpeakers() {
        genericRequest('AudioLabWakeListSpeakers', {}, data => {
            const el = getRequiredElementById('wakeword_speakers');
            if (!data.success) { el.innerHTML = `<span class="text-danger">${escapeHtml(data.error)}</span>`; return; }
            if (!data.available) {
                el.innerHTML = '<em>Speaker identification is unavailable: the CAM++ weights were not found.</em>';
                return;
            }
            const speakers = data.speakers ?? [];
            if (speakers.length === 0) { el.innerHTML = '<em>Nobody enrolled yet.</em>'; return; }
            let html = '<table class="table table-sm"><thead><tr><th>Name</th><th>Utterances</th><th>Phrase</th><th></th></tr></thead><tbody>';
            for (const s of speakers) {
                html += `<tr><td>${escapeHtml(s.name)}</td><td>${s.utterances}</td><td>${escapeHtml(s.phrase ?? '')}</td>`
                    + `<td><button class="btn btn-sm btn-outline-danger" onclick="WakeWordUI.removeSpeaker('${escapeHtml(s.name)}')">Remove</button></td></tr>`;
            }
            el.innerHTML = html + '</tbody></table>';
        });
    },

    removeSpeaker(name) {
        if (!confirm(`Remove enrolled speaker '${name}'?`)) { return; }
        genericRequest('AudioLabWakeRemoveSpeaker', { name: name }, () => this.refreshSpeakers());
    },

    refreshAll() {
        this.refreshStatus();
        this.refreshWords();
        this.refreshSettings();
        this.refreshSpeakers();
    },

    /** Wires the tab up the first time it is shown, so nothing connects for users who never open it. */
    start() {
        if (this.started) { this.refreshAll(); return; }
        this.started = true;
        getRequiredElementById('wakeword_start_btn').addEventListener('click', () =>
            genericRequest('AudioLabWakeStart', {}, data => {
                if (!data.success) { alert(`Could not start: ${data.error}`); }
                this.refreshAll();
            }));
        getRequiredElementById('wakeword_stop_btn').addEventListener('click', () =>
            genericRequest('AudioLabWakeStop', {}, () => this.refreshAll()));
        getRequiredElementById('wakeword_refresh_btn').addEventListener('click', () => this.refreshAll());
        getRequiredElementById('wakeword_save_settings_btn').addEventListener('click', () => this.saveSettings());
        getRequiredElementById('wakeword_train_btn').addEventListener('click', () => this.trainWord());
        this.refreshAll();
        this.connectFeed();
    },
};

document.addEventListener('DOMContentLoaded', () => {
    const link = document.querySelector('#maintab_wakeword');
    if (!link) { return; }
    link.addEventListener('click', () => WakeWordUI.start());
    // Cover a reload that lands directly on the tab.
    if (link.classList.contains('active')) { WakeWordUI.start(); }
});
