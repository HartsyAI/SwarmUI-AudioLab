/**
 * AudioLab Feature Handler for SwarmUI
 * Integrates audio processing models with SwarmUI's Generate tab feature system.
 * Mirrors the pattern from SwarmUI-API-Backends/Assets/api-backends.js.
 */

const AudioLabConfig = {
    /** Maps model class IDs (curArch) to their category flag and provider-specific flag. */
    archToCategory: {
        kokoro_tts: { category: 'audiolab_tts', providerFlag: 'kokoro_tts_params' },
        chatterbox_tts: { category: 'audiolab_tts', providerFlag: 'chatterbox_tts_params' },
        bark_tts: { category: 'audiolab_tts', providerFlag: 'bark_tts_params' },
        piper_tts: { category: 'audiolab_tts', providerFlag: 'piper_tts_params' },
        dia_tts: { category: 'audiolab_tts', providerFlag: 'dia_tts_params' },
        csm_tts: { category: 'audiolab_tts', providerFlag: 'csm_tts_params' },
        orpheus_tts: { category: 'audiolab_tts', providerFlag: 'orpheus_tts_params' },
        vibevoice_tts: { category: 'audiolab_tts', providerFlag: 'vibevoice_tts_params' },
        zonos_tts: { category: 'audiolab_tts', providerFlag: 'zonos_tts_params' },
        f5_tts: { category: 'audiolab_tts', providerFlag: 'f5_tts_params' },
        neutts_tts: { category: 'audiolab_tts', providerFlag: 'neutts_tts_params' },
        cosyvoice_tts: { category: 'audiolab_tts', providerFlag: 'cosyvoice_tts_params' },
        fishspeech_tts: { category: 'audiolab_tts', providerFlag: 'fishspeech_tts_params' },
        whisper_stt: { category: 'audiolab_stt', providerFlag: 'whisper_stt_params' },
        distilwhisper_stt: { category: 'audiolab_stt', providerFlag: 'distilwhisper_stt_params' },
        moonshine_stt: { category: 'audiolab_stt', providerFlag: 'moonshine_stt_params' },
        realtimestt_stt: { category: 'audiolab_stt', providerFlag: 'realtimestt_params' },
        musicgen_music: { category: 'audiolab_music', providerFlag: 'musicgen_music_params' },
        acestep_music: { category: 'audiolab_music', providerFlag: 'acestep_music_params' },
        openvoice_clone: { category: 'audiolab_clone', providerFlag: 'openvoice_clone_params' },
        rvc_clone: { category: 'audiolab_clone', providerFlag: 'rvc_clone_params' },
        gptsovits_clone: { category: 'audiolab_clone', providerFlag: 'gptsovits_clone_params' },
        demucs_fx: { category: 'audiolab_fx', providerFlag: 'demucs_fx_params' },
        resemble_enhance_fx: { category: 'audiolab_fx', providerFlag: 'resemble_enhance_fx_params' },
        audiogen_sfx: { category: 'audiolab_sfx', providerFlag: 'audiogen_sfx_params' }
    },

    categoryFlags: ['audiolab_tts', 'audiolab_stt', 'audiolab_music', 'audiolab_clone', 'audiolab_fx', 'audiolab_sfx'],

    /** Core image params to hide when an audio model is selected. */
    coreParamsToHide: [
        'steps', 'cfgscale', 'width', 'height', 'sidelength', 'aspectratio',
        'seed', 'batchsize', 'initimage', 'initimagecreativity', 'initimageresettonorm',
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

    /** Get all audio-related flags (categories + all provider flags). */
    get allAudioFlags() {
        const providerFlags = Object.values(this.archToCategory).map(v => v.providerFlag);
        return [...this.categoryFlags, ...providerFlags];
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
        return [[], AudioLabConfig.allAudioFlags];
    }

    const config = AudioLabConfig.archToCategory[curArch];
    const activeCategory = config.category;
    const activeProviderFlag = config.providerFlag;

    const otherAudioFlags = AudioLabConfig.allAudioFlags.filter(f => f !== activeCategory && f !== activeProviderFlag);
    const removeFlags = [...AudioLabConfig.incompatibleFlags, ...otherAudioFlags];
    const addFlags = ['prompt', activeCategory, activeProviderFlag];

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
    { key: 'MusicGen', label: 'Music Generation' },
    { key: 'VoiceClone', label: 'Voice Cloning' },
    { key: 'AudioFX', label: 'Audio Effects' },
    { key: 'SoundFX', label: 'Sound Effects' }
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
        const btn = document.createElement('button');
        btn.className = 'basic-button';
        btn.innerText = 'Remove';
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

/** Shows the install confirmation modal for an engine. */
function audioLabShowInstallModal(engine) {
    const existingModal = document.getElementById('audiolab_install_modal');
    if (existingModal) existingModal.remove();

    const models = engine.models || [];
    const firstModel = models.length > 0 ? models[0] : {};
    const depNames = engine.dependencies ? engine.dependencies.map(d => d.name) : [];
    const depListHtml = depNames.length > 0
        ? `<ul style="margin:0.5em 0;padding-left:1.5em">${depNames.map(d => `<li style="font-size:0.9em">${escapeHtml(d)}</li>`).join('')}</ul>`
        : '<em>None</em>';

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
            return `<tr>
                <td style="padding:3px 8px 3px 0">${sourceLink}</td>
                <td style="padding:3px 0;color:var(--text-soft);font-size:0.85em">${escapeHtml(details.join(' | '))}</td>
            </tr>`;
        }).join('');
        modelsListHtml = `<table style="width:100%;margin:0.3em 0">${modelRows}</table>`;
    }

    const bodyHtml = `
        <div class="modal-body">
            <p><b>${escapeHtml(engine.name)}</b></p>
            <p>${escapeHtml(firstModel.description || '')}</p>
            <p style="color:var(--text-soft);margin-top:0.5em"><b>Models to download (${models.length}):</b></p>
            ${modelsListHtml}
            <p style="color:var(--text-soft);margin-top:0.5em"><b>Dependencies:</b></p>
            ${depListHtml}
            <div id="audiolab_install_progress" style="display:none;margin-top:1em">
                <p style="color:var(--text-soft)"><b>Install Progress:</b></p>
                <div id="audiolab_install_progress_text" style="font-family:monospace;font-size:0.85em;max-height:150px;overflow-y:auto;padding:0.5em;border:1px solid var(--border-color);border-radius:4px"></div>
            </div>
        </div>`;

    const footerHtml = `
        <div class="modal-footer">
            <button class="btn btn-primary basic-button" id="audiolab_install_confirm_btn">Install</button>
            <button class="btn btn-secondary basic-button" id="audiolab_install_cancel_btn">Cancel</button>
        </div>`;

    const html = modalHeader('audiolab_install_modal', `Install ${escapeHtml(engine.name)}`)
        + bodyHtml + footerHtml + modalFooter();

    const wrapper = document.createElement('div');
    wrapper.innerHTML = html;
    document.body.appendChild(wrapper.firstElementChild);

    const modal = document.getElementById('audiolab_install_modal');
    const confirmBtn = document.getElementById('audiolab_install_confirm_btn');
    const cancelBtn = document.getElementById('audiolab_install_cancel_btn');

    confirmBtn.addEventListener('click', () => audioLabDoInstall(engine, modal));
    cancelBtn.addEventListener('click', () => {
        $(modal).modal('hide');
        setTimeout(() => modal.remove(), 300);
    });

    $(modal).modal('show');
}

/** Executes the install flow for an engine via WebSocket with streaming progress. */
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

/** Confirms and uninstalls an engine. */
function audioLabConfirmUninstall(engine) {
    if (!confirm(`Remove ${engine.name}? Its models will be unregistered from the model browser.`)) {
        return;
    }
    genericRequest('AudioLabUninstallEngine', { provider_id: engine.id }, data => {
        if (data.success) {
            doNoticePopover(`${engine.name} removed.`, 'notice-pop-green');
            audioLabRefreshEngineManager();
        } else {
            showError(`Failed to remove ${engine.name}: ${data.error || 'Unknown error'}`);
        }
    });
}

/** Refreshes the engine manager UI by reloading data from the API. */
function audioLabRefreshEngineManager() {
    audioLabLoadEngines(engines => {
        const container = document.getElementById('audiolab_engine_manager');
        if (container) {
            audioLabRenderEngineManager(container, engines);
        }
    });
}

// TODO: PR to SwarmUI to add a buttonsForImageCallbacks array in outputhistory.js
// so extensions can register buttons without wrapping.
setTimeout(() => {
    if (typeof buttonsForImage !== 'function') {
        console.warn('[audiolab] buttonsForImage not found, Audio Lab button not registered');
        return;
    }
    const origButtonsForImage = buttonsForImage;
    buttonsForImage = function(fullsrc, src, metadata) {
        const buttons = origButtonsForImage(fullsrc, src, metadata);
        if (isAudioExt(src)) {
            buttons.push({
                label: 'Audio Lab',
                title: 'Open Audio Lab for editing, voice cloning setup, and export',
                onclick: () => AudioLab.open(src)
            });
        }
        return buttons;
    };
}, 100);

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
