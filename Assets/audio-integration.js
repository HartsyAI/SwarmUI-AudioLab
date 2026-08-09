/**
 * AudioLab Feature Handler for SwarmUI
 * Integrates audio processing models with SwarmUI's Generate tab feature system.
 * Mirrors the pattern from SwarmUI-API-Backends/Assets/api-backends.js.
 */

const AudioLabConfig = {
    /** Maps model class IDs (curArch) to their category flag and provider-specific flag. */
    archToCategory: {
        kokoro_tts: { category: 'audiolab_tts', providerFlag: 'kokoro_tts_params' },
        chatterbox_tts: { category: 'audiolab_tts', providerFlag: 'chatterbox_tts_params', extraFlags: ['tts_sampling'] }, // no tts_voice_ref: engine lacks the voice-encoder front-end
        bark_tts: { category: 'audiolab_tts', providerFlag: 'bark_tts_params' },
        piper_tts: { category: 'audiolab_tts', providerFlag: 'piper_tts_params' },
        dia_tts: { category: 'audiolab_tts', providerFlag: 'dia_tts_params', extraFlags: ['tts_sampling'] },
        csm_tts: { category: 'audiolab_tts', providerFlag: 'csm_tts_params', extraFlags: ['tts_sampling'] },
        orpheus_tts: { category: 'audiolab_tts', providerFlag: 'orpheus_tts_params', extraFlags: ['tts_sampling'] },
        vibevoice_tts: { category: 'audiolab_tts', providerFlag: 'vibevoice_tts_params', extraFlags: ['tts_voice_ref'] },
        zonos_tts: { category: 'audiolab_tts', providerFlag: 'zonos_tts_params', extraFlags: ['tts_voice_ref'] },
        f5_tts: { category: 'audiolab_tts', providerFlag: 'f5_tts_params', extraFlags: ['tts_voice_ref'] },
        neutts_tts: { category: 'audiolab_tts', extraFlags: ['tts_voice_ref'] },
        cosyvoice_tts: { category: 'audiolab_tts', providerFlag: 'cosyvoice_tts_params', extraFlags: ['tts_voice_ref'] },
        pockettts_tts: { category: 'audiolab_tts', providerFlag: 'pockettts_tts_params', extraFlags: ['tts_voice_ref'] },
        kyutaitts_tts: { category: 'audiolab_tts', providerFlag: 'kyutaitts_tts_params', extraFlags: ['tts_voice_ref'] },
        fishspeech_tts: { category: 'audiolab_tts', providerFlag: 'fishspeech_tts_params', extraFlags: ['tts_voice_ref'] },
        qwen3_tts_clone: { category: 'audiolab_tts', providerFlag: 'qwen3tts_tts_params', extraFlags: ['tts_voice_ref'] },
        qwen3_tts_custom: { category: 'audiolab_tts', providerFlag: 'qwen3tts_tts_params', extraFlags: ['qwen3tts_speaker_params', 'qwen3tts_instruct_params'] },
        qwen3_tts_design: { category: 'audiolab_tts', providerFlag: 'qwen3tts_tts_params', extraFlags: ['qwen3tts_instruct_params'] },
        sparktts_tts: { category: 'audiolab_tts', extraFlags: ['sparktts_create_params', 'tts_voice_ref'] },
        styletts2_tts: { category: 'audiolab_tts', providerFlag: 'styletts2_tts_params', extraFlags: ['tts_voice_ref', 'styletts2_clone_params'] },
        melotts_tts: { category: 'audiolab_tts', providerFlag: 'melotts_tts_params' },
        zipvoice_tts: { category: 'audiolab_tts', providerFlag: 'zipvoice_tts_params', extraFlags: ['tts_voice_ref'] },
        // Provider-level fallback: every Qwen3 model overrides ModelClassId, but an unmapped arch
        // hides ALL audio params, so keep a safe entry in case a future variant forgets to.
        qwen3_tts: { category: 'audiolab_tts', providerFlag: 'qwen3tts_tts_params' },
        whisper_stt: { category: 'audiolab_stt', providerFlag: 'whisper_stt_params' },
        whisperstreaming_stt: { category: 'audiolab_stt' },
        moonshinestreaming_stt: { category: 'audiolab_stt' },
        kyutaistt_stt: { category: 'audiolab_stt' },
        distilwhisper_stt: { category: 'audiolab_stt' },
        moonshine_stt: { category: 'audiolab_stt' },
        realtimestt_stt: { category: 'audiolab_stt' },
        musicgen_music: { category: 'audiolab_audiogen', extraFlags: ['audiocraft_sampling'] },
        // `text2audio` unlocks core's Text2Audio group (duration/BPM/key/time-sig/language/style), which
        // BuildEngineArgs already prefers over AudioLab's bespoke equivalents. Only ACE-Step gets it —
        // every param in that group is meaningful here, which isn't true of the other music providers.
        acestep_music: { category: 'audiolab_audiogen', providerFlag: 'acestep_music_params', extraFlags: ['acestep_lm_params', 'acestep_task_params', 'music_instrumental_param', 'text2audio'] },
        openvoice_clone: { category: 'audiolab_clone' },
        rvc_clone: { category: 'audiolab_clone', providerFlag: 'rvc_clone_params' },
        gptsovits_clone: { category: 'audiolab_clone', providerFlag: 'gptsovits_clone_params' },
        demucs_fx: { category: 'audiolab_audioproc', providerFlag: 'demucs_fx_params' },
        resemble_enhance_fx: { category: 'audiolab_audioproc', providerFlag: 'resemble_enhance_fx_params' },
        audiogen_sfx: { category: 'audiolab_audiogen', extraFlags: ['audiocraft_sampling'] },
        yue_music: { category: 'audiolab_audiogen', providerFlag: 'yue_music_params' },
        heartlib_music: { category: 'audiolab_audiogen', providerFlag: 'heartlib_music_params' },
        stableaudio_music: { category: 'audiolab_audiogen', providerFlag: 'stableaudio_music_params' },
        // API TTS providers
        elevenlabs_tts: { category: 'audiolab_tts', providerFlag: 'elevenlabs_tts_params' },
        openai_tts: { category: 'audiolab_tts', providerFlag: 'openai_tts_params' },
        google_tts: { category: 'audiolab_tts', providerFlag: 'google_tts_params' },
        azure_tts: { category: 'audiolab_tts', providerFlag: 'azure_tts_params' },
        amazon_polly: { category: 'audiolab_tts', providerFlag: 'polly_tts_params' },
        deepgram_tts: { category: 'audiolab_tts', providerFlag: 'deepgram_tts_params' },
        cartesia_tts: { category: 'audiolab_tts', providerFlag: 'cartesia_tts_params' },
        playht_tts: { category: 'audiolab_tts', providerFlag: 'playht_tts_params' },
        // API STT providers
        openai_stt: { category: 'audiolab_stt', providerFlag: 'openai_stt_params' },
        google_stt: { category: 'audiolab_stt', providerFlag: 'google_stt_params' },
        azure_stt: { category: 'audiolab_stt', providerFlag: 'azure_stt_params' },
        aws_transcribe: { category: 'audiolab_stt' },
        assemblyai_stt: { category: 'audiolab_stt', providerFlag: 'assemblyai_stt_params' },
        deepgram_stt: { category: 'audiolab_stt', providerFlag: 'deepgram_stt_params' },
        // API audio generation providers
        elevenlabs_sfx: { category: 'audiolab_audiogen', providerFlag: 'elevenlabs_sfx_params' },
        suno_music: { category: 'audiolab_audiogen', extraFlags: ['music_style_params', 'music_instrumental_param'] },
        udio_music: { category: 'audiolab_audiogen', extraFlags: ['music_style_params'] },
        // API voice conversion providers
        elevenlabs_vc: { category: 'audiolab_clone', providerFlag: 'elevenlabs_vc_params' },
        // API audio processing providers
        elevenlabs_isolator: { category: 'audiolab_audioproc' },
        dolby_audioproc: { category: 'audiolab_audioproc', providerFlag: 'dolby_audioproc_params' }
    },

    categoryFlags: ['audiolab_tts', 'audiolab_stt', 'audiolab_audiogen', 'audiolab_clone', 'audiolab_audioproc'],

    /** Core image params to hide when an audio model is selected. */
    coreParamsToHide: [
        'steps', 'cfgscale', 'width', 'height', 'sidelength', 'aspectratio',
        'batchsize', 'initimage', 'initimagecreativity', 'initimageresettonorm',
        'initimagenoise', 'maskimage', 'maskblur', 'maskgrow', 'maskshrinkgrow',
        'useinpaintingencode', 'initimagerecompositemask', 'unsamplepprompt', 'zeronegative',
        'seamlesstileable', 'cascadelatentcompression', 'sd3textencs',
        'fluxguidancescale', 'fluxdisableguidance', 'clipstopatlayer',
        'vaetilesize', 'vaetileoverlap', 'removebackground', 'automaticvae',
        'modelspecificenhancements'
    ],

    /** Core parameter groups that are image-only. */
    coreGroupsToHide: [
        'resolution',
        'refineupscale',
        'controlnet',
        'swarminternal',
        'advancedvideo',
        'videoextend',
        'advancedmodeladdons',
        'magicpromptautoenable',
        'dynamicthresholding',
        'regionalprompting',
        'segmentrefining',
        'segmentparamoverrides',
        'advancedsampling',
        'initimage',
        'freeu',
        'texttovideo',
        'alternateguidance',
        'sampling',
        'variation'
    ],

    /** Image-only feature flags incompatible with audio models. */
    incompatibleFlags: [
        'sampling', 'zero_negative', 'refiners', 'controlnet', 'variation_seed',
        'video', 'autowebui', 'comfyui', 'frameinterps', 'ipadapter', 'sdxl',
        'dynamic_thresholding', 'cascade', 'sd3', 'flux-dev', 'seamless',
        'freeu', 'teacache', 'text2video', 'yolov8', 'aitemplate', 'sdcpp'
    ],

    /** Check if the given architecture is an AudioLab model. */
    isAudioModel(arch) {
        return arch in this.archToCategory;
    },

    /** Get all audio-related flags (categories + all provider flags + extra flags). */
    get allAudioFlags() {
        const flags = new Set(this.categoryFlags);
        for (const v of Object.values(this.archToCategory)) {
            if (v.providerFlag) flags.add(v.providerFlag);
            if (v.extraFlags) v.extraFlags.forEach(f => flags.add(f));
        }
        return [...flags];
    },

    /** Get all provider IDs (architecture keys). */
    get allArchIds() {
        return Object.keys(this.archToCategory);
    }
};

/** Controls parameter visibility for audio models via SwarmUI's feature flag system. */
featureSetChangers.push(() => {
    if (!gen_param_types) {
        return [[], []];
    }

    const curArch = currentModelHelper.curArch;
    const isAudioModel = AudioLabConfig.isAudioModel(curArch);

    for (const param of gen_param_types) {
        if (AudioLabConfig.coreParamsToHide.includes(param.id)) {
            if (isAudioModel) {
                if (!param.hasOwnProperty('original_feature_flag_audiolab')) {
                    param.original_feature_flag_audiolab = param.feature_flag;
                }
                param.feature_flag = '__audiolab_incompatible__';
            } else if (param.hasOwnProperty('original_feature_flag_audiolab')) {
                param.feature_flag = param.original_feature_flag_audiolab;
                delete param.original_feature_flag_audiolab;
            }
        }
        let inHiddenGroup = false;
        let currentGroup = param.group;
        while (currentGroup) {
            if (AudioLabConfig.coreGroupsToHide.includes(currentGroup.id)) {
                inHiddenGroup = true;
                break;
            }
            currentGroup = currentGroup.parent;
        }
        if (inHiddenGroup) {
            if (isAudioModel) {
                if (!param.hasOwnProperty('original_feature_flag_audiolab_group')) {
                    param.original_feature_flag_audiolab_group = param.feature_flag;
                }
                param.feature_flag = '__audiolab_incompatible__';
            } else if (param.hasOwnProperty('original_feature_flag_audiolab_group')) {
                param.feature_flag = param.original_feature_flag_audiolab_group;
                delete param.original_feature_flag_audiolab_group;
            }
        }
    }

    if (!isAudioModel) {
        // `text2audio` is a CORE flag that core itself grants to native ace-step-1_5 models. Removes are
        // applied after adds, so listing it here would undo core's own grant — leave it to core.
        return [[], AudioLabConfig.allAudioFlags.filter(f => f != 'text2audio')];
    }

    const config = AudioLabConfig.archToCategory[curArch];
    const activeExtraFlags = config.extraFlags || [];
    const activeSet = new Set([config.category, ...activeExtraFlags]);
    if (config.providerFlag) {
        activeSet.add(config.providerFlag);
    }
    const otherAudioFlags = AudioLabConfig.allAudioFlags.filter(f => !activeSet.has(f));
    const removeFlags = [...AudioLabConfig.incompatibleFlags, ...otherAudioFlags];
    const addFlags = ['prompt', ...activeSet];

    return [addFlags, removeFlags];
});

/** Auto-play queue for streaming TTS chunks. */
const AudioStreamPlayer = {
    /** @type {string[]} */
    queue: [],
    /** @type {HTMLAudioElement|null} */
    current: null,
    /** @type {boolean} */
    active: false,

    /** Reset state and prepare for a new streaming session. */
    start() {
        this.stop();
        this.active = true;
        console.log('[audiolab] Stream playback started');
    },

    /** Add a chunk to the queue; begin playback if idle. */
    enqueue(dataUrl) {
        if (!this.active) return;
        this.queue.push(dataUrl);
        if (!this.current) {
            this.playNext();
        }
    },

    /** Play the next chunk. Chains via the 'ended' event. */
    playNext() {
        if (!this.active || this.queue.length === 0) {
            this.current = null;
            return;
        }
        const src = this.queue.shift();
        const audio = new Audio(src);
        this.current = audio;
        audio.addEventListener('ended', () => {
            this.current = null;
            this.playNext();
        });
        audio.addEventListener('error', (e) => {
            console.warn('[audiolab] Chunk playback error:', e);
            this.current = null;
            this.playNext();
        });
        audio.play().catch(err => {
            console.warn('[audiolab] Autoplay blocked:', err.message);
            this.current = null;
            this.playNext();
        });
    },

    /** Stop playback and clear queue. */
    stop() {
        this.active = false;
        if (this.current) {
            this.current.pause();
            this.current = null;
        }
        this.queue = [];
    }
};

/** Hook into doGenerate + internalHandleData for streaming audio playback. */
setTimeout(() => {
    const handler = typeof mainGenHandler !== 'undefined' ? mainGenHandler
                  : typeof genHandler !== 'undefined' ? genHandler : null;
    if (!handler) {
        console.warn('[audiolab] No generate handler found, streaming hooks not installed');
        return;
    }

    const origDoGenerate = handler.doGenerate.bind(handler);
    handler.doGenerate = function(input_overrides = {}, input_preoverrides = {}, postCollectRun = null) {
        const curArch = currentModelHelper.curArch;
        if (AudioLabConfig.isAudioModel(curArch)) {
            const config = AudioLabConfig.archToCategory[curArch];
            if (config.category === 'audiolab_tts') {
                const streamParam = document.getElementById('input_streamchunksize');
                if (streamParam && parseInt(streamParam.value) > 0) {
                    AudioStreamPlayer.start();
                }
            }
        }
        return origDoGenerate(input_overrides, input_preoverrides, postCollectRun);
    };

    const origHandleData = handler.internalHandleData.bind(handler);
    handler.internalHandleData = function(data, images, discardable, timeLastGenHit, actualInput, socketId, socket, isPreview, batch_id) {
        if (data.image && AudioStreamPlayer.active) {
            const batchIdx = parseInt(data.batch_index);
            if (batchIdx < 0 && isAudioExt(data.image)) {
                AudioStreamPlayer.enqueue(data.image);
            }
        }
        if (data.socket_intention === 'close') {
            AudioStreamPlayer.stop();
        }
        return origHandleData(data, images, discardable, timeLastGenHit, actualInput, socketId, socket, isPreview, batch_id);
    };

    console.log('[audiolab] Streaming audio hooks installed');
}, 600);

setTimeout(() => {
    reviseBackendFeatureSet();
    hideUnsupportableParams();
}, 500);

/** Category display order and labels for the engine manager UI. */
const ENGINE_CATEGORIES = [
    { key: 'TTS', label: 'Text-to-Speech' },
    { key: 'STT', label: 'Speech-to-Text' },
    { key: 'AudioGeneration', label: 'Audio Generation' },
    { key: 'VoiceConversion', label: 'Voice Conversion' },
    { key: 'AudioProcessing', label: 'Audio Processing' }
];

let audioLabEngineData = null;
let audioLabRetryTimer = null;

/** Loads engine list from the API and caches it. Retries if backend isn't ready. */
function audioLabLoadEngines(callback) {
    genericRequest('AudioLabListEngines', {}, data => {
        if (data.success) {
            audioLabEngineData = data.engines;
            if (callback) callback(data.engines);
            if (data.backend_status && data.backend_status !== 'RUNNING') {
                if (!audioLabRetryTimer) {
                    console.log(`[audiolab] Backend status: ${data.backend_status}, will retry in 3s`);
                    audioLabRetryTimer = setTimeout(() => {
                        audioLabRetryTimer = null;
                        audioLabRefreshEngineManager();
                    }, 3000);
                }
            }
        } else {
            console.error('[audiolab] Failed to load engines:', data);
        }
    });
}

/** Renders the engine manager section into the given container element. */
function audioLabRenderEngineManager(container, engines) {
    container.innerHTML = '';
    let header = createDiv(null, 'audiolab-engine-section-header');
    header.innerHTML = '<b>Available Engines</b>';
    container.appendChild(header);

    for (const cat of ENGINE_CATEGORIES) {
        const catEngines = engines.filter(e => e.category === cat.key);
        if (catEngines.length === 0) continue;

        const installedCount = catEngines.filter(e => e.installed).length;
        const countLabel = installedCount > 0 ? ` (${installedCount}/${catEngines.length} installed)` : ` (${catEngines.length})`;
        const catGroup = createDiv(null, 'audiolab-cat-group');
        const catHeader = createDiv(null, 'audiolab-cat-header');
        const arrow = document.createElement('span');
        arrow.className = 'audiolab-cat-arrow';
        arrow.innerHTML = '&#x2B9F;';
        catHeader.appendChild(arrow);
        const label = document.createElement('span');
        label.innerText = cat.label;
        catHeader.appendChild(label);
        const count = document.createElement('span');
        count.style.cssText = 'color:var(--text-soft);font-weight:normal;font-size:0.85em';
        count.innerText = countLabel;
        catHeader.appendChild(count);
        catHeader.addEventListener('click', () => {
            catGroup.classList.toggle('collapsed');
        });
        // Start collapsed by default
        catGroup.classList.add('collapsed');
        catGroup.appendChild(catHeader);

        // Card grid body
        const catBody = createDiv(null, 'audiolab-cat-body');
        for (const engine of catEngines) {
            catBody.appendChild(audioLabBuildEngineCard(engine));
        }
        catGroup.appendChild(catBody);
        container.appendChild(catGroup);
    }
}

/** Builds a single engine card element. */
function audioLabBuildEngineCard(engine) {
    const card = createDiv(null, 'audiolab-engine-card');
    if (!engine.platform_compatible) card.classList.add('incompatible');

    const cardHeader = createDiv(null, 'audiolab-engine-card-header');
    const status = createDiv(null, 'audiolab-engine-status-dot');
    if (engine.installed) {
        status.style.backgroundColor = 'var(--backend-running)';
        status.title = 'Installed';
    } else if (!engine.platform_compatible) {
        status.style.backgroundColor = 'var(--backend-disabled)';
        status.title = engine.platform_note || 'Not compatible';
    }
    cardHeader.appendChild(status);
    const nameSpan = document.createElement('span');
    nameSpan.className = 'audiolab-engine-name';
    nameSpan.innerText = engine.name;
    cardHeader.appendChild(nameSpan);
    if (engine.is_api_provider) {
        const apiBadge = document.createElement('span');
        apiBadge.className = 'audiolab-api-badge';
        apiBadge.innerText = 'API';
        apiBadge.title = 'Cloud API — requires an API key, no local GPU needed';
        cardHeader.appendChild(apiBadge);
    }
    card.appendChild(cardHeader);

    const firstModel = engine.models && engine.models.length > 0 ? engine.models[0] : null;
    if (firstModel) {
        const meta = createDiv(null, 'audiolab-engine-meta');
        const parts = [];
        if (firstModel.estimated_vram) parts.push(firstModel.estimated_vram + ' VRAM');
        if (firstModel.license) parts.push(firstModel.license);
        if (firstModel.estimated_size) parts.push(firstModel.estimated_size);
        meta.innerText = parts.join(' | ');
        card.appendChild(meta);
    }

    if (firstModel && firstModel.description) {
        const desc = createDiv(null, 'audiolab-engine-desc');
        desc.innerText = firstModel.description;
        card.appendChild(desc);
    }

    const footer = createDiv(null, 'audiolab-engine-card-footer');
    if (!engine.platform_compatible) {
        const note = document.createElement('span');
        note.style.cssText = 'color:var(--text-soft);font-size:0.8em';
        note.innerText = 'Requires Docker';
        footer.appendChild(note);
    } else if (engine.installed) {

        // Checkpoint engines can hold several variants — let the user add/remove individual models.
        const perModel = !engine.self_managed && !engine.is_api_provider && (engine.models || []).length > 1;
        if (perModel) {
            const manageBtn = document.createElement('button');
            manageBtn.className = 'basic-button btn-primary';
            manageBtn.innerText = 'Manage';
            manageBtn.title = 'Install or remove individual models';
            manageBtn.addEventListener('click', (e) => { e.stopPropagation(); audioLabShowInstallModal(engine); });
            footer.appendChild(manageBtn);
        }
        const btn = document.createElement('button');
        btn.className = 'basic-button';
        btn.innerText = 'Remove';
        btn.title = 'Remove the whole engine and all its models';
        btn.addEventListener('click', (e) => { e.stopPropagation(); audioLabConfirmUninstall(engine); });
        footer.appendChild(btn);
    } else {
        const btn = document.createElement('button');
        btn.className = 'basic-button btn-primary';
        btn.innerText = 'Install';
        btn.addEventListener('click', (e) => { e.stopPropagation(); audioLabShowInstallModal(engine); });
        footer.appendChild(btn);
    }
    card.appendChild(footer);

    return card;
}

/** Shows the install/manage modal for an engine. Checkpoint engines (non-API, non-self-managed) get a
 * per-model table with an Install/Remove button per variant plus Download All; API/self-managed engines
 * keep the simpler whole-engine confirm flow (their weights fetch on first use). */
function audioLabShowInstallModal(engine) {
    const existingModal = document.getElementById('audiolab_install_modal');
    if (existingModal) existingModal.remove();

    const models = engine.models || [];
    const firstModel = models.length > 0 ? models[0] : {};
    // Per-model install only applies where the extension actually downloads discrete checkpoints.
    const perModel = !engine.self_managed && !engine.is_api_provider && models.length > 0;
    const runtimeNoteHtml = engine.self_managed
        ? `<p style="color:var(--text-soft);margin-top:0.5em">⚙ Runs on the HartsyInference C# engine — these models download automatically on first use, no install needed.</p>`
        : (engine.in_process === false ? '' : `<p style="color:var(--text-soft);margin-top:0.5em">⚙ Runs on the HartsyInference C# engine — each model you install downloads its weights to disk.</p>`);

    let modelsListHtml = '';
    if (models.length > 0) {
        const modelRows = models.map(m => {
            const sourceLink = m.source_url
                ? `<a href="${escapeHtml(m.source_url)}" target="_blank" rel="noopener" style="font-size:0.85em">${escapeHtml(m.name)}</a>`
                : escapeHtml(m.name);
            const details = [];
            if (m.estimated_size) details.push(m.estimated_size);
            if (m.estimated_vram) details.push(m.estimated_vram + ' VRAM');
            if (m.license) details.push(m.license);
            const actionCells = perModel
                ? `<td class="audiolab-model-status" data-model-status></td>
                   <td class="audiolab-model-action" style="text-align:right" data-model-action></td>`
                : '';
            return `<tr data-model-id="${escapeHtml(m.id)}">
                <td style="padding:3px 8px 3px 0">${sourceLink}</td>
                <td style="padding:3px 8px 3px 0;color:var(--text-soft);font-size:0.85em">${escapeHtml(details.join(' | '))}</td>
                ${actionCells}
            </tr>`;
        }).join('');
        modelsListHtml = `<table style="width:100%;margin:0.3em 0" id="audiolab_install_models">${modelRows}</table>`;
    }

    const heading = perModel
        ? `<b>Models (${models.length})</b> — install only what you need:`
        : `<b>Models to download (${models.length}):</b>`;
    const bodyHtml = `
        <div class="modal-body">
            <p><b>${escapeHtml(engine.name)}</b></p>
            <p>${escapeHtml(firstModel.description || '')}</p>
            <p style="color:var(--text-soft);margin-top:0.5em">${heading}</p>
            ${modelsListHtml}
            ${runtimeNoteHtml}
            <div id="audiolab_install_progress" style="display:none;margin-top:1em">
                <p style="color:var(--text-soft)"><b>Progress:</b></p>
                <div id="audiolab_install_progress_text" style="font-family:monospace;font-size:0.85em;max-height:150px;overflow-y:auto;padding:0.5em;border:1px solid var(--border-color);border-radius:4px"></div>
            </div>
        </div>`;

    const footerHtml = perModel
        ? `<div class="modal-footer">
            <button class="btn btn-primary basic-button" id="audiolab_install_downloadall_btn">Download All</button>
            <button class="btn btn-secondary basic-button" id="audiolab_install_cancel_btn">Close</button>
        </div>`
        : `<div class="modal-footer">
            <button class="btn btn-primary basic-button" id="audiolab_install_confirm_btn">Install</button>
            <button class="btn btn-secondary basic-button" id="audiolab_install_cancel_btn">Cancel</button>
        </div>`;

    const title = perModel && engine.installed ? `Manage ${escapeHtml(engine.name)}` : `Install ${escapeHtml(engine.name)}`;
    const html = modalHeader('audiolab_install_modal', title) + bodyHtml + footerHtml + modalFooter();

    const wrapper = document.createElement('div');
    wrapper.innerHTML = html;
    document.body.appendChild(wrapper.firstElementChild);

    const modal = document.getElementById('audiolab_install_modal');
    const cancelBtn = document.getElementById('audiolab_install_cancel_btn');
    cancelBtn.addEventListener('click', () => {
        $(modal).modal('hide');
        setTimeout(() => modal.remove(), 300);
    });

    if (perModel) {
        for (const m of models) {
            const row = modal.querySelector(`tr[data-model-id="${cssEscape(m.id)}"]`);
            if (row) audioLabRenderModelRow(engine, m, row);
        }
        // The primary footer button flips between "Download All" and "Remove All" based on how many models
        // are installed — so once everything's installed you get a Remove All instead of a dead Download All.
        audioLabUpdateManageFooter(engine);
    }
    else {
        const confirmBtn = document.getElementById('audiolab_install_confirm_btn');
        confirmBtn.addEventListener('click', () => audioLabDoInstall(engine, modal));
    }

    $(modal).modal('show');
}

/** CSS.escape shim (some older embedded browsers lack it) for building attribute selectors from model ids. */
function cssEscape(value) {
    if (window.CSS && CSS.escape) return CSS.escape(value);
    return String(value).replace(/[^a-zA-Z0-9_-]/g, '\\$&');
}

/** Renders the status + action cells of one per-model row to reflect its current installed state. */
function audioLabRenderModelRow(engine, model, row) {
    const statusCell = row.querySelector('[data-model-status]');
    const actionCell = row.querySelector('[data-model-action]');
    if (!statusCell || !actionCell) return;
    statusCell.innerHTML = '';
    actionCell.innerHTML = '';
    if (model.installed) {
        const badge = document.createElement('span');
        badge.className = 'audiolab-model-installed';
        badge.innerText = '✓ Installed';
        statusCell.appendChild(badge);
        const removeBtn = document.createElement('button');
        removeBtn.className = 'basic-button audiolab-model-btn';
        removeBtn.innerText = 'Remove';
        removeBtn.title = 'Delete this model\'s weights from disk';
        removeBtn.addEventListener('click', () => audioLabRemoveModel(engine, model, row));
        actionCell.appendChild(removeBtn);
    }
    else {
        const installBtn = document.createElement('button');
        installBtn.className = 'basic-button btn-primary audiolab-model-btn';
        installBtn.innerText = 'Install';
        // Refresh the footer after a single install so it flips to Remove All once the last model lands.
        installBtn.addEventListener('click', async () => { await audioLabInstallModel(engine, model, row); audioLabUpdateManageFooter(engine); });
        actionCell.appendChild(installBtn);
    }
}

/** Shows the shared progress area and returns its text element. */
function audioLabShowProgress(initialLine) {
    const area = document.getElementById('audiolab_install_progress');
    const text = document.getElementById('audiolab_install_progress_text');
    if (area) area.style.display = 'block';
    if (text && initialLine) { text.innerText += initialLine + '\n'; text.scrollTop = text.scrollHeight; }
    return text;
}

/** Installs one model's weights via WebSocket with streaming progress. Resolves true on success,
 * false on error (so Download All can sequence and keep going). */
function audioLabInstallModel(engine, model, row) {
    const actionCell = row.querySelector('[data-model-action]');
    const statusCell = row.querySelector('[data-model-status]');
    if (actionCell) actionCell.innerHTML = '';
    if (statusCell) statusCell.innerHTML = '<span class="audiolab-model-installing">Installing…</span>';
    const progressText = audioLabShowProgress(`Installing ${model.name}…`);

    return new Promise(resolve => {
        makeWSRequest('AudioLabInstallEngine', { provider_id: engine.id, model_id: model.id }, data => {
            if (data.info) {
                if (progressText) { progressText.innerText += data.info + '\n'; progressText.scrollTop = progressText.scrollHeight; }
            }
            else if (data.success) {
                if (progressText) { progressText.innerText += `${model.name} installed.\n`; progressText.scrollTop = progressText.scrollHeight; }
                model.installed = true;
                audioLabRenderModelRow(engine, model, row);
                audioLabRefreshEngineManager();
                resolve(true);
            }
            else if (data.error) {
                if (progressText) { progressText.innerText += `Error: ${data.error}\n`; progressText.scrollTop = progressText.scrollHeight; }
                audioLabRenderModelRow(engine, model, row);
                showError(`Failed to install ${model.name}: ${data.error}`);
                resolve(false);
            }
        }, 0, e => {
            if (progressText) { progressText.innerText += `Connection error: ${e}\n`; }
            audioLabRenderModelRow(engine, model, row);
            showError(`Failed to install ${model.name}: ${e}`);
            resolve(false);
        });
    });
}

/** Deletes one model's weights from disk (no confirm), updating its row. Resolves true on success so
 * Remove All can sequence and keep going. */
function audioLabDoRemoveModel(engine, model, row) {
    return new Promise(resolve => {
        genericRequest('AudioLabUninstallEngine', { provider_id: engine.id, model_id: model.id, delete_weights: true }, data => {
            if (data.success) {
                model.installed = false;
                if (row) audioLabRenderModelRow(engine, model, row);
                resolve(true);
            } else {
                showError(`Failed to remove ${model.name}: ${data.error || 'Unknown error'}`);
                resolve(false);
            }
        }, 0, e => { showError(`Failed to remove ${model.name}: ${e}`); resolve(false); });
    });
}

/** Deletes one model's weights from disk, leaving the engine installed. */
async function audioLabRemoveModel(engine, model, row) {
    if (!confirm(`Delete ${model.name}'s weights from disk?\n\nThe model stays listed and re-downloads if you use it again.`)) {
        return;
    }
    if (await audioLabDoRemoveModel(engine, model, row)) {
        doNoticePopover(`${model.name} weights removed.`, 'notice-pop-green');
        audioLabUpdateManageFooter(engine);
        audioLabRefreshEngineManager();
    }
}

/** Installs every not-yet-installed model via the server-side bulk endpoint (AudioLabInstallAllModels),
 * streaming progress into the shared box and flipping each model's row as it completes. */
function audioLabDownloadAll(engine, modal, button) {
    const pending = (engine.models || []).filter(m => !m.installed);
    if (pending.length === 0) {
        doNoticePopover(`All ${engine.name} models are already installed.`, 'notice-pop-green');
        return;
    }
    // Mark every pending row as installing up front (the server installs them in sequence).
    for (const m of pending) {
        const row = modal.querySelector(`tr[data-model-id="${cssEscape(m.id)}"]`);
        const statusCell = row && row.querySelector('[data-model-status]');
        const actionCell = row && row.querySelector('[data-model-action]');
        if (actionCell) actionCell.innerHTML = '';
        if (statusCell) statusCell.innerHTML = '<span class="audiolab-model-installing">Installing…</span>';
    }
    button.disabled = true;
    button.innerText = 'Downloading…';
    const progressText = audioLabShowProgress(`Installing all ${engine.name} models…`);
    const flipRow = (modelId, installed) => {
        const model = (engine.models || []).find(m => m.id === modelId);
        const row = modal.querySelector(`tr[data-model-id="${cssEscape(modelId)}"]`);
        if (model) model.installed = installed;
        if (model && row) audioLabRenderModelRow(engine, model, row);
    };
    makeWSRequest('AudioLabInstallAllModels', { provider_id: engine.id }, data => {
        if (data.info) {
            if (progressText) { progressText.innerText += data.info + '\n'; progressText.scrollTop = progressText.scrollHeight; }
            if (data.model_done) flipRow(data.model_id, true);
            else if (data.model_failed) flipRow(data.model_id, false);
        }
        else if (data.success) {
            button.disabled = false;
            button.innerText = 'Download All';
            doNoticePopover(data.message || `Installed ${engine.name} models.`, data.installed === data.total ? 'notice-pop-green' : 'notice-pop-red');
            audioLabUpdateManageFooter(engine);
            audioLabRefreshEngineManager();
        }
        else if (data.error) {
            button.disabled = false;
            button.innerText = 'Download All';
            if (progressText) { progressText.innerText += `Error: ${data.error}\n`; }
            showError(data.error);
            // Re-render rows to whatever their real state is now.
            for (const m of pending) { const row = modal.querySelector(`tr[data-model-id="${cssEscape(m.id)}"]`); if (row) audioLabRenderModelRow(engine, m, row); }
            audioLabUpdateManageFooter(engine);
        }
    }, 0, e => {
        button.disabled = false;
        button.innerText = 'Download All';
        showError(`Install-all failed: ${e}`);
        for (const m of pending) { const row = modal.querySelector(`tr[data-model-id="${cssEscape(m.id)}"]`); if (row) audioLabRenderModelRow(engine, m, row); }
        audioLabUpdateManageFooter(engine);
    });
}

/** Removes every installed model via the server-side bulk endpoint (AudioLabRemoveAllModels). One bulk
 * confirm, then flips each removed model's row from the authoritative removed_ids list. */
function audioLabRemoveAll(engine, modal, button) {
    const models = (engine.models || []).filter(m => m.installed);
    if (models.length === 0) {
        doNoticePopover(`No ${engine.name} models are installed.`, 'notice-pop-green');
        return;
    }
    if (!confirm(`Remove all ${models.length} installed ${engine.name} model(s)?\n\nTheir weights are deleted from disk; they re-download if used again.`)) {
        return;
    }
    button.disabled = true;
    button.innerText = 'Removing…';
    genericRequest('AudioLabRemoveAllModels', { provider_id: engine.id }, data => {
        button.disabled = false;
        if (data.success) {
            for (const id of (data.removed_ids || [])) {
                const model = (engine.models || []).find(m => m.id === id);
                const row = modal.querySelector(`tr[data-model-id="${cssEscape(id)}"]`);
                if (model) model.installed = false;
                if (model && row) audioLabRenderModelRow(engine, model, row);
            }
            doNoticePopover(data.message || `Removed ${engine.name} models.`, data.removed === data.total ? 'notice-pop-green' : 'notice-pop-red');
        } else {
            showError(data.error || 'Remove all failed');
        }
        audioLabUpdateManageFooter(engine);
        audioLabRefreshEngineManager();
    }, 0, e => {
        button.disabled = false;
        showError(`Remove all failed: ${e}`);
        audioLabUpdateManageFooter(engine);
    });
}

/** Flips the primary footer button between Download All (something's missing) and Remove All (everything
 * installed), re-binding its click handler. Cloning the node strips the previous listener so handlers can't
 * stack. No-op outside the per-model manage modal (button absent). */
function audioLabUpdateManageFooter(engine) {
    const oldBtn = document.getElementById('audiolab_install_downloadall_btn');
    if (!oldBtn) return;
    const modal = document.getElementById('audiolab_install_modal');
    const models = engine.models || [];
    const allInstalled = models.length > 0 && models.every(m => m.installed);
    // Shallow clone (drops children + listeners); keep id so future lookups still find it.
    const btn = oldBtn.cloneNode(false);
    btn.id = 'audiolab_install_downloadall_btn';
    oldBtn.parentNode.replaceChild(btn, oldBtn);
    if (allInstalled) {
        btn.className = 'btn btn-danger basic-button';
        btn.innerText = 'Remove All';
        btn.title = 'Delete every installed model\'s weights from disk';
        btn.addEventListener('click', () => audioLabRemoveAll(engine, modal, btn));
    } else {
        btn.className = 'btn btn-primary basic-button';
        btn.innerText = 'Download All';
        btn.title = '';
        btn.addEventListener('click', () => audioLabDownloadAll(engine, modal, btn));
    }
}

/** Executes the whole-engine install flow via WebSocket with streaming progress (API/self-managed engines). */
function audioLabDoInstall(engine, modal) {
    const confirmBtn = document.getElementById('audiolab_install_confirm_btn');
    const cancelBtn = document.getElementById('audiolab_install_cancel_btn');
    const progressArea = document.getElementById('audiolab_install_progress');
    const progressText = document.getElementById('audiolab_install_progress_text');

    confirmBtn.disabled = true;
    confirmBtn.innerText = 'Installing...';
    cancelBtn.disabled = true;
    progressArea.style.display = 'block';
    progressText.innerText = 'Starting installation...\n';

    makeWSRequest('AudioLabInstallEngine', { provider_id: engine.id }, data => {
        if (data.info) {
            progressText.innerText += data.info + '\n';
            progressText.scrollTop = progressText.scrollHeight;
        }
        else if (data.success) {
            progressText.innerText += 'Installation complete!\n';
            setTimeout(() => {
                $(modal).modal('hide');
                setTimeout(() => modal.remove(), 300);
                doNoticePopover(`${engine.name} installed!`, 'notice-pop-green');
                audioLabRefreshEngineManager();
            }, 1000);
        }
        else if (data.error) {
            progressText.innerText += `Error: ${data.error}\n`;
            confirmBtn.disabled = false;
            confirmBtn.innerText = 'Retry';
            cancelBtn.disabled = false;
            showError(`Failed to install ${engine.name}: ${data.error}`);
        }
    }, 0, e => {
        progressText.innerText += `Connection error: ${e}\n`;
        confirmBtn.disabled = false;
        confirmBtn.innerText = 'Retry';
        cancelBtn.disabled = false;
        showError(`Failed to install ${engine.name}: ${e}`);
    });
}

/** Confirms and uninstalls an engine, optionally deleting its downloaded weights from disk. */
function audioLabConfirmUninstall(engine) {
    if (!confirm(`Remove ${engine.name}? Its models will be unregistered from the model browser.`)) {
        return;
    }
    // Second prompt: keep or delete the downloaded files. Engine-private weights are removed; shared
    // side-model caches (used by other installed engines) are retained automatically by the backend.
    const deleteWeights = confirm(
        `Also DELETE ${engine.name}'s downloaded weights from disk to free space?\n\n`
        + `OK = delete the files (you'll re-download to use it again).\n`
        + `Cancel = keep the files on disk.`);
    genericRequest('AudioLabUninstallEngine', { provider_id: engine.id, delete_weights: deleteWeights }, data => {
        if (data.success) {
            doNoticePopover(`${engine.name} removed${data.deleted_weights ? ' (weights deleted)' : ''}.`, 'notice-pop-green');
            audioLabRefreshEngineManager();
        } else {
            showError(`Failed to remove ${engine.name}: ${data.error || 'Unknown error'}`);
        }
    });
}

/** Repairs an engine whose weights are missing: clears the stale registration + any partial files, then
 * reopens the install flow (which re-downloads). */
/** Refreshes the engine manager UI by reloading data from the API. */
function audioLabRefreshEngineManager() {
    audioLabLoadEngines(engines => {
        const container = document.getElementById('audiolab_engine_manager');
        if (container) {
            audioLabRenderEngineManager(container, engines);
        }
    });
}

registerMediaButton('Audio Lab', (src) => AudioLab.open(src), 'Open Audio Lab for editing, voice cloning setup, and export', ['audio'], true);

/** Injects engine manager UI into Audio Backend cards via backendsRevisedCallbacks. */
backendsRevisedCallbacks.push(() => {
    for (const [id, backend] of Object.entries(backends_loaded)) {
        if (backend.type !== 'audio-backend') continue;

        const card = document.getElementById(`backend-card-${id}`);
        if (!card) continue;

        const cardBody = card.querySelector('.card-body');
        if (!cardBody) continue;

        if (document.getElementById('audiolab_engine_manager')) continue;

        const separator = document.createElement('hr');
        separator.style.borderColor = 'var(--border-color)';
        separator.style.margin = '1em 0';
        cardBody.appendChild(separator);

        const container = createDiv('audiolab_engine_manager', 'audiolab-engine-manager');
        container.innerHTML = '<em style="color:var(--text-soft)">Loading engines...</em>';
        cardBody.appendChild(container);

        audioLabLoadEngines(engines => {
            audioLabRenderEngineManager(container, engines);
        });
    }
});
