/**
 * AudioLab Feature Handler for SwarmUI
 * Integrates audio processing models with SwarmUI's Generate tab feature system.
 * Mirrors the pattern from SwarmUI-API-Backends/Assets/api-backends.js.
 */

const AudioLabConfig = {
    // Maps model class IDs (curArch) to their category flag and provider-specific flag
    // Category flags control which param groups appear; provider flags control provider-specific params
    archToCategory: {
        // TTS providers
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
        // STT providers
        whisper_stt: { category: 'audiolab_stt', providerFlag: 'whisper_stt_params' },
        distilwhisper_stt: { category: 'audiolab_stt', providerFlag: 'distilwhisper_stt_params' },
        moonshine_stt: { category: 'audiolab_stt', providerFlag: 'moonshine_stt_params' },
        realtimestt_stt: { category: 'audiolab_stt', providerFlag: 'realtimestt_params' },
        // Music providers
        musicgen_music: { category: 'audiolab_music', providerFlag: 'musicgen_music_params' },
        acestep_music: { category: 'audiolab_music', providerFlag: 'acestep_music_params' },
        // Voice Clone providers
        openvoice_clone: { category: 'audiolab_clone', providerFlag: 'openvoice_clone_params' },
        rvc_clone: { category: 'audiolab_clone', providerFlag: 'rvc_clone_params' },
        gptsovits_clone: { category: 'audiolab_clone', providerFlag: 'gptsovits_clone_params' },
        // Audio FX providers
        demucs_fx: { category: 'audiolab_fx', providerFlag: 'demucs_fx_params' },
        resemble_enhance_fx: { category: 'audiolab_fx', providerFlag: 'resemble_enhance_fx_params' },
        // Sound FX providers
        audiogen_sfx: { category: 'audiolab_sfx', providerFlag: 'audiogen_sfx_params' }
    },

    // All category-level flags
    categoryFlags: ['audiolab_tts', 'audiolab_stt', 'audiolab_music', 'audiolab_clone', 'audiolab_fx', 'audiolab_sfx'],

    // All core image params to hide when an audio model is selected
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

    // Core parameter groups that are image-only and should be hidden for all audio models
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

    // Image-only feature flags incompatible with audio models
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

// Feature set changer - controls parameter visibility for audio models
featureSetChangers.push(() => {
    if (!gen_param_types) {
        return [[], []];
    }

    const curArch = currentModelHelper.curArch;
    const isAudioModel = AudioLabConfig.isAudioModel(curArch);

    // Handle core param visibility for audio models
    for (let param of gen_param_types) {
        // Hide individual core params that don't apply to audio models
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
        // Hide params belonging to image-only groups
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

    // Not using an audio model - remove all audio-specific flags
    if (!isAudioModel) {
        return [[], AudioLabConfig.allAudioFlags];
    }

    // Audio model selected - determine which flags to add/remove
    const config = AudioLabConfig.archToCategory[curArch];
    const activeCategory = config.category;
    const activeProviderFlag = config.providerFlag;

    // Remove: incompatible image flags + other audio category/provider flags
    const otherAudioFlags = AudioLabConfig.allAudioFlags.filter(f => f !== activeCategory && f !== activeProviderFlag);
    const removeFlags = [
        ...AudioLabConfig.incompatibleFlags,
        ...otherAudioFlags
    ];

    // Add: prompt (for text input), active category flag, active provider flag
    const addFlags = ['prompt', activeCategory, activeProviderFlag];

    return [addFlags, removeFlags];
});

// ============================================================
// AudioStreamPlayer — auto-play queue for streaming TTS chunks
// ============================================================
const AudioStreamPlayer = {
    /** @type {string[]} Queue of audio data URLs waiting to play */
    queue: [],
    /** @type {HTMLAudioElement|null} Currently playing audio element */
    current: null,
    /** @type {boolean} Whether streaming playback is active */
    active: false,

    /** Reset state and prepare for a new streaming session. */
    start() {
        this.stop();
        this.active = true;
        console.log('[audiolab] Stream playback started');
    },

    /** Add a chunk data URL to the play queue; begin playback if idle. */
    enqueue(dataUrl) {
        if (!this.active) return;
        this.queue.push(dataUrl);
        if (!this.current) {
            this.playNext();
        }
    },

    /** Play the next chunk from the queue. Chains via the 'ended' event. */
    playNext() {
        if (!this.active || this.queue.length === 0) {
            this.current = null;
            return;
        }
        let src = this.queue.shift();
        let audio = new Audio(src);
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

    /** Stop playback, clear queue, reset state. */
    stop() {
        this.active = false;
        if (this.current) {
            this.current.pause();
            this.current = null;
        }
        this.queue = [];
    }
};

// ============================================================
// Hook into doGenerate + internalHandleData for streaming
// ============================================================
setTimeout(() => {
    // Find the main GenerateHandler instance (genHandler or mainGenHandler)
    let handler = typeof mainGenHandler !== 'undefined' ? mainGenHandler
                : typeof genHandler !== 'undefined' ? genHandler : null;
    if (!handler) {
        console.warn('[audiolab] No generate handler found, streaming hooks not installed');
        return;
    }

    // Wrap doGenerate to start AudioStreamPlayer when streaming TTS is active
    let origDoGenerate = handler.doGenerate.bind(handler);
    handler.doGenerate = function(input_overrides = {}, input_preoverrides = {}, postCollectRun = null) {
        let curArch = currentModelHelper.curArch;
        if (AudioLabConfig.isAudioModel(curArch)) {
            let config = AudioLabConfig.archToCategory[curArch];
            if (config.category === 'audiolab_tts') {
                // Check if Stream Audio checkbox is checked
                let streamParam = document.getElementById('input_streamaudio');
                if (streamParam && (streamParam.checked || streamParam.value === 'true')) {
                    AudioStreamPlayer.start();
                }
            }
        }
        return origDoGenerate(input_overrides, input_preoverrides, postCollectRun);
    };

    // Wrap internalHandleData to enqueue streaming chunks and stop on close
    let origHandleData = handler.internalHandleData.bind(handler);
    handler.internalHandleData = function(data, images, discardable, timeLastGenHit, actualInput, socketId, socket, isPreview, batch_id) {
        // Detect streaming chunk: negative batch_index + audio data URL
        if (data.image && AudioStreamPlayer.active) {
            let batchIdx = parseInt(data.batch_index);
            if (batchIdx < 0 && isAudioExt(data.image)) {
                AudioStreamPlayer.enqueue(data.image);
            }
        }
        // Detect session close
        if (data.socket_intention === 'close') {
            AudioStreamPlayer.stop();
        }
        // Always call original handler
        return origHandleData(data, images, discardable, timeLastGenHit, actualInput, socketId, socket, isPreview, batch_id);
    };

    console.log('[audiolab] Streaming audio hooks installed');
}, 600);

// Initial setup
setTimeout(() => {
    console.log('[audiolab] Initial parameter setup starting');
    reviseBackendFeatureSet();
    hideUnsupportableParams();
}, 500);
