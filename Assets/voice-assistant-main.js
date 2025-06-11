/**
 * Voice Assistant Main Integration Script
 * Initializes and connects all Voice Assistant modules.
 * This script runs when the DOM is ready and sets up the complete system.
 */

(() => {
    console.log('[VoiceAssistant] Main integration script loaded - Production v1.0');

    /**
     * Initialize the Voice Assistant system
     */
    async function initializeVoiceAssistant() {
        try {
            console.log('[VoiceAssistant] Starting system initialization...');

            // Check if required modules are loaded
            if (!window.VoiceAssistantCore || !window.VoiceAssistantAPI || !window.VoiceAssistantUI) {
                throw new Error('Required modules not loaded. Please ensure all script files are included.');
            }

            // Create module instances
            const api = new VoiceAssistantAPI();
            const ui = new VoiceAssistantUI();
            const core = new VoiceAssistantCore();

            // Initialize UI first (sets up DOM element cache and event handlers)
            const uiInitialized = ui.initialize();
            if (!uiInitialized) {
                throw new Error('Failed to initialize UI module');
            }

            // Initialize core with dependencies
            const coreInitialized = await core.initialize(api, ui);
            if (!coreInitialized) {
                throw new Error('Failed to initialize core module');
            }

            // Store global reference for debugging/external access
            window.swarmVoiceAssistant = {
                core,
                api,
                ui,
                version: '1.0.0',

                // Convenience methods for external access
                getStatus: () => core.checkServiceStatus(),
                startService: () => core.startService(),
                stopService: () => core.stopService(),

                // Debugging methods
                testAPI: () => api.testConnectivity(),
                getState: () => core.state,
                getConfig: () => core.config
            };

            console.log('[VoiceAssistant] System initialization completed successfully');
            console.log('[VoiceAssistant] Global access available via: window.swarmVoiceAssistant');

        } catch (error) {
            console.error('[VoiceAssistant] System initialization failed:', error);

            // Try to show error in UI if possible
            if (window.VoiceAssistantUI) {
                try {
                    const ui = new VoiceAssistantUI();
                    ui.initialize();
                    ui.showError('Voice Assistant initialization failed: ' + error.message);
                } catch (uiError) {
                    console.error('[VoiceAssistant] Could not show error in UI:', uiError);
                }
            }
        }
    }

    /**
     * Wait for DOM to be ready and all required scripts to load
     */
    function waitForReady() {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initializeVoiceAssistant);
        } else {
            // DOM is already ready, check if modules are loaded
            if (window.VoiceAssistantCore && window.VoiceAssistantAPI && window.VoiceAssistantUI) {
                initializeVoiceAssistant();
            } else {
                // Wait a bit for modules to load
                setTimeout(() => {
                    if (window.VoiceAssistantCore && window.VoiceAssistantAPI && window.VoiceAssistantUI) {
                        initializeVoiceAssistant();
                    } else {
                        console.error('[VoiceAssistant] Required modules not found after timeout');
                    }
                }, 1000);
            }
        }
    }

    // Start the initialization process
    waitForReady();

    // Global cleanup on page unload
    window.addEventListener('beforeunload', () => {
        try {
            if (window.swarmVoiceAssistant) {
                console.log('[VoiceAssistant] Performing cleanup on page unload');

                if (window.swarmVoiceAssistant.core) {
                    window.swarmVoiceAssistant.core.destroy();
                }

                if (window.swarmVoiceAssistant.ui) {
                    window.swarmVoiceAssistant.ui.destroy();
                }
            }
        } catch (error) {
            console.error('[VoiceAssistant] Error during cleanup:', error);
        }
    });
})();
