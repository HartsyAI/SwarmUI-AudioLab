/**
 * AudioLab wake-word section, rendered into the Audio Backend card.
 *
 * Drives the listener: status, settings, the live detection feed, word training and speaker management.
 * The detection feed rides a WebSocket the server holds open, so this file never polls.
 *
 * It lives on the backend card rather than its own tab because it is set-up-once admin config that belongs
 * with the rest of the audio setup. The listener itself is NOT part of that backend's lifecycle.
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

    /** Live status line: rewritten on every socket event, so callers pass already-translated text and the
     *  element never carries class="translate" (a later sweep would restore a stale cached original). */
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
        this.setFeedState(translate('Connecting\u2026'));
        const socket = makeWSRequest('AudioLabWakeEvents', {}, data => {
            if (data.subscribed) {
                this.backoff = 1000;
                this.setFeedState(data.running
                    ? `${translate('Connected, listening on port')} ${data.port}.`
                    : translate('Connected, but the listener is stopped.'), 'text-success');
            }
            else if (data.detection) { this.addDetection(data.detection); }
        }, 0, err => this.setFeedState(`${translate('Event socket error:')} ${err}`, 'text-danger'));
        if (!socket) { return; }
        this.socket = socket;
        socket.addEventListener('close', () => {
            this.socket = null;
            this.setFeedState(`${translate('Disconnected, retrying in')} ${Math.round(this.backoff / 1000)}s.`, 'text-warning');
            setTimeout(() => this.connectFeed(), this.backoff);
            this.backoff = Math.min(this.backoff * 2, 30000);
        });
    },

    refreshStatus() {
        genericRequest('AudioLabWakeStatus', {}, data => {
            const badge = getRequiredElementById('wakeword_state_badge');
            // Badge text flips on every refresh, so it is translated here and never class="translate".
            badge.innerText = data.running ? translate('running') : translate('stopped');
            badge.className = `audiolab-wake-badge${data.running ? ' running' : ''}`;
            const devices = (data.devices ?? []);
            getRequiredElementById('wakeword_status_body').innerHTML =
                `<div><strong>${translate('Port:')}</strong> ${data.running ? data.port : translate('n/a')}</div>`
                + `<div><strong>${translate('Models:')}</strong> <code>${escapeHtml(data.model_root ?? '')}</code></div>`
                + `<div><strong>${translate('Satellites:')}</strong> ${devices.length === 0 ? `<em>${translate('none connected')}</em>` : devices.map(escapeHtml).join(', ')}</div>`
                + `<div><strong>${translate('Noise suppression:')}</strong> ${this.denoiseStatus(data)}</div>`;
            this.renderModels(data);
        }, 0, e => getRequiredElementById('wakeword_status_body').innerHTML = `<span class="text-danger">${escapeHtml(e)}</span>`);
    },

    /// The listener fails closed with a bare "backbone not found" until these are downloaded, and nothing in
    /// the UI used to offer to download them — the install routes existed but were only reachable over the API.
    renderModels(data) {
        const el = document.getElementById('wakeword_models_body');
        if (!el) { return; }
        const heads = data.installed_heads ?? [];
        const stock = data.available_stock_heads ?? [];
        const ok = (yes, yesText, noText) => yes
            ? `<span class="text-success">${yesText}</span>`
            : `<span class="text-warning">${noText}</span>`;
        // Whole phrases rather than "${verb} backbone": word order differs between languages.
        let html = `<div class="audiolab-wake-model-row">`
            + `<div><strong>${translate('Backbone')}</strong> &mdash; ${ok(data.backbone_installed, translate('installed'), translate('not installed; the listener cannot start without it'))}</div>`
            + `<button class="basic-button" id="wakeword_install_backbone">${data.backbone_installed ? translate('Reinstall backbone') : translate('Install backbone')}</button>`
            + `</div>`;
        html += `<div class="audiolab-wake-model-row">`
            + `<div><strong>${translate('Wake words')}</strong> &mdash; ${heads.length ? escapeHtml(heads.join(', ')) : `<span class="text-warning">${translate('none installed; install a stock word or train one')}</span>`}</div>`;
        if (stock.length) {
            // Option values and labels are the wake-word ids themselves — never translated.
            html += `<select id="wakeword_stock_pick" class="form-select">`
                + stock.map(w => `<option value="${escapeHtml(w)}">${escapeHtml(w)}</option>`).join('')
                + `</select><button class="basic-button" id="wakeword_install_head">${translate('Install')}</button>`;
        }
        html += `</div>`;
        html += `<div class="audiolab-wake-model-row">`
            + `<div><strong>${translate('Denoiser')}</strong> &mdash; ${ok(data.denoiser_available, translate('installed'), translate('not installed; noise suppression will run unsuppressed'))}</div>`
            + `<button class="basic-button" id="wakeword_install_denoiser">${data.denoiser_available ? translate('Reinstall denoiser') : translate('Install denoiser')}</button>`
            + `</div>`
            + `<div class="audiolab-wake-hint">${translate('The denoiser has no canonical download &mdash; it is a conversion of upstream RNNoise\'s PyTorch checkpoint. Set a URL under Settings to install it from.')}</div>`;
        html += `<div class="audiolab-wake-model-row">`
            + `<div><strong>${translate('End of speech')}</strong> &mdash; ${ok(data.vad_installed, translate('installed'), translate('not installed; every utterance waits a fixed 3s and anything longer is cut off'))}</div>`
            + `<button class="basic-button" id="wakeword_install_vad">${data.vad_installed ? translate('Reinstall end-of-speech model') : translate('Install end-of-speech model')}</button>`
            + `</div>`
            + `<div class="audiolab-wake-hint">${translate('Silero VAD, from its own repository. With it the utterance ends when you stop talking, so a short command is transcribed at once and a long question is allowed to finish.')}</div>`;
        html += `<div id="wakeword_install_progress" class="small"></div>`;
        el.innerHTML = html;
        const bind = (id, fn) => { const b = document.getElementById(id); if (b) { b.addEventListener('click', fn); } };
        // Route names stay verbatim; the trailing argument is the display label shown in the progress box.
        bind('wakeword_install_backbone', () => this.installModel('AudioLabWakeInstallBackbone', {}, translate('backbone')));
        bind('wakeword_install_head', () => this.installModel('AudioLabWakeInstallStockHead',
            { word: getRequiredElementById('wakeword_stock_pick').value }, translate('wake word')));
        bind('wakeword_install_denoiser', () => this.installModel('AudioLabWakeInstallDenoiser', {}, translate('denoiser')));
        bind('wakeword_install_vad', () => this.installModel('AudioLabWakeInstallVad', {}, translate('end-of-speech model')));
    },

    /// Shared driver for the three installs: same status/success/error frames the training route uses.
    installModel(route, params, label) {
        const progress = getRequiredElementById('wakeword_install_progress');
        progress.innerHTML = `<em>${translate('Downloading')} ${escapeHtml(label)}\u2026</em>`;
        makeWSRequest(route, params, data => {
            if (data.status) {
                progress.insertAdjacentHTML('beforeend', `<div>${escapeHtml(data.status)}</div>`);
            }
            else if (data.success) {
                progress.insertAdjacentHTML('beforeend', `<div class="text-success">${escapeHtml(label)}: ${translate('installed.')}</div>`);
                // Refreshing rebuilds this panel, so the buttons reflect what is now on disk.
                this.refreshStatus();
                this.refreshWords();
            }
            else if (data.error) {
                progress.insertAdjacentHTML('beforeend', `<div class="text-danger">${escapeHtml(data.error)}</div>`);
            }
        });
    },

    /// Distinguishes "off" from "on but the weights were never produced" -- the second runs unsuppressed and
    /// would otherwise look identical to working.
    denoiseStatus(data) {
        if (!data.noise_suppression) { return `<em>${translate('off')}</em>`; }
        if (data.denoiser_available) { return translate('on'); }
        // The script path and the on-disk location are literals, so they sit outside the translated prose.
        return `<span class="text-warning">${translate('on, but no denoiser weights found &mdash; running unsuppressed.')}`
            + ` ${translate('Convert them with')} <code>tools/convert_pth_to_safetensors.py</code> ${translate('and place the result at')}`
            + ' <code>{models}/audio/wake/denoise/rnnoise.safetensors</code>.</span>';
    },

    refreshWords() {
        genericRequest('AudioLabWakeListWords', {}, data => {
            const words = data.words ?? [];
            if (words.length === 0) {
                getRequiredElementById('wakeword_words').innerHTML = `<em>${translate('No wake words loaded. Start the listener, or train one.')}</em>`;
                return;
            }
            let html = `<table class="table table-sm"><thead><tr><th>${translate('Word')}</th><th>${translate('Threshold')}</th>`
                + `<th>${translate('Route')}</th><th>${translate('Required speaker')}</th><th></th></tr></thead><tbody>`;
            for (const w of words) {
                const id = escapeHtml(w.name);
                html += `<tr>
                    <td><code>${id}</code></td>
                    <td><input type="number" step="0.01" min="0" max="1" class="form-control form-control-sm" id="wakeword_thresh_${id}" value="${w.threshold}"></td>
                    <td><input type="text" class="form-control form-control-sm" id="wakeword_route_${id}" value="${escapeHtml(w.route ?? '')}"></td>
                    <td><input type="text" class="form-control form-control-sm" id="wakeword_speaker_${id}" value="${escapeHtml(w.required_speaker ?? '')}"></td>
                    <td><button class="btn btn-sm btn-primary" onclick="WakeWordUI.saveWord('${id}')">${translate('Save')}</button></td>
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
            else { showError(`${translate('Could not save')} '${name}': ${data.error}`); }
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
            getRequiredElementById('wakeword_setting_denoise').checked = !!s.NoiseSuppression;
            getRequiredElementById('wakeword_setting_denoiser_url').value = s.DenoiserUrl ?? '';
            getRequiredElementById('wakeword_setting_eos').checked = s.UseEndOfSpeech !== false;
            getRequiredElementById('wakeword_setting_eos_silence').value = s.EndOfSpeechSilenceMs ?? 500;
            getRequiredElementById('wakeword_setting_utterance').value = s.UtteranceSeconds ?? 12;
            getRequiredElementById('wakeword_setting_server_turns').checked = s.ServerSideTurns === true;
            getRequiredElementById('wakeword_setting_assistant').value = s.AssistantId ?? '';
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
                NoiseSuppression: getRequiredElementById('wakeword_setting_denoise').checked,
                DenoiserUrl: getRequiredElementById('wakeword_setting_denoiser_url').value.trim(),
                UseEndOfSpeech: getRequiredElementById('wakeword_setting_eos').checked,
                EndOfSpeechSilenceMs: parseInt(getRequiredElementById('wakeword_setting_eos_silence').value),
                UtteranceSeconds: parseFloat(getRequiredElementById('wakeword_setting_utterance').value),
                ServerSideTurns: getRequiredElementById('wakeword_setting_server_turns').checked,
                AssistantId: getRequiredElementById('wakeword_setting_assistant').value.trim(),
            }
        }, data => {
            if (!data.success) { showError(`${translate('Could not save settings:')} ${data.error}`); }
            this.refreshStatus();
        });
    },

    trainWord() {
        const phrase = getRequiredElementById('wakeword_train_phrase').value.trim();
        // doNoticePopover translates its own static message; wrapping it here would double-translate.
        if (!phrase) { doNoticePopover('Enter a phrase to train.', 'notice-pop-yellow'); return; }
        const progress = getRequiredElementById('wakeword_train_progress');
        progress.innerHTML = `<em>${translate('Starting…')}</em>`;
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
                let summary = `<div class="text-success"><strong>${translate('Trained')} '${escapeHtml(data.name)}'</strong></div>`
                    + `<div>${translate('Held-out recall')} ${(data.recall * 100).toFixed(1)}% `
                    + `${translate('at threshold')} ${data.suggested_threshold.toFixed(2)}</div>`
                    + `<div>${translate('False accepts:')} ${perHour}/${translate('hour')}</div>`;
                if (perHour > 5) {
                    summary += `<div class="text-warning">${perHour} ${translate('false accepts/hour is too high to live with. Point the negative audio folder at hours of real room audio.')}</div>`;
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
                el.innerHTML = `<em>${translate('Speaker identification is unavailable: the CAM++ weights were not found.')}</em>`;
                return;
            }
            const speakers = data.speakers ?? [];
            if (speakers.length === 0) { el.innerHTML = `<em>${translate('Nobody enrolled yet.')}</em>`; return; }
            let html = `<table class="table table-sm"><thead><tr><th>${translate('Name')}</th><th>${translate('Utterances')}</th>`
                + `<th>${translate('Phrase')}</th><th></th></tr></thead><tbody>`;
            for (const s of speakers) {
                html += `<tr><td>${escapeHtml(s.name)}</td><td>${s.utterances}</td><td>${escapeHtml(s.phrase ?? '')}</td>`
                    + `<td><button class="btn btn-sm btn-outline-danger" onclick="WakeWordUI.removeSpeaker('${escapeHtml(s.name)}')">${translate('Remove')}</button></td></tr>`;
            }
            el.innerHTML = html + '</tbody></table>';
        });
    },

    removeSpeaker(name) {
        if (!confirm(`${translate('Remove enrolled speaker')} '${name}'?`)) { return; }
        genericRequest('AudioLabWakeRemoveSpeaker', { name: name }, () => this.refreshSpeakers());
    },

    refreshAll() {
        this.refreshStatus();
        this.refreshWords();
        this.refreshSettings();
        this.refreshSpeakers();
    },


    /** The section's markup. Uses the engine manager's collapsible groups so it reads as one card. */
    /** The section's markup: one collapsible Wake Word group holding everything, so the card stays short. */
    sectionHtml() {
        // translate() is baked in rather than class="translate": most labels here wrap their own <input>,
        // and a textContent sweep over those would delete the input. The state badge is rewritten by
        // refreshStatus(), so it must be translated directly here too.
        return `
<div class="audiolab-cat-group collapsed" id="wakeword_root_group">
    <div class="audiolab-cat-header" id="wakeword_root_header">
        <span class="audiolab-cat-arrow">&#x2B9F;</span>
        <span>${translate('Wake Word')}</span>
        <span id="wakeword_state_badge" class="audiolab-wake-badge">${translate('stopped')}</span>
    </div>
    <div class="audiolab-cat-body audiolab-wake-body">
        <div class="audiolab-wake-intro">
            ${translate('Voice satellites stream microphone audio here continuously. The engine detects the wake word, transcribes the command that follows, and identifies the speaker. Detections are published on the')}
            <code>AudioLabWakeEvents</code> ${translate('API for other extensions to react to.')}
            <em>${translate('The listener runs independently of this backend: restarting the backend does not stop it.')}</em>
        </div>
        <div class="audiolab-wake-actions">
            <button id="wakeword_start_btn" class="basic-button btn-primary">${translate('Start Listener')}</button>
            <button id="wakeword_stop_btn" class="basic-button">${translate('Stop Listener')}</button>
            <button id="wakeword_refresh_btn" class="basic-button">${translate('Refresh')}</button>
        </div>
        <div id="wakeword_status_body" class="audiolab-wake-status"><em>${translate('Loading…')}</em></div>

        <div class="audiolab-cat-group" data-wake-group>
            <div class="audiolab-cat-header"><span class="audiolab-cat-arrow">&#x2B9F;</span><span>${translate('Models')}</span></div>
            <div class="audiolab-cat-body audiolab-wake-body">
                <div id="wakeword_models_body"><em>${translate('Loading…')}</em></div>
            </div>
        </div>

        <div class="audiolab-cat-group collapsed" data-wake-group>
            <div class="audiolab-cat-header"><span class="audiolab-cat-arrow">&#x2B9F;</span><span>${translate('Settings')}</span></div>
            <div class="audiolab-cat-body audiolab-wake-body">
                <div class="audiolab-wake-grid">
                    <label>${translate('Start with SwarmUI')}<input type="checkbox" id="wakeword_setting_enabled" class="form-check-input"></label>
                    <label>${translate('Port')}<input type="number" id="wakeword_setting_port" class="form-control" value="10800" min="1" max="65535"></label>
                    <label>${translate('Bind address')}<input type="text" id="wakeword_setting_bind" class="form-control" value="0.0.0.0"></label>
                    <label>${translate('Transcribe the command')}<input type="checkbox" id="wakeword_setting_transcribe" class="form-check-input" checked></label>
                    <label>${translate('Identify speakers')}<input type="checkbox" id="wakeword_setting_speakers" class="form-check-input" checked></label>
                    <label>${translate('Transcription model')}<input type="text" id="wakeword_setting_model" class="form-control" value="whisper"></label>
                    <label>${translate('Bind the LAN port')}<input type="checkbox" id="wakeword_setting_tcp" class="form-check-input" checked>
                        <span class="audiolab-wake-hint">${translate('Off for a tunnel-only setup: nothing listens on the LAN and satellites use the WebSocket route.')}</span></label>
                    <label>${translate('Shared secret')}<input type="text" id="wakeword_setting_token" class="form-control" placeholder="${translate('(empty = no authentication)')}">
                        <span class="audiolab-wake-hint">${translate('Satellites send this in their hello frame. Empty is fine on a trusted LAN, but set it before this is reachable from the internet, or anyone who finds the endpoint can stream audio in and read every transcript.')}</span></label>
                    <label>${translate('Denoiser URL')}<input type="text" id="wakeword_setting_denoiser_url" class="form-control" placeholder="${translate('(empty = no denoiser download configured)')}">
                        <span class="audiolab-wake-hint">${translate('Where to download the RNNoise weights from. There is no default: they are a conversion of upstream\'s PyTorch checkpoint, so whoever hosts the converted file decides where it lives. Save, then use Install below.')}</span></label>
                    <label>${translate('End the utterance when you stop talking')}<input type="checkbox" id="wakeword_setting_eos" class="form-check-input" checked>
                        <span class="audiolab-wake-hint">${translate('Needs the end-of-speech model installed above. Off, transcription starts a fixed three seconds after the wake word: a short command waits the full three seconds and a longer question is cut off mid-word.')}</span></label>
                    <label>${translate('End-of-speech silence (ms)')}<input type="number" id="wakeword_setting_eos_silence" class="form-control" value="500" min="100" max="3000" step="50">
                        <span class="audiolab-wake-hint">${translate('How long a pause has to be before it counts as the end. Below about 400ms it will cut people off mid-sentence.')}</span></label>
                    <label>${translate('Longest utterance (s)')}<input type="number" id="wakeword_setting_utterance" class="form-control" value="12" min="2" max="15" step="0.5">
                        <span class="audiolab-wake-hint">${translate('Caps both the audio transcribed and how long end-of-speech waits, so someone who never stops talking still gets an answer.')}</span></label>
                    <label>${translate('Answer on the server')}<input type="checkbox" id="wakeword_setting_server_turns" class="form-check-input">
                        <span class="audiolab-wake-hint">${translate('The server asks the assistant, synthesizes the reply, and sends the audio back down the socket the satellite already has open — instead of the satellite opening three connections of its own to do it. Needs firmware that plays <code>audio</code> frames: turn it on for a device that still runs its own turn and every reply is spoken twice. Wake words with a route configured are left alone, since a route means something else owns that turn.')}</span></label>
                    <label>${translate('Assistant')}<input type="text" id="wakeword_setting_assistant" class="form-control" placeholder="${translate('(the active one)')}">
                        <span class="audiolab-wake-hint">${translate('Which assistant answers, when the server runs the turn. Empty means whichever is currently active.')}</span></label>
                    <label>${translate('Noise suppression')}<input type="checkbox" id="wakeword_setting_denoise" class="form-check-input">
                        <span class="audiolab-wake-hint" id="wakeword_denoise_hint">${translate('Runs RNNoise over each satellite\'s audio before the wake model scores it, so it hears speech rather than the room. Costs compute per connected satellite. Transcription and speaker identification still use the raw microphone feed.')}</span></label>
                </div>
                <div class="audiolab-wake-actions">
                    <button id="wakeword_save_settings_btn" class="basic-button btn-primary">${translate('Save Settings')}</button>
                    <span class="audiolab-wake-hint">${translate('Saving restarts the listener when it is running.')}</span>
                </div>
            </div>
        </div>

        <div class="audiolab-cat-group collapsed" data-wake-group>
            <div class="audiolab-cat-header"><span class="audiolab-cat-arrow">&#x2B9F;</span><span>${translate('Wake Words')}</span></div>
            <div class="audiolab-cat-body audiolab-wake-body">
                <div id="wakeword_words"><em>${translate('Loading…')}</em></div>
                <div class="audiolab-wake-actions">
                    <button id="wakeword_train_btn" class="basic-button btn-primary">${translate('Train a Wake Word')}</button>
                </div>
            </div>
        </div>

        <div class="audiolab-cat-group collapsed" data-wake-group>
            <div class="audiolab-cat-header"><span class="audiolab-cat-arrow">&#x2B9F;</span><span>${translate('Speakers')}</span></div>
            <div class="audiolab-cat-body audiolab-wake-body">
                <div class="audiolab-wake-hint">${translate('Enroll on repetitions of the wake phrase itself. A wake word is about a second long, and speaker verification degrades badly at that length unless enrollment and use share the same words. Enrollment is API-only for now.')}
                    (<code>AudioLabWakeEnrollSpeaker</code>)</div>
                <div id="wakeword_speakers"><em>${translate('Loading…')}</em></div>
            </div>
        </div>

        <div class="audiolab-cat-group collapsed" data-wake-group>
            <div class="audiolab-cat-header"><span class="audiolab-cat-arrow">&#x2B9F;</span><span>${translate('Live Detections')}</span></div>
            <div class="audiolab-cat-body audiolab-wake-body">
                <div id="wakeword_feed_state" class="audiolab-wake-hint">${translate('Not connected.')}</div>
                <div id="wakeword_feed" class="wakeword-feed"></div>
            </div>
        </div>
    </div>
</div>`;
    },

    /** Renders the section into a backend card and wires it. Nothing talks to the server until expanded. */
    mount(container) {
        container.innerHTML = this.sectionHtml();
        const root = container.querySelector('#wakeword_root_group');
        container.querySelector('#wakeword_root_header').addEventListener('click', () => {
            root.classList.toggle('collapsed');
            if (!root.classList.contains('collapsed')) { this.start(); }
        });
        for (const header of container.querySelectorAll('[data-wake-group] > .audiolab-cat-header')) {
            header.addEventListener('click', () => header.parentElement.classList.toggle('collapsed'));
        }
        container.querySelector('#wakeword_start_btn').addEventListener('click', () =>
            genericRequest('AudioLabWakeStart', {}, data => {
                if (!data.success) { showError(`${translate('Could not start the wake listener:')} ${data.error}`); }
                this.start();
            }));
        container.querySelector('#wakeword_stop_btn').addEventListener('click', () =>
            genericRequest('AudioLabWakeStop', {}, () => this.refreshAll()));
        container.querySelector('#wakeword_refresh_btn').addEventListener('click', () => this.refreshAll());
        container.querySelector('#wakeword_save_settings_btn').addEventListener('click', () => this.saveSettings());
        container.querySelector('#wakeword_train_btn').addEventListener('click', () => this.showTrainModal());
        // Status is cheap and is the one thing worth showing before anything is expanded.
        this.refreshStatus();
    },

    /** Opens the training flow in a modal: it streams progress and can run for minutes. */
    showTrainModal() {
        document.getElementById('wakeword_train_modal')?.remove();
        const body = `
        <div class="modal-body">
            <p>${translate('The phrase is synthesized across the engine\'s TTS voices and a small classifier is fitted to it. Point negative audio at a folder of real room recordings. With too few negatives the false-accept rate is unusable no matter how good the recall looks.')}</p>
            <div class="audiolab-wake-grid">
                <label>${translate('Phrase')}<input type="text" id="wakeword_train_phrase" class="form-control" placeholder="hey hartsy"></label>
                <label>${translate('Negative audio folder')}<input type="text" id="wakeword_train_negatives" class="form-control" placeholder="/path/to/recordings"></label>
                <label>${translate('Negative phrases')}<input type="text" id="wakeword_train_negphrases" class="form-control" placeholder="hey alexa, okay google"></label>
            </div>
            <div id="wakeword_train_progress" class="wakeword-progress"></div>
        </div>
        <div class="modal-footer">
            <button class="btn btn-primary basic-button" id="wakeword_train_go">${translate('Train')}</button>
            <button class="btn btn-secondary basic-button" id="wakeword_train_close">${translate('Close')}</button>
        </div>`;
        const wrapper = document.createElement('div');
        wrapper.innerHTML = modalHeader('wakeword_train_modal', translate('Train a Wake Word')) + body + modalFooter();
        document.body.appendChild(wrapper.firstElementChild);
        const modal = document.getElementById('wakeword_train_modal');
        modal.querySelector('#wakeword_train_close').addEventListener('click', () => {
            $(modal).modal('hide');
            setTimeout(() => modal.remove(), 300);
        });
        modal.querySelector('#wakeword_train_go').addEventListener('click', () => this.trainWord());
        $(modal).modal('show');
    },

    /** Wires the section the first time it is expanded, so nothing connects for admins who never open it. */
    start() {
        this.refreshAll();
        if (this.started) { return; }
        this.started = true;
        this.connectFeed();
    },
};
