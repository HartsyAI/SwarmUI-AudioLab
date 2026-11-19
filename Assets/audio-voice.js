/**
 * AudioVoice -- the "Voice Agent" bottom tab: a live phone-style call with the server's
 * AudioLabVoiceSession WebSocket (HartsyInference.Voice, answered through LLMAssistant).
 *
 * Built once per page session (audio-daw.js calls render(container) exactly once, the same "built ONCE, not
 * in updateBottomPanel" treatment Beats/Generate/Score get -- see that file's buildBottomPanel), so a live call
 * survives switching to another bottom tab and back, instead of being torn down and losing its socket.
 *
 * The rules for what the call shows (turns, latency, notices, what an error means) live in AudioVoiceState (pure);
 * this file owns the socket, the audio graph and the DOM.
 *
 * Reuses SwarmUI utilities: createDiv(), createSpan(), translate(), getWSAddress(), genericRequest().
 * Deliberately does NOT use makeWSRequest() (site.js): that helper's onmessage assumes every
 * frame is JSON text, and this socket's replies are a mix of JSON events and binary PCM16 audio frames.
 */
const AudioVoice = (() => {
    'use strict';

    // Derived from this file's own <script src>, not a written-out path -- core serves extension files under
    // the extension CLASS name, not the folder name (see audio-daw-score.js's identical SOUNDFONT_URL comment).
    const WORKLET_URL = (document.querySelector('script[src*="audio-voice.js"]')?.src || '')
        .replace(/audio-voice\.js.*$/, 'audio-voice-worklet.js');

    const OUTBOUND_SAMPLE_RATE = 24000; // Kokoro's own rate; the server sends it unresampled (see the PR).
    const CAPTURE_FRAME_MS = 20;

    /** The longest typed message the server accepts (the engine's own limit). */
    const TYPED_MAX_CHARS = 2000;

    /** How close to the bottom the transcript counts as "following" the conversation, in pixels. */
    const STICK_SLACK_PX = 40;

    let rendered = false;
    let els = {};

    // Networking + audio graph. Null whenever a call is not active.
    let ws = null;
    let audioCtx = null;
    let micStream = null;
    let micSource = null;
    let captureNode = null;
    let analyserNode = null;
    let outGain = null;
    let outAnalyser = null;
    let meterRaf = null;

    // Playback scheduling: one AudioContext, sources queued back to back, each tagged with the turn it came
    // from so a `bargein` event can stop/drop only that turn's audio.
    let nextPlayTime = 0;
    let scheduledSources = []; // { turnId, source }

    let callState = 'idle'; // idle | connecting | Warming | Listening | Thinking | Speaking | ToolRunning | ended

    // What the tab shows (AudioVoiceState owns the rules)
    let transcript = AudioVoiceState.createTranscript();
    const notices = AudioVoiceState.createNotices();
    const bubbleEls = new Map(); // transcript entry -> its element
    let callStartedAt = 0;
    let tickTimer = null;
    let stickToBottom = true;
    let micLevel = 0;
    let outLevel = 0;
    let canReconnect = false;
    let starting = false; // a Start or Reconnect attempt is waiting on the microphone or the worklet

    // Setup: the language models LLMAssistant offers, the voices, and the saved settings
    const STORAGE_KEY = 'audiolab.voiceagent.v1';
    const PREVIEW_TEXT = 'Hello! This is how I sound.';
    const llm = { status: 'loading', models: [], warnings: [] };
    // What the server offers to hear and speak with: { stt: [{id, name, note}], tts: [{engine, name, note, provider_id, default_voice, voices}], defaultStt, defaultTts }
    let engines = { stt: [], tts: [], languages: [], defaultStt: '', defaultTts: '', defaultLanguage: 'en' };
    let store = AudioVoiceState.createPresetStore();
    let previewCtx = null;
    let previewSource = null;
    let previewToken = 0; // a newer preview (or a stop) makes an older one's fetch result stale

    // Microphone gating: muted by the Mute button; with push-to-talk on, open only while the key is held
    let muted = false;
    let talking = false;
    let rebindingKey = false; // the push-to-talk key button is waiting for a key press

    function render(container) {
        if (!container || rendered) {
            return;
        }
        rendered = true;
        store = loadStore();
        buildUi(container);
        applySettings(store.last);
        loadOptions();
        setState(callState);
        // Capture phase, so the push-to-talk key is taken before the DAW's own shortcuts see it (only while it applies)
        document.addEventListener('keydown', onPttKeyDown, true);
        document.addEventListener('keyup', onPttKeyUp, true);
        window.addEventListener('blur', releaseTalk); // a key released outside the window never reports it
        document.addEventListener('click', (e) => {
            if (e.target !== els.pttKeyBtn) {
                cancelRebind(); // a click anywhere else ends the wait for a key
            }
        }, true);
    }

    // ===== Saved settings =====

    /** The stored presets and last-used settings. Storage can be missing, blocked or full, so every access is guarded. */
    function loadStore() {
        try {
            return AudioVoiceState.parsePresetStore(localStorage.getItem(STORAGE_KEY));
        }
        catch (e) {
            return AudioVoiceState.createPresetStore();
        }
    }

    function persistStore() {
        try {
            localStorage.setItem(STORAGE_KEY, AudioVoiceState.serializePresetStore(store));
        }
        catch (e) { /* storage unavailable: the settings simply do not persist */ }
    }

    /** What the setup fields hold now. */
    function readSettings() {
        return AudioVoiceState.normalizeSettings({
            model: els.model.value,
            assistantId: els.assistantId.value,
            voice: els.voice.value,
            systemPrompt: els.systemPrompt.value,
            bargeIn: els.bargeIn.checked,
            sttModel: els.stt.value,
            ttsEngine: els.engine.value,
            language: els.language.value,
            pushToTalk: els.ptt.checked,
            pttKey: els.pttKeyBtn.dataset.code
        });
    }

    /** Put settings into the fields. A model, assistant or voice the lists do not have is kept as an extra option so it is not lost. */
    function applySettings(settings) {
        // Once the model list is known, a model it does not offer (an old preset) is not selectable: it would start a call that cannot answer.
        const unavailable = llm.status === 'ready' && settings.model && !llm.models.some(m => m.id === settings.model);
        setSelect(els.model, unavailable ? '' : settings.model, settings.model);
        if (unavailable) {
            pushNotice(`${translate('Saved model')} "${settings.model}" ${translate('is not available; using')} "${els.model.value}".`, 'info');
        }
        setSelect(els.assistantId, settings.assistantId, settings.assistantId);
        // Once the server's lists are known a recognizer or engine it does not offer (an old preset) is replaced by the default: the server would refuse it.
        const stt = engines.stt.length ? AudioVoiceState.pickOption(engines.stt.map(c => c.id), settings.sttModel, engines.defaultStt) : settings.sttModel;
        const engine = engines.tts.length ? AudioVoiceState.pickOption(engines.tts.map(c => c.engine), settings.ttsEngine, engines.defaultTts) : settings.ttsEngine;
        setSelect(els.stt, stt, stt);
        setSelect(els.engine, engine, engine);
        const language = engines.languages.length ? AudioVoiceState.pickOption(engines.languages.map(c => c.id), settings.language, engines.defaultLanguage) : settings.language;
        setSelect(els.language, language, language);
        syncLanguage(); // after the recognizer: only a multilingual one takes a language
        fillVoices(settings.voice); // after the engine: the voice list is the engine's
        els.systemPrompt.value = settings.systemPrompt;
        els.promptDetails.open = !!settings.systemPrompt;
        els.bargeIn.checked = settings.bargeIn;
        els.ptt.checked = settings.pushToTalk;
        setPttKey(settings.pttKey);
        talking = false;
        applyMicGate(); // a preset loaded mid-call changes push-to-talk now
        showEngineNote();
        refreshControls();
    }

    function setSelect(select, value, label) {
        if (value && ![...select.options].some(o => o.value === value)) {
            select.add(new Option(label, value));
        }
        select.value = value || (select.options.length ? select.options[0].value : '');
    }

    /** The fields changed: remember them as the last-used settings. */
    function onSettingsChanged() {
        store.last = readSettings();
        persistStore();
        setState(callState);
    }

    // ===== UI =====

    function btn(parent, text, cls, onClick) {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = cls;
        b.innerText = text;
        if (onClick) {
            b.addEventListener('click', onClick);
        }
        parent.appendChild(b);
        return b;
    }

    function labeled(parent, labelText, input) {
        const wrap = createDiv(null, 'voice-agent-field');
        const label = createSpan(null, 'voice-agent-field-label', translate(labelText));
        wrap.appendChild(label);
        wrap.appendChild(input);
        parent.appendChild(wrap);
        return wrap;
    }

    function buildUi(container) {
        container.innerHTML = '';
        const root = createDiv(null, 'voice-agent-root');

        // --- Header: the orb, the state, the timer, and the call buttons ---
        const head = createDiv(null, 'voice-agent-head');
        els.orb = createDiv(null, 'voice-agent-orb voice-agent-tone-idle');
        els.orb.appendChild(createDiv(null, 'voice-agent-orb-ring'));
        els.orb.appendChild(createDiv(null, 'voice-agent-orb-core'));
        head.appendChild(els.orb);
        const status = createDiv(null, 'voice-agent-status');
        const stateRow = createDiv(null, 'voice-agent-state-row');
        els.state = createSpan(null, 'voice-agent-state voice-agent-state-idle', translate('Idle'));
        els.timer = createSpan(null, 'voice-agent-timer', '');
        els.micBadge = createSpan(null, 'voice-agent-mic-badge', '');
        els.micBadge.hidden = true;
        stateRow.appendChild(els.state);
        stateRow.appendChild(els.timer);
        stateRow.appendChild(els.micBadge);
        status.appendChild(stateRow);
        els.hint = createDiv(null, 'voice-agent-hint');
        status.appendChild(els.hint);
        head.appendChild(status);
        const buttons = createDiv(null, 'voice-agent-buttons');
        els.startBtn = btn(buttons, translate('Start'), 'basic-button voice-agent-start', onStartClick);
        els.muteBtn = btn(buttons, translate('Mute'), 'basic-button voice-agent-mute', onMuteClick);
        els.muteBtn.title = translate('Silence your microphone without ending the call');
        els.stopBtn = btn(buttons, translate('Stop'), 'basic-button voice-agent-stop', onStopClick);
        els.reconnectBtn = btn(buttons, translate('Reconnect'), 'basic-button voice-agent-reconnect', onStartClick);
        head.appendChild(buttons);
        root.appendChild(head);

        // --- Settings (folds away during a call) ---
        els.settings = document.createElement('details');
        els.settings.className = 'voice-agent-settings';
        els.settings.open = true;
        const summary = document.createElement('summary');
        summary.className = 'voice-agent-settings-summary';
        summary.innerText = translate('Call settings');
        els.settings.appendChild(summary);

        // Presets: load one from the list; Save stores the current settings under the name typed
        const presetRow = createDiv(null, 'voice-agent-presets');
        els.presetSel = document.createElement('select');
        els.presetSel.className = 'form-control voice-agent-select voice-agent-preset-select';
        els.presetSel.title = translate('Saved presets');
        els.presetSel.addEventListener('change', onPresetPicked);
        presetRow.appendChild(els.presetSel);
        els.presetName = document.createElement('input');
        els.presetName.type = 'text';
        els.presetName.maxLength = 40;
        els.presetName.className = 'form-control voice-agent-input voice-agent-preset-name';
        els.presetName.placeholder = translate('Preset name');
        els.presetName.addEventListener('input', refreshPresetButtons);
        els.presetName.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                onPresetSave();
            }
        });
        presetRow.appendChild(els.presetName);
        els.presetSave = btn(presetRow, translate('Save'), 'basic-button voice-agent-tool', onPresetSave);
        els.presetRename = btn(presetRow, translate('Rename'), 'basic-button voice-agent-tool', onPresetRename);
        els.presetDelete = btn(presetRow, translate('Delete'), 'basic-button voice-agent-tool', onPresetDelete);
        els.settings.appendChild(presetRow);

        const setup = createDiv(null, 'voice-agent-setup');

        // The language model comes from LLMAssistant; Refresh asks again (a provider may have started since the page loaded)
        els.model = document.createElement('select');
        els.model.className = 'form-control voice-agent-select';
        els.model.addEventListener('change', onSettingsChanged);
        const modelWrap = createDiv(null, 'voice-agent-with-button');
        modelWrap.appendChild(els.model);
        els.modelRefresh = btn(modelWrap, translate('Refresh'), 'basic-button voice-agent-tool', () => {
            loadModels();
            loadAssistants();
        });
        labeled(setup, 'Language model', modelWrap);

        els.assistantId = document.createElement('select');
        els.assistantId.className = 'form-control voice-agent-select';
        els.assistantId.add(new Option(translate('(default assistant)'), ''));
        els.assistantId.addEventListener('change', onSettingsChanged);
        labeled(setup, 'Assistant', els.assistantId);

        els.stt = document.createElement('select');
        els.stt.className = 'form-control voice-agent-select';
        els.stt.addEventListener('change', () => {
            syncLanguage();
            onSettingsChanged();
            showEngineNote();
        });
        labeled(setup, 'Hears you', els.stt);

        els.language = document.createElement('select');
        els.language.className = 'form-control voice-agent-select';
        els.language.addEventListener('change', () => {
            onSettingsChanged();
            showEngineNote();
        });
        labeled(setup, 'Language', els.language);

        els.engine = document.createElement('select');
        els.engine.className = 'form-control voice-agent-select';
        els.engine.addEventListener('change', () => {
            fillVoices(''); // the new engine's own voices; its default is picked
            onSettingsChanged();
            showEngineNote();
        });
        labeled(setup, 'Speaks with', els.engine);

        els.voice = document.createElement('select');
        els.voice.className = 'form-control voice-agent-select';
        els.voice.addEventListener('change', onSettingsChanged);
        const voiceWrap = createDiv(null, 'voice-agent-with-button');
        voiceWrap.appendChild(els.voice);
        els.previewBtn = btn(voiceWrap, translate('Preview'), 'basic-button voice-agent-tool', onPreviewClick);
        els.previewBtn.title = translate('Hear this voice');
        labeled(setup, 'Voice', voiceWrap);

        els.bargeIn = document.createElement('input');
        els.bargeIn.type = 'checkbox';
        els.bargeIn.checked = true;
        els.bargeIn.className = 'voice-agent-checkbox';
        els.bargeIn.addEventListener('change', onSettingsChanged);
        const bargeWrap = createDiv(null, 'voice-agent-field voice-agent-field-inline');
        bargeWrap.appendChild(els.bargeIn);
        bargeWrap.appendChild(createSpan(null, 'voice-agent-field-label', translate('Allow barge-in')));
        setup.appendChild(bargeWrap);

        // Push-to-talk: the microphone is open only while this key is held. The key button rebinds on the next key press.
        els.ptt = document.createElement('input');
        els.ptt.type = 'checkbox';
        els.ptt.className = 'voice-agent-checkbox';
        els.ptt.addEventListener('change', () => {
            talking = false;
            applyMicGate();
            onSettingsChanged();
        });
        const pttWrap = createDiv(null, 'voice-agent-field voice-agent-field-inline');
        pttWrap.appendChild(els.ptt);
        pttWrap.appendChild(createSpan(null, 'voice-agent-field-label', translate('Push-to-talk')));
        els.pttKeyBtn = btn(pttWrap, '', 'basic-button voice-agent-tool', onPttKeyBtnClick);
        els.pttKeyBtn.title = translate('Click, then press the key to hold while you talk');
        setup.appendChild(pttWrap);
        els.settings.appendChild(setup);
        els.modelStatus = createDiv(null, 'voice-agent-model-status');
        els.settings.appendChild(els.modelStatus);
        els.engineNote = createDiv(null, 'voice-agent-model-status');
        els.settings.appendChild(els.engineNote);

        // The system prompt is long and rarely changed, so it folds
        els.promptDetails = document.createElement('details');
        els.promptDetails.className = 'voice-agent-prompt-details';
        const promptSummary = document.createElement('summary');
        promptSummary.innerText = translate('System prompt');
        els.promptDetails.appendChild(promptSummary);
        els.systemPrompt = document.createElement('textarea');
        els.systemPrompt.className = 'form-control voice-agent-prompt';
        els.systemPrompt.rows = 3;
        els.systemPrompt.placeholder = translate('Leave blank for the assistant default');
        els.systemPrompt.addEventListener('input', onSettingsChanged);
        els.promptDetails.appendChild(els.systemPrompt);
        els.settings.appendChild(els.promptDetails);
        root.appendChild(els.settings);
        renderPresetList('');

        // --- Notices: chips you can dismiss ---
        els.notices = createDiv(null, 'voice-agent-notices');
        root.appendChild(els.notices);

        // --- Transcript ---
        const flowWrap = createDiv(null, 'voice-agent-flow-wrap');
        els.transcript = createDiv(null, 'voice-agent-transcript');
        els.transcript.addEventListener('scroll', () => {
            stickToBottom = els.transcript.scrollHeight - els.transcript.scrollTop - els.transcript.clientHeight <= STICK_SLACK_PX;
            if (stickToBottom) {
                els.jump.hidden = true;
            }
        });
        els.empty = createDiv(null, 'voice-agent-empty', translate('The conversation appears here. Press Start and speak.'));
        els.transcript.appendChild(els.empty);
        flowWrap.appendChild(els.transcript);
        els.jump = btn(flowWrap, translate('Jump to latest'), 'basic-button voice-agent-jump', () => scrollToLatest(true));
        els.jump.hidden = true;
        root.appendChild(flowWrap);

        // --- Typed messages: answered like speech; the server echoes them back as a "You" bubble ---
        const typeRow = createDiv(null, 'voice-agent-type');
        els.typeInput = document.createElement('input');
        els.typeInput.type = 'text';
        els.typeInput.maxLength = TYPED_MAX_CHARS;
        els.typeInput.className = 'form-control voice-agent-input voice-agent-type-input';
        els.typeInput.placeholder = translate('Type a message to the agent (Enter to send)');
        els.typeInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.isComposing) {
                e.preventDefault();
                onSendTyped();
            }
        });
        els.typeInput.addEventListener('input', refreshControls);
        typeRow.appendChild(els.typeInput);
        els.typeSend = btn(typeRow, translate('Send'), 'basic-button voice-agent-tool', onSendTyped);
        root.appendChild(typeRow);

        // --- Transcript tools ---
        const tools = createDiv(null, 'voice-agent-tools');
        els.copyBtn = btn(tools, translate('Copy'), 'basic-button voice-agent-tool', onCopyClick);
        els.clearBtn = btn(tools, translate('Clear'), 'basic-button voice-agent-tool', onClearClick);
        els.exportSel = document.createElement('select');
        els.exportSel.className = 'form-control voice-agent-export';
        for (const [value, label] of [['', translate('Export…')], ['md', 'Markdown'], ['txt', translate('Text')], ['json', 'JSON']]) {
            els.exportSel.add(new Option(label, value));
        }
        els.exportSel.addEventListener('change', () => {
            onExport(els.exportSel.value);
            els.exportSel.value = '';
        });
        tools.appendChild(els.exportSel);
        head.insertBefore(tools, buttons); // beside the call buttons: the bottom panel is short, the transcript needs the height

        container.appendChild(root);
    }

    // ===== Options: voices, language models, assistants =====

    /** Ask the server what the tab can offer (the voices, and whether LLMAssistant is there), then fill the lists. */
    function loadOptions() {
        genericRequest('AudioLabVoiceAgentOptions', {}, data => {
            fillEngines(data);
            if (data.llm_available === false) {
                llm.status = 'missing';
                llm.models = [];
                showModelStatus();
                setState(callState);
                return;
            }
            loadModels();
            loadAssistants();
        }, 0, () => {
            // The options route is this extension's own: if it fails the page is out of step with the server. Keep the
            // choices the settings hold and still try the models, so the tab degrades instead of going blank.
            loadModels();
            loadAssistants();
        });
    }

    /** Fill the recognizer and speech-engine lists from the server's options. A saved choice the server no longer lists, or none saved, becomes its default. */
    function fillEngines(data) {
        engines = {
            stt: data.stt || [],
            tts: data.tts || [],
            languages: data.languages || [],
            defaultStt: data.default_stt || '',
            defaultTts: data.default_tts || '',
            defaultLanguage: data.default_language || 'en'
        };
        const wantedStt = els.stt.value;
        els.stt.textContent = '';
        for (const choice of engines.stt) {
            els.stt.add(new Option(choice.name, choice.id));
        }
        els.stt.value = AudioVoiceState.pickOption(engines.stt.map(c => c.id), wantedStt, engines.defaultStt);
        const wantedLanguage = els.language.value;
        els.language.textContent = '';
        for (const choice of engines.languages) {
            els.language.add(new Option(choice.name, choice.id));
        }
        els.language.value = AudioVoiceState.pickOption(engines.languages.map(c => c.id), wantedLanguage, engines.defaultLanguage);
        syncLanguage();
        const wantedEngine = els.engine.value;
        els.engine.textContent = '';
        for (const choice of engines.tts) {
            els.engine.add(new Option(choice.name, choice.engine));
        }
        els.engine.value = AudioVoiceState.pickOption(engines.tts.map(c => c.engine), wantedEngine, engines.defaultTts);
        fillVoices(els.voice.value);
        showEngineNote();
        onSettingsChanged();
    }

    /** Whether the chosen recognizer hears a language other than English. False until the options arrive (the language is then not sent). */
    function sttIsMultilingual() {
        return !!engines.stt.find(c => c.id === els.stt.value)?.multilingual;
    }

    /** The language list follows the recognizer: an English-only one is shown as English and cannot be changed. */
    function syncLanguage() {
        if (!engines.stt.length) {
            return; // the options have not arrived: the recognizer's kind is unknown, so a saved language must not be reset to English yet
        }
        const multilingual = sttIsMultilingual();
        els.language.disabled = !multilingual;
        if (!multilingual) {
            els.language.value = engines.defaultLanguage;
        }
    }

    /** The chosen engine's offer, or null before the options arrive. */
    function currentEngine() {
        return engines.tts.find(c => c.engine === els.engine.value) || null;
    }

    /** The voice list of the chosen engine, grouped by language. `wanted` is kept when that engine has it; otherwise the engine's default. */
    function fillVoices(wanted) {
        const engine = currentEngine();
        if (!engine) {
            setSelect(els.voice, wanted, wanted); // options not here yet: keep what the settings hold
            return;
        }
        els.voice.textContent = '';
        for (const { group, voices: list } of AudioVoiceState.groupVoices(engine.voices)) {
            const optgroup = document.createElement('optgroup');
            optgroup.label = group;
            for (const voice of list) {
                optgroup.appendChild(new Option(voice.name, voice.id));
            }
            els.voice.appendChild(optgroup);
        }
        els.voice.value = AudioVoiceState.pickOption(engine.voices.map(v => v.id), wanted, engine.default_voice);
    }

    /** What the chosen recognizer and engine are good for, and that a model used for the first time is fetched. */
    function showEngineNote() {
        const stt = engines.stt.find(c => c.id === els.stt.value);
        const engine = currentEngine();
        const parts = [stt?.note, engine?.note].filter(Boolean).map(translate);
        if (sttIsMultilingual() && els.language.value && els.language.value !== engines.defaultLanguage) {
            // The speech engine speaks its voice's language, not the one it hears: the reply is only in that language if the voice is.
            parts.push(translate('The agent speaks in its voice\'s language: pick a voice that speaks') + ' ' + els.language.selectedOptions[0].text + '.');
        }
        if (parts.length) {
            parts.push(translate('The first call with a model not used before downloads it, which takes longer.'));
        }
        els.engineNote.textContent = parts.join(' ');
        els.engineNote.hidden = parts.length === 0;
    }

    /** Fetch LLMAssistant's models. Safe to call again (Refresh); the chosen model is kept when it is still offered. */
    function loadModels() {
        llm.status = 'loading';
        showModelStatus();
        setState(callState);
        els.modelRefresh.disabled = true;
        genericRequest('LLMAssistantGetModels', {}, data => {
            els.modelRefresh.disabled = false;
            const wanted = els.model.value || store.last.model; // the list may have been empty a moment ago, which cleared the field
            llm.models = (data.models || []).filter(m => m && m.id);
            llm.warnings = Array.isArray(data.warnings) ? data.warnings : [];
            els.model.textContent = '';
            for (const model of llm.models) {
                els.model.add(new Option(model.title || model.name || model.id, model.id));
            }
            llm.status = llm.models.length ? 'ready' : 'empty';
            if (llm.models.length) {
                const keep = llm.models.some(m => m.id === wanted);
                els.model.value = keep ? wanted : llm.models[0].id;
                if (wanted && !keep) {
                    pushNotice(`${translate('Saved model')} "${wanted}" ${translate('is not available; using')} "${els.model.value}".`, 'info');
                }
                onSettingsChanged();
            }
            showModelStatus();
            setState(callState);
        }, 0, (error) => {
            els.modelRefresh.disabled = false;
            llm.status = 'error';
            llm.warnings = typeof error === 'string' && error ? [error] : [];
            showModelStatus();
            setState(callState);
        });
    }

    /** Fetch LLMAssistant's assistants for the Assistant list; the default entry stays if there are none or it is not installed. */
    function loadAssistants() {
        genericRequest('LLMAssistantGetAssistants', {}, data => {
            const wanted = els.assistantId.value;
            els.assistantId.textContent = '';
            els.assistantId.add(new Option(translate('(default assistant)'), ''));
            for (const assistant of (data.assistants || [])) {
                if (assistant && assistant.id) {
                    els.assistantId.add(new Option(assistant.name || assistant.title || assistant.id, assistant.id));
                }
            }
            setSelect(els.assistantId, wanted, wanted);
        }, 0, () => { /* the list stays at "default assistant"; the model list reports the real problem */ });
    }

    /** The line under the setup fields: why there is no model, and any provider warnings. */
    function showModelStatus() {
        const reason = llm.status === 'ready' ? '' : AudioVoiceState.startBlocker({ status: llm.status, model: els.model.value });
        const lines = [reason, ...llm.warnings.map(String)].filter(Boolean).map(translate);
        els.modelStatus.textContent = lines.join(' ');
        els.modelStatus.classList.toggle('voice-agent-model-status-bad', llm.status === 'empty' || llm.status === 'missing' || llm.status === 'error');
        els.modelStatus.hidden = lines.length === 0;
        els.modelRefresh.hidden = llm.status === 'missing';
    }

    // ===== Presets =====

    /** The preset list: a "Presets" heading entry, then the saved names; `selected` is one to show chosen. */
    function renderPresetList(selected) {
        els.presetSel.textContent = '';
        els.presetSel.add(new Option(store.presets.length ? translate('Presets…') : translate('No presets yet'), ''));
        for (const preset of store.presets) {
            els.presetSel.add(new Option(preset.name, preset.name));
        }
        els.presetSel.value = AudioVoiceState.findPreset(store, selected)?.name || '';
        refreshPresetButtons();
    }

    /** Save needs a name, Rename a chosen preset and a name, Delete a chosen preset. */
    function refreshPresetButtons() {
        const chosen = els.presetSel.value;
        const name = AudioVoiceState.cleanPresetName(els.presetName.value);
        els.presetSave.disabled = !name;
        els.presetRename.disabled = !chosen || !name || name === chosen;
        els.presetDelete.disabled = !chosen;
    }

    function onPresetPicked() {
        const preset = AudioVoiceState.findPreset(store, els.presetSel.value);
        els.presetName.value = preset ? preset.name : '';
        if (preset) {
            applySettings(preset.settings);
            onSettingsChanged();
        }
        refreshPresetButtons();
    }

    function onPresetSave() {
        const saved = AudioVoiceState.savePreset(store, els.presetName.value, readSettings());
        if (!saved) {
            pushNotice(translate(els.presetName.value.trim() ? 'There is no room for another preset; delete one first.' : 'Type a name for the preset.'), 'info');
            return;
        }
        persistStore();
        renderPresetList(saved.name);
        pushNotice(`${translate('Saved preset')} "${saved.name}".`, 'info');
    }

    function onPresetRename() {
        const from = els.presetSel.value;
        if (!AudioVoiceState.renamePreset(store, from, els.presetName.value)) {
            pushNotice(translate('That name is already used by another preset.'), 'info');
            return;
        }
        persistStore();
        renderPresetList(els.presetName.value);
    }

    function onPresetDelete() {
        const name = els.presetSel.value;
        if (AudioVoiceState.deletePreset(store, name)) {
            persistStore();
            els.presetName.value = '';
            renderPresetList('');
            pushNotice(`${translate('Deleted preset')} "${name}".`, 'info');
        }
    }

    // ===== Voice preview =====

    /** Speak a short line in the chosen voice through the server's raw speech route (Kokoro), so the voice can be judged before a call. */
    function onPreviewClick() {
        if (AudioVoiceState.describeState(callState).live) {
            return; // it would play into the open microphone
        }
        stopPreview();
        const token = ++previewToken;
        els.previewBtn.disabled = true;
        previewCtx = previewCtx || new (window.AudioContext || window.webkitAudioContext)(); // made inside the click, so the browser lets it play
        const ctx = previewCtx;
        const finish = () => {
            if (token === previewToken) {
                els.previewBtn.disabled = false;
            }
        };
        genericRequest('AudioLabSpeakRaw', { text: PREVIEW_TEXT, provider_id: currentEngine()?.provider_id || 'kokoro_tts', voice: els.voice.value, sample_rate: OUTBOUND_SAMPLE_RATE }, async data => {
            try {
                const response = await fetch(new URL(data.url, document.baseURI));
                if (!response.ok) {
                    throw new Error(response.statusText);
                }
                const pcm = new Int16Array(await response.arrayBuffer());
                if (token !== previewToken) {
                    return;
                }
                if (pcm.length === 0) {
                    finish();
                    return;
                }
                await ctx.resume();
                const buffer = ctx.createBuffer(1, pcm.length, data.sample_rate || OUTBOUND_SAMPLE_RATE);
                const floats = buffer.getChannelData(0);
                for (let i = 0; i < pcm.length; i++) {
                    floats[i] = pcm[i] / 32768;
                }
                previewSource = ctx.createBufferSource();
                previewSource.buffer = buffer;
                previewSource.connect(ctx.destination);
                previewSource.onended = finish;
                previewSource.start();
            }
            catch (e) {
                pushNotice(`${translate('The voice preview could not play.')} ${e?.message || ''}`.trim(), 'error');
                finish();
            }
        }, 0, (error) => {
            pushNotice(`${translate('The voice preview needs this voice model downloaded.')} ${error || ''}`.trim(), 'error');
            finish();
        });
    }

    function stopPreview() {
        previewToken++;
        if (previewSource) {
            previewSource.onended = null;
            try { previewSource.stop(); } catch (e) { /* already finished */ }
            previewSource = null;
        }
        els.previewBtn.disabled = false;
    }

    /** Which buttons make sense for the call as it is now. */
    function refreshControls() {
        const live = AudioVoiceState.describeState(callState).live;
        const blocked = AudioVoiceState.startBlocker({ status: llm.status, model: els.model.value });
        els.startBtn.hidden = live || canReconnect;
        els.startBtn.disabled = !!blocked;
        els.startBtn.title = blocked ? translate(blocked) : '';
        els.reconnectBtn.disabled = !!blocked;
        els.reconnectBtn.title = els.startBtn.title;
        els.reconnectBtn.hidden = live || !canReconnect;
        els.stopBtn.hidden = !live;
        els.muteBtn.hidden = !live;
        const canType = AudioVoiceState.canType(callState, !!ws && ws.readyState === WebSocket.OPEN);
        els.typeInput.disabled = !canType;
        els.typeSend.disabled = !canType || !els.typeInput.value.trim();
        els.copyBtn.disabled = els.clearBtn.disabled = transcript.entries.length === 0;
        els.exportSel.disabled = transcript.entries.length === 0;
    }

    // ===== Call lifecycle =====

    /** Start (or reconnect) a call; one attempt at a time, since the mic and the worklet load are asynchronous. */
    async function onStartClick() {
        if (starting || (callState !== 'idle' && callState !== 'ended') || AudioVoiceState.startBlocker({ status: llm.status, model: els.model.value })) {
            return;
        }
        stopPreview();
        starting = true;
        const hadReconnect = canReconnect;
        els.startBtn.disabled = true;
        els.reconnectBtn.disabled = true;
        try {
            if (!await openCall()) {
                canReconnect = hadReconnect; // a failed attempt leaves the tab as it was, Reconnect included
            }
        }
        finally {
            starting = false;
            refreshControls();
        }
    }

    /** Open the microphone and the audio graph, then the socket. Returns false (after telling the user why) if it could not start. */
    async function openCall() {
        try {
            micStream = await navigator.mediaDevices.getUserMedia({
                audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
            });
        }
        catch (e) {
            reportError('mic', e);
            return false;
        }
        try {
            audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
            if (audioCtx.state === 'suspended') {
                await audioCtx.resume();
            }
            await audioCtx.audioWorklet.addModule(WORKLET_URL);
        }
        catch (e) {
            reportError('worklet', e);
            teardownAudio();
            return false;
        }
        muted = false; // a new call starts with the microphone open (or held shut by push-to-talk)
        talking = false;
        applyMicGate();
        micSource = audioCtx.createMediaStreamSource(micStream);
        analyserNode = audioCtx.createAnalyser();
        analyserNode.fftSize = 512;
        micSource.connect(analyserNode);
        outGain = audioCtx.createGain();
        outAnalyser = audioCtx.createAnalyser();
        outAnalyser.fftSize = 512;
        outGain.connect(audioCtx.destination);
        outGain.connect(outAnalyser);
        startMeter();

        const frameSize = Math.round(audioCtx.sampleRate * (CAPTURE_FRAME_MS / 1000));
        captureNode = new AudioWorkletNode(audioCtx, 'voice-capture-processor', { processorOptions: { frameSize } });
        captureNode.port.onmessage = (ev) => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(ev.data);
            }
        };
        micSource.connect(captureNode);

        canReconnect = false;
        resetCall();
        connectSocket(Math.round(audioCtx.sampleRate));
        return true;
    }

    function onStopClick() {
        if (ws) {
            const closing = ws;
            ws = null; // the close handler reads this to tell a requested stop from a dropped connection
            try {
                closing.send(JSON.stringify({ end: true }));
            }
            catch (e) { /* socket may already be closing */ }
            try {
                closing.close();
            }
            catch (e) { /* ignore */ }
        }
        endCallLocally(false);
    }

    /** Clear the previous call's transcript and start the clock. */
    function resetCall() {
        clearTranscript();
        callStartedAt = Date.now();
        els.settings.open = false;
        startTick();
        tick();
    }

    /** Make sure the once-a-second tick is running; it stops itself when there is no live call and no notice left to expire. */
    function startTick() {
        if (!tickTimer) {
            tickTimer = setInterval(tick, 1000);
        }
    }

    function endCallLocally(dropped) {
        ws = null;
        teardownAudio();
        flushAllScheduledAudio();
        canReconnect = !!dropped;
        setState(dropped ? 'ended' : 'idle');
        els.settings.open = true;
        refreshControls();
        tick();
    }

    /** Once a second: the call timer, and old informational notices leaving. */
    function tick() {
        if (callStartedAt && AudioVoiceState.describeState(callState).live) {
            els.timer.innerText = AudioVoiceState.clock((Date.now() - callStartedAt) / 1000);
        }
        if (AudioVoiceState.expireNotices(notices)) {
            renderNotices();
        }
        if (tickTimer && !AudioVoiceState.describeState(callState).live && !notices.list.some(n => n.severity !== 'error')) {
            clearInterval(tickTimer);
            tickTimer = null;
        }
    }

    function teardownAudio() {
        if (meterRaf) {
            cancelAnimationFrame(meterRaf);
            meterRaf = null;
        }
        if (captureNode) {
            try { captureNode.disconnect(); } catch (e) { /* ignore */ }
            captureNode = null;
        }
        if (micSource) {
            try { micSource.disconnect(); } catch (e) { /* ignore */ }
            micSource = null;
        }
        if (micStream) {
            for (const track of micStream.getTracks()) {
                track.stop();
            }
            micStream = null;
        }
        if (outGain) {
            try { outGain.disconnect(); } catch (e) { /* ignore */ }
            outGain = null;
            outAnalyser = null;
        }
        analyserNode = null;
        muted = false;
        talking = false;
        micLevel = outLevel = 0;
        els.orb.style.setProperty('--mic', '0');
        els.orb.style.setProperty('--out', '0');
        // The playback AudioContext stays alive across a call (nextPlayTime/scheduled sources use it, and a
        // fresh one would need another user gesture to unlock); it is only ever torn down implicitly by the
        // page unloading.
    }

    // ===== WebSocket =====

    function connectSocket(inputRate) {
        setState('connecting');
        const address = getWSAddress();
        if (!address) {
            reportError('address');
            endCallLocally(true);
            return;
        }
        const socket = new WebSocket(`${address}/API/AudioLabVoiceSession`);
        ws = socket;
        socket.binaryType = 'arraybuffer';
        socket.onopen = () => {
            const settings = readSettings();
            socket.send(JSON.stringify({
                session_id: session_id,
                model: settings.model,
                assistantId: settings.assistantId.trim() || undefined,
                voice: settings.voice,
                systemPrompt: settings.systemPrompt.trim() || undefined,
                bargeIn: settings.bargeIn,
                sttModel: settings.sttModel || undefined,
                ttsEngine: settings.ttsEngine || undefined,
                language: sttIsMultilingual() && settings.language ? settings.language : undefined,
                inputRate: inputRate,
            }));
        };
        socket.onmessage = (ev) => {
            if (ws === socket) { // a socket the user stopped may still deliver frames while it closes
                onSocketMessage(ev);
            }
        };
        // No onerror chip: the browser always follows an error with a close event, and that one explains it.
        socket.onclose = (ev) => {
            if (ws === socket) { // the user didn't request Stop; the server/connection ended the call
                const why = AudioVoiceState.explainError('socket', ev);
                pushNotice(`${translate(why.title)} ${why.hint ? translate(why.hint) : ''}`.trim(), 'error');
                endCallLocally(true);
            }
        };
    }

    function onSocketMessage(ev) {
        if (ev.data instanceof ArrayBuffer) {
            playOutboundFrame(ev.data);
            return;
        }
        let data;
        try {
            data = JSON.parse(ev.data);
        }
        catch (e) {
            return;
        }
        if (data.state) {
            if (!AudioVoiceState.describeState(data.state).live && ws) {
                // The server says the call is over (Ended/Idle): release the mic and the socket now instead of waiting for a close.
                const closing = ws;
                ws = null;
                try {
                    closing.close();
                }
                catch (e) { /* already closing */ }
                endCallLocally(false);
            }
            setState(data.state);
        }
        else if (data.transcript) {
            addTranscript(data.transcript.role, data.transcript.text, data.transcript.turnId);
        }
        else if (data.bargein) {
            flushTurn(data.bargein.turnId);
            pushNotice(translate('Interrupted: the agent stopped speaking.'), 'info');
        }
        else if (data.tool_call) {
            addTool({ id: data.tool_call.id, name: data.tool_call.name, args: data.tool_call.arguments || '', turnId: data.tool_call.turnId });
        }
        else if (data.tool_result) {
            addTool({ id: data.tool_result.id, name: data.tool_result.name, result: data.tool_result.result || '', turnId: data.tool_result.turnId });
        }
        else if (data.notice) {
            pushNotice(data.notice, 'info');
        }
        else if (data.metrics) {
            addMetrics(data.metrics);
        }
        else if (data.error) {
            pushNotice(data.error, 'error');
        }
    }

    // ===== Outbound playback (turn-tagged PCM16 @ 24 kHz) =====

    function playOutboundFrame(buffer) {
        if (buffer.byteLength <= 4 || !audioCtx) {
            return;
        }
        const view = new DataView(buffer);
        const turnId = view.getInt32(0, true); // little-endian, matches VoiceOutboundFrame.Encode
        const pcm = new Int16Array(buffer, 4);
        const floats = new Float32Array(pcm.length);
        for (let i = 0; i < pcm.length; i++) {
            floats[i] = pcm[i] / 32768;
        }
        const audioBuffer = audioCtx.createBuffer(1, floats.length, OUTBOUND_SAMPLE_RATE);
        audioBuffer.copyToChannel(floats, 0);
        const source = audioCtx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(outGain || audioCtx.destination);
        const now = audioCtx.currentTime;
        if (nextPlayTime < now) {
            nextPlayTime = now;
        }
        source.start(nextPlayTime);
        const entry = { turnId: turnId, source: source };
        scheduledSources.push(entry);
        source.onended = () => {
            const idx = scheduledSources.indexOf(entry);
            if (idx >= 0) {
                scheduledSources.splice(idx, 1);
            }
        };
        nextPlayTime += audioBuffer.duration;
    }

    /** Drops queued/playing audio for turnId and anything older, same rule the server's own pump uses. */
    function flushTurn(turnId) {
        for (let i = scheduledSources.length - 1; i >= 0; i--) {
            if (scheduledSources[i].turnId !== 0 && scheduledSources[i].turnId <= turnId) {
                try { scheduledSources[i].source.stop(); } catch (e) { /* already finished */ }
                scheduledSources.splice(i, 1);
            }
        }
        if (audioCtx) {
            nextPlayTime = audioCtx.currentTime;
        }
    }

    function flushAllScheduledAudio() {
        for (const entry of scheduledSources) {
            try { entry.source.stop(); } catch (e) { /* already finished */ }
        }
        scheduledSources = [];
        nextPlayTime = 0;
    }

    // ===== Typed messages =====

    /** Send the typed message over the call's socket and clear the box; the server's own transcript event draws the bubble. */
    function onSendTyped() {
        const message = els.typeInput.value.trim();
        if (!message || !AudioVoiceState.canType(callState, !!ws && ws.readyState === WebSocket.OPEN)) {
            return;
        }
        ws.send(JSON.stringify({ text: message }));
        els.typeInput.value = '';
        refreshControls();
        els.typeInput.focus();
    }

    // ===== Microphone gating: mute and push-to-talk =====

    /** Whether the call is live and its tab is the visible one, so the push-to-talk key means something. */
    function pttApplies() {
        return els.ptt.checked && AudioVoiceState.describeState(callState).live && !!micStream && els.orb.offsetParent !== null;
    }

    /** The microphone follows `micOpen`: a closed one is a disabled track, which sends silence, so the server's voice detector keeps a steady stream and hears no speech. */
    function applyMicGate() {
        const gate = { muted, pushToTalk: els.ptt.checked, talking, pttKey: els.pttKeyBtn.dataset.code };
        const live = AudioVoiceState.describeState(callState).live;
        const open = AudioVoiceState.micOpen(gate);
        if (micStream) {
            for (const track of micStream.getAudioTracks()) {
                track.enabled = open;
            }
        }
        const label = AudioVoiceState.micLabel(gate);
        els.micBadge.hidden = !live || !label.text;
        els.micBadge.textContent = label.tone === 'ptt' ? `${translate('Hold')} ${AudioVoiceState.keyLabel(gate.pttKey)} ${translate('to talk')}` : translate(label.text);
        els.micBadge.className = `voice-agent-mic-badge voice-agent-mic-badge-${label.tone}`;
        els.orb.classList.toggle('voice-agent-orb-closed', live && !open);
        els.muteBtn.textContent = translate(muted ? 'Unmute' : 'Mute');
        els.muteBtn.setAttribute('aria-pressed', muted ? 'true' : 'false');
        els.muteBtn.classList.toggle('voice-agent-mute-on', muted);
    }

    function onMuteClick() {
        muted = !muted;
        if (muted) {
            talking = false;
        }
        applyMicGate();
    }

    /** Let go of the talk key (also when the window loses focus, where the key-up would never arrive). */
    function releaseTalk() {
        if (talking) {
            talking = false;
            applyMicGate();
        }
    }

    function setPttKey(code) {
        const key = AudioVoiceState.isUsablePttKey(code) ? code : AudioVoiceState.DEFAULT_SETTINGS.pttKey;
        els.pttKeyBtn.dataset.code = key;
        els.pttKeyBtn.textContent = rebindingKey ? translate('Press a key…') : AudioVoiceState.keyLabel(key);
    }

    function onPttKeyBtnClick() {
        rebindingKey = !rebindingKey;
        setPttKey(els.pttKeyBtn.dataset.code);
    }

    /** Typing in a field must never open the microphone. */
    function isTypingTarget(target) {
        return !!target && (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable);
    }

    /** Stop waiting for a new push-to-talk key. */
    function cancelRebind() {
        if (rebindingKey) {
            rebindingKey = false;
            setPttKey(els.pttKeyBtn.dataset.code);
        }
    }

    function onPttKeyDown(e) {
        if (rebindingKey && (isTypingTarget(e.target) || els.orb.offsetParent === null)) {
            cancelRebind(); // the user went elsewhere (a text field, another tab): never swallow their keys
        }
        if (rebindingKey) {
            e.preventDefault();
            e.stopPropagation();
            if (e.code !== 'Escape' && AudioVoiceState.isUsablePttKey(e.code)) {
                rebindingKey = false;
                setPttKey(e.code);
                onSettingsChanged();
            }
            else if (e.code === 'Escape') {
                rebindingKey = false;
                setPttKey(els.pttKeyBtn.dataset.code);
            }
            return;
        }
        if (e.code !== els.pttKeyBtn.dataset.code || e.ctrlKey || e.metaKey || e.altKey || isTypingTarget(e.target) || !pttApplies()) {
            return;
        }
        e.preventDefault();
        e.stopPropagation(); // the DAW's shortcuts must not also fire for this key
        if (!e.repeat && !talking) {
            talking = true;
            applyMicGate();
        }
    }

    function onPttKeyUp(e) {
        if (talking && e.code === els.pttKeyBtn.dataset.code) {
            e.preventDefault();
            e.stopPropagation();
            releaseTalk();
        }
    }

    // ===== Level meter and orb =====

    /** The loudest sample of an analyser's window, 0 to 1. */
    function peakOf(analyser, data) {
        analyser.getByteTimeDomainData(data);
        let peak = 0;
        for (let i = 0; i < data.length; i++) {
            peak = Math.max(peak, Math.abs(data[i] - 128));
        }
        return Math.min(1, peak / 100);
    }

    function startMeter() {
        const micData = new Uint8Array(analyserNode.fftSize);
        const outData = new Uint8Array(outAnalyser.fftSize);
        const step = () => {
            if (!analyserNode) {
                return;
            }
            micLevel = AudioVoiceState.smoothLevel(micLevel, peakOf(analyserNode, micData));
            outLevel = AudioVoiceState.smoothLevel(outLevel, outAnalyser ? peakOf(outAnalyser, outData) : 0);
            els.orb.style.setProperty('--mic', micLevel.toFixed(3));
            els.orb.style.setProperty('--out', outLevel.toFixed(3));
            meterRaf = requestAnimationFrame(step);
        };
        meterRaf = requestAnimationFrame(step);
    }

    // ===== State, transcript, notices =====

    function setState(state) {
        callState = state;
        const info = AudioVoiceState.describeState(state);
        els.state.innerText = translate(info.label);
        els.state.className = `voice-agent-state voice-agent-state-${info.key}`;
        els.orb.className = `voice-agent-orb voice-agent-tone-${info.tone}`;
        const blocked = info.live ? '' : AudioVoiceState.startBlocker({ status: llm.status, model: els.model.value });
        els.hint.innerText = translate(blocked || info.hint); // when idle and not ready to call, say what is missing
        if (!info.live) {
            els.timer.innerText = '';
        }
        refreshControls();
        applyMicGate(); // setState replaced the orb's classes
    }

    function clearTranscript() {
        transcript = AudioVoiceState.createTranscript();
        bubbleEls.clear();
        els.transcript.innerHTML = '';
        els.transcript.appendChild(els.empty);
        els.empty.hidden = false;
        stickToBottom = true;
        els.jump.hidden = true;
        refreshControls();
    }

    function onClearClick() {
        clearTranscript();
    }

    /** Keep the newest bubble in view while the user is following; otherwise offer a way back. */
    function scrollToLatest(force) {
        if (force || stickToBottom) {
            els.transcript.scrollTop = els.transcript.scrollHeight;
            stickToBottom = true;
            els.jump.hidden = true;
        }
        else {
            els.jump.hidden = false;
        }
    }

    /** Make or refresh the element for a bubble. */
    function drawBubble(entry) {
        let el = bubbleEls.get(entry);
        if (!el) {
            el = createDiv(null, `voice-agent-bubble voice-agent-bubble-${entry.kind}`);
            const who = createSpan(null, 'voice-agent-bubble-who');
            who.innerText = entry.kind === 'user' ? translate('You') : translate('Agent');
            const when = createSpan(null, 'voice-agent-bubble-time', AudioVoiceState.timeOfDay(entry.time));
            const head = createDiv(null, 'voice-agent-bubble-head');
            head.appendChild(who);
            head.appendChild(when);
            el.appendChild(head);
            el.appendChild(createDiv(null, 'voice-agent-bubble-text'));
            el.appendChild(createDiv(null, 'voice-agent-bubble-latency'));
            bubbleEls.set(entry, el);
            els.transcript.appendChild(el);
        }
        el.querySelector('.voice-agent-bubble-text').innerText = AudioVoiceState.textOf(entry);
        const lat = el.querySelector('.voice-agent-bubble-latency');
        lat.textContent = '';
        for (const item of AudioVoiceState.latencyOf(entry.metrics)) {
            lat.appendChild(createSpan(null, 'voice-agent-latency-cell', `${translate(item.label)} ${item.text}`));
        }
        return el;
    }

    function addTranscript(role, text, turnId) {
        const { entry } = AudioVoiceState.addTranscript(transcript, role, text, turnId);
        els.empty.hidden = true;
        if (entry.kind === 'assistant') {
            const held = AudioVoiceState.takePendingMetrics(transcript, entry.turnId);
            if (held) {
                entry.metrics = held;
            }
        }
        drawBubble(entry);
        scrollToLatest(false);
        refreshControls();
    }

    function addTool(call) {
        const entry = AudioVoiceState.addTool(transcript, call);
        els.empty.hidden = true;
        let el = bubbleEls.get(entry);
        if (!el) {
            el = document.createElement('details');
            el.className = 'voice-agent-tool-row';
            el.appendChild(document.createElement('summary'));
            el.appendChild(createDiv(null, 'voice-agent-tool-body'));
            bubbleEls.set(entry, el);
            els.transcript.appendChild(el);
        }
        el.querySelector('summary').innerText = `${translate('Tool')}: ${entry.name}${entry.result === null ? '…' : ''}`;
        el.querySelector('.voice-agent-tool-body').innerText = `${entry.args || ''}${entry.result !== null ? `\n→ ${entry.result}` : ''}`;
        scrollToLatest(false);
        refreshControls();
    }

    function addMetrics(metrics) {
        const bubble = AudioVoiceState.addMetrics(transcript, metrics);
        if (bubble) {
            drawBubble(bubble);
            scrollToLatest(false); // the latency line made the bubble taller
        }
    }

    function pushNotice(text, severity) {
        AudioVoiceState.pushNotice(notices, text, severity);
        startTick();
        renderNotices();
    }

    /** Report a failure the way the user can act on it: what happened, then what to do. */
    function reportError(kind, error) {
        const why = AudioVoiceState.explainError(kind, error);
        pushNotice(`${translate(why.title)}${why.hint ? ' ' + translate(why.hint) : ''}`, 'error');
    }

    function renderNotices() {
        els.notices.textContent = '';
        for (const notice of notices.list) {
            const chip = createDiv(null, `voice-agent-notice voice-agent-notice-${notice.severity}`);
            chip.appendChild(createSpan(null, 'voice-agent-notice-text', notice.count > 1 ? `${notice.text} (×${notice.count})` : notice.text));
            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'voice-agent-notice-close';
            close.innerText = '×';
            close.title = translate('Dismiss');
            close.addEventListener('click', () => {
                AudioVoiceState.dismissNotice(notices, notice.id);
                renderNotices();
            });
            chip.appendChild(close);
            els.notices.appendChild(chip);
        }
    }

    // ===== Transcript tools =====

    function onCopyClick() {
        const text = AudioVoiceState.toText(transcript, { user: translate('You'), assistant: translate('Agent') });
        copyText(text); // core helper: also works over plain-HTTP LAN, where navigator.clipboard does not exist
        pushNotice(translate('Copied the conversation.'), 'info');
    }

    function onExport(kind) {
        if (!kind) {
            return;
        }
        const labels = { user: translate('You'), assistant: translate('Agent') };
        const body = kind === 'json' ? AudioVoiceState.toJson(transcript)
            : kind === 'md' ? AudioVoiceState.toMarkdown(transcript, labels) : AudioVoiceState.toText(transcript, labels);
        const url = URL.createObjectURL(new Blob([body], { type: kind === 'json' ? 'application/json' : 'text/plain' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = `voice-call.${kind}`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    return { render };
})();
