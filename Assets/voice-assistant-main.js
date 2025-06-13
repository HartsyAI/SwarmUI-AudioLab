/**
 * Voice Assistant Main Integration Script - Updated for Generic Endpoints
 * Initializes and connects all Voice Assistant modules with enhanced error handling.
 * This script runs when the DOM is ready and sets up the complete system.
 */

(() => {
    console.log('[VoiceAssistant] Main integration script loaded - v2.0 Generic Endpoints');

    // Global system state
    let systemInitialized = false;
    let initializationAttempts = 0;
    const maxInitializationAttempts = 3;

    /**
     * Initialize the Voice Assistant system with comprehensive error handling
     */
    async function initializeVoiceAssistant() {
        if (systemInitialized) {
            console.log('[VoiceAssistant] System already initialized');
            return;
        }

        initializationAttempts++;
        console.log(`[VoiceAssistant] Starting system initialization (attempt ${initializationAttempts})...`);

        try {
            // Check if required modules are loaded
            const requiredModules = checkRequiredModules();
            if (!requiredModules.allLoaded) {
                throw new Error(`Missing required modules: ${requiredModules.missing.join(', ')}`);
            }

            // Create module instances
            console.log('[VoiceAssistant] Creating module instances...');
            const api = new VoiceAssistantAPI();
            const ui = new VoiceAssistantUI();
            const core = new VoiceAssistantCore();

            // Initialize UI first (sets up DOM element cache and event handlers)
            console.log('[VoiceAssistant] Initializing UI module...');
            const uiInitialized = ui.initialize();
            if (!uiInitialized) {
                throw new Error('Failed to initialize UI module - check if HTML elements are present');
            }

            // Test API connectivity before core initialization
            console.log('[VoiceAssistant] Testing API connectivity...');
            const connectivityResults = await api.testConnectivity();
            if (!connectivityResults.status) {
                console.warn('[VoiceAssistant] API connectivity test failed:', connectivityResults.error);
                ui.showWarning('API connectivity issues detected. Some features may not work properly.');
            }

            // Initialize core with dependencies
            console.log('[VoiceAssistant] Initializing core module...');
            const coreInitialized = await core.initialize(api, ui);
            if (!coreInitialized) {
                throw new Error('Failed to initialize core module');
            }

            // Create global reference for debugging/external access
            console.log('[VoiceAssistant] Creating global reference...');
            window.swarmVoiceAssistant = createGlobalInterface(core, api, ui);

            // Mark as initialized
            systemInitialized = true;

            // Show success notification
            ui.showSuccess('Voice Assistant initialized successfully with generic endpoints!');
            ui.addConsoleMessage('success', 'Voice Assistant v2.0 ready - Generic endpoints loaded');

            console.log('[VoiceAssistant] System initialization completed successfully');
            console.log('[VoiceAssistant] Global access available via: window.swarmVoiceAssistant');

            // Optional: Run initial diagnostics
            await runInitialDiagnostics(api, ui);

        } catch (error) {
            console.error('[VoiceAssistant] System initialization failed:', error);

            // Handle initialization failure
            await handleInitializationFailure(error);
        }
    }

    /**
     * Check if all required modules are loaded
     */
    function checkRequiredModules() {
        const requiredModules = [
            { name: 'VoiceAssistantCore', class: window.VoiceAssistantCore },
            { name: 'VoiceAssistantAPI', class: window.VoiceAssistantAPI },
            { name: 'VoiceAssistantUI', class: window.VoiceAssistantUI }
        ];

        const missing = requiredModules
            .filter(module => !module.class)
            .map(module => module.name);

        return {
            allLoaded: missing.length === 0,
            missing: missing,
            loaded: requiredModules.filter(module => module.class).map(module => module.name)
        };
    }

    /**
     * Create global interface for external access with updated methods
     */
    function createGlobalInterface(core, api, ui) {
        return {
            // Core modules
            core,
            api,
            ui,

            // Version info
            version: '2.0.0',
            buildDate: new Date().toISOString(),
            endpointVersion: 'generic',

            // Convenience methods for external access
            async getStatus() {
                try {
                    return await core.checkServiceStatus();
                } catch (error) {
                    console.error('[VoiceAssistant] Error getting status:', error);
                    return { error: error.message };
                }
            },

            async startService() {
                try {
                    return await core.startService();
                } catch (error) {
                    console.error('[VoiceAssistant] Error starting service:', error);
                    return { error: error.message };
                }
            },

            async stopService() {
                try {
                    return await core.stopService();
                } catch (error) {
                    console.error('[VoiceAssistant] Error stopping service:', error);
                    return { error: error.message };
                }
            },

            // Mode switching
            switchMode(mode) {
                try {
                    core.switchMode(mode);
                    return { success: true, mode };
                } catch (error) {
                    console.error('[VoiceAssistant] Error switching mode:', error);
                    return { error: error.message };
                }
            },

            // Generic endpoint access
            async processSTT(audioData, language = 'en-US', options = {}) {
                try {
                    return await api.processSTT(audioData, language, options);
                } catch (error) {
                    console.error('[VoiceAssistant] Error processing STT:', error);
                    return { error: error.message };
                }
            },

            async processTTS(text, voice = 'default', language = 'en-US', volume = 0.8, options = {}) {
                try {
                    return await api.processTTS(text, voice, language, volume, options);
                } catch (error) {
                    console.error('[VoiceAssistant] Error processing TTS:', error);
                    return { error: error.message };
                }
            },

            async processPipeline(inputType, inputData, pipelineSteps, sessionId = null) {
                try {
                    return await api.processPipeline(inputType, inputData, pipelineSteps, sessionId);
                } catch (error) {
                    console.error('[VoiceAssistant] Error processing pipeline:', error);
                    return { error: error.message };
                }
            },

            async processSpeechToSpeech(audioData, language = 'en-US', voice = 'default', volume = 0.8, options = {}) {
                try {
                    return await api.processSpeechToSpeech(audioData, language, voice, volume, options);
                } catch (error) {
                    console.error('[VoiceAssistant] Error processing speech-to-speech:', error);
                    return { error: error.message };
                }
            },

            // Pipeline step builders
            createSTTStep(language, options) {
                return api.createSTTStep(language, options);
            },

            createTTSStep(voice, language, volume, options) {
                return api.createTTSStep(voice, language, volume, options);
            },

            createCommandStep(processor, options) {
                return api.createCommandStep(processor, options);
            },

            // Debugging methods
            async testAPI() {
                try {
                    return await api.testConnectivity();
                } catch (error) {
                    console.error('[VoiceAssistant] Error testing API:', error);
                    return { error: error.message };
                }
            },

            getState() {
                try {
                    return {
                        core: core.state,
                        api: api.getStatistics(),
                        initialized: systemInitialized,
                        endpointVersion: 'generic'
                    };
                } catch (error) {
                    console.error('[VoiceAssistant] Error getting state:', error);
                    return { error: error.message };
                }
            },

            getConfig() {
                try {
                    return core.config;
                } catch (error) {
                    console.error('[VoiceAssistant] Error getting config:', error);
                    return { error: error.message };
                }
            },

            // Utility methods
            showMessage(type, message) {
                try {
                    switch (type) {
                        case 'success':
                            ui.showSuccess(message);
                            break;
                        case 'error':
                            ui.showError(message);
                            break;
                        case 'warning':
                            ui.showWarning(message);
                            break;
                        case 'info':
                            ui.showInfo(message);
                            break;
                        default:
                            ui.showInfo(message);
                    }
                    return { success: true };
                } catch (error) {
                    console.error('[VoiceAssistant] Error showing message:', error);
                    return { error: error.message };
                }
            },

            // Emergency controls
            emergencyStop() {
                try {
                    core.stopAllActivity();
                    api.emergencyStop();
                    ui.showWarning('Emergency stop activated - all operations halted');
                    return { success: true };
                } catch (error) {
                    console.error('[VoiceAssistant] Error during emergency stop:', error);
                    return { error: error.message };
                }
            },

            // System information
            getSystemInfo() {
                return {
                    version: '2.0.0',
                    endpointVersion: 'generic',
                    initialized: systemInitialized,
                    initializationAttempts: initializationAttempts,
                    modules: checkRequiredModules(),
                    supportedEndpoints: [
                        'ProcessSTT',
                        'ProcessTTS', 
                        'ProcessPipeline',
                        'GetVoiceStatus',
                        'StartVoiceService',
                        'StopVoiceService',
                        'CheckInstallationStatus',
                        'GetInstallationProgress'
                    ],
                    browser: {
                        userAgent: navigator.userAgent,
                        webkitSpeechRecognition: !!window.webkitSpeechRecognition,
                        speechSynthesis: !!window.speechSynthesis,
                        mediaDevices: !!navigator.mediaDevices,
                        mediaRecorder: !!window.MediaRecorder
                    }
                };
            },

            // Cleanup method
            cleanup() {
                try {
                    core.destroy();
                    ui.destroy();
                    api.cleanup();
                    console.log('[VoiceAssistant] System cleanup completed');
                    return { success: true };
                } catch (error) {
                    console.error('[VoiceAssistant] Error during cleanup:', error);
                    return { error: error.message };
                }
            }
        };
    }

    /**
     * Run initial diagnostics after successful initialization
     */
    async function runInitialDiagnostics(api, ui) {
        try {
            console.log('[VoiceAssistant] Running initial diagnostics...');

            // Test API endpoints
            const connectivity = await api.testConnectivity();
            if (connectivity.status) {
                ui.addConsoleMessage('success', `API connectivity: OK (${connectivity.latency}ms)`);

                if (connectivity.service_running) {
                    ui.addConsoleMessage('info', 'Voice service: Running');
                } else {
                    ui.addConsoleMessage('info', 'Voice service: Stopped');
                }

                if (connectivity.dependencies_installed) {
                    ui.addConsoleMessage('success', 'Dependencies: Installed');
                } else {
                    ui.addConsoleMessage('warning', 'Dependencies: Not installed');
                }
            } else {
                ui.addConsoleMessage('error', `API connectivity: Failed - ${connectivity.error}`);
            }

            // Check browser capabilities
            const browserCaps = [];
            if (navigator.mediaDevices) browserCaps.push('MediaDevices');
            if (window.MediaRecorder) browserCaps.push('MediaRecorder');
            if (window.AudioContext || window.webkitAudioContext) browserCaps.push('AudioContext');
            if (window.speechSynthesis) browserCaps.push('SpeechSynthesis');

            ui.addConsoleMessage('info', `Browser capabilities: ${browserCaps.join(', ')}`);

            // Show endpoint information
            ui.addConsoleMessage('info', 'Generic endpoints: ProcessSTT, ProcessTTS, ProcessPipeline');

            console.log('[VoiceAssistant] Initial diagnostics completed');
        } catch (error) {
            console.warn('[VoiceAssistant] Diagnostics failed:', error);
            ui.addConsoleMessage('warning', 'Initial diagnostics failed');
        }
    }

    /**
     * Handle initialization failure with retry logic
     */
    async function handleInitializationFailure(error) {
        console.error('[VoiceAssistant] Initialization failure:', error);

        // Try to show error in UI if possible
        try {
            if (window.VoiceAssistantUI) {
                const ui = new VoiceAssistantUI();
                if (ui.initialize()) {
                    const errorMessage = `Voice Assistant initialization failed: ${error.message}`;

                    if (initializationAttempts < maxInitializationAttempts) {
                        ui.showWarning(`${errorMessage} (Retrying in 3 seconds...)`);
                        ui.addConsoleMessage('warning', `Initialization attempt ${initializationAttempts} failed`);

                        // Retry after delay
                        setTimeout(() => {
                            console.log('[VoiceAssistant] Retrying initialization...');
                            initializeVoiceAssistant();
                        }, 3000);
                    } else {
                        ui.showError(`${errorMessage} (Max attempts reached)`);
                        ui.addConsoleMessage('error', 'Initialization failed permanently');

                        // Provide manual retry option
                        ui.showInfo('You can try to manually reinitialize by calling: window.initVoiceAssistant()');
                        window.initVoiceAssistant = initializeVoiceAssistant;
                    }
                } else {
                    console.error('[VoiceAssistant] Could not initialize UI to show error');
                }
            }
        } catch (uiError) {
            console.error('[VoiceAssistant] Could not show error in UI:', uiError);
        }

        // Always provide a global recovery method
        window.swarmVoiceAssistantRecover = () => {
            systemInitialized = false;
            initializationAttempts = 0;
            initializeVoiceAssistant();
        };
    }

    /**
     * Wait for DOM and all required scripts to load
     */
    function waitForReady() {
        const checkReady = () => {
            // Check DOM readiness
            if (document.readyState === 'loading') {
                return false;
            }

            // Check if required modules are loaded
            const modules = checkRequiredModules();
            if (!modules.allLoaded) {
                console.log('[VoiceAssistant] Waiting for modules:', modules.missing);
                return false;
            }

            // Check if we're in the right context (SwarmUI)
            if (typeof genericRequest !== 'function') {
                console.warn('[VoiceAssistant] SwarmUI context not available (genericRequest not found)');
                return false;
            }

            return true;
        };

        if (checkReady()) {
            initializeVoiceAssistant();
        } else {
            // Set up multiple check mechanisms

            // DOM ready listener
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', () => {
                    setTimeout(checkReady, 500);
                });
            }

            // Polling fallback
            const pollInterval = setInterval(() => {
                if (checkReady()) {
                    clearInterval(pollInterval);
                    initializeVoiceAssistant();
                }
            }, 1000);

            // Timeout fallback
            setTimeout(() => {
                clearInterval(pollInterval);
                if (!systemInitialized) {
                    console.warn('[VoiceAssistant] Initialization timeout, attempting anyway...');
                    initializeVoiceAssistant();
                }
            }, 10000);
        }
    }

    // Global cleanup on page unload
    window.addEventListener('beforeunload', () => {
        try {
            if (window.swarmVoiceAssistant && systemInitialized) {
                console.log('[VoiceAssistant] Performing cleanup on page unload');
                window.swarmVoiceAssistant.cleanup();
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error during cleanup:', error);
        }
    });

    // Global error handler for unhandled voice assistant errors
    window.addEventListener('error', (event) => {
        if (event.error && event.error.message && event.error.message.includes('VoiceAssistant')) {
            console.error('[VoiceAssistant] Unhandled error:', event.error);

            try {
                if (window.swarmVoiceAssistant && window.swarmVoiceAssistant.ui) {
                    window.swarmVoiceAssistant.ui.showError('An unexpected error occurred in Voice Assistant');
                }
            } catch (uiError) {
                console.error('[VoiceAssistant] Could not show error in UI:', uiError);
            }
        }
    });

    // Expose initialization function globally for debugging
    window.initVoiceAssistant = initializeVoiceAssistant;

    // Start the initialization process
    console.log('[VoiceAssistant] Starting initialization process with generic endpoints...');
    waitForReady();
})();
