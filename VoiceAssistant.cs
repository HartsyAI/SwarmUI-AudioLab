using SwarmUI.Core;
using SwarmUI.Utils;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Hartsy.Extensions.VoiceAssistant.Services;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant;

/// <summary>SwarmUI Voice Assistant Extension - Main Entry Point
/// Provides speech-to-text, text-to-speech, and voice command processing integrated into SwarmUI.
/// This extension manages a Python backend for STT/TTS processing and registers API endpoints
/// for voice interaction with the SwarmUI interface.</summary>
public class VoiceAssistant : Extension
{
    /// <summary> Extension version for compatibility tracking.</summary>
    public static new readonly string Version = "0.0.1";

    /// <summary>Pre-initialization phase - registers web assets before SwarmUI core initialization.
    /// This runs before the main UI is ready, so we only register static assets here.</summary>
    public override void OnPreInit()
    {
        try
        {
            ServiceConfiguration.ExtensionDirectory = Path.Combine("src", "Extensions", "SwarmUI-VoiceAssistant");
            Logs.Debug($"[VoiceAssistant] Extension directory: {ServiceConfiguration.ExtensionDirectory}");
            ScriptFiles.Add("Assets/voice-api.js");
            ScriptFiles.Add("Assets/voice-ui.js");
            ScriptFiles.Add("Assets/voice-core.js");
            // Register CSS file
            StyleSheetFiles.Add("Assets/voice-assistant.css");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during pre-initialization: {ex.Message}");
        }
    }

    /// <summary>Main initialization phase - registers API endpoints and validates configuration.
    /// This runs after SwarmUI core is ready, allowing us to register API calls and validate setup.</summary>
    public override async void OnInit()
    {
        try
        {
            if (!ServiceConfiguration.ValidateConfiguration())
            {
                Logs.Error("[VoiceAssistant] Configuration validation failed. Extension may not function properly.");
            }
            VoiceAssistantAPI.Register(); // Register API endpoints to use in Swarm
            PythonBackendClient.Instance.Initialize(); // Initialize the HTTP client for the python backend
            // Prepare the Python backend service (doesn't start the service)
            await PythonBackendService.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize extension: {ex.Message}");
        }
    }

    /// <summary>Extension shutdown - cleans up resources and stops services.
    /// Ensures graceful termination of background processes and proper resource disposal.</summary>
    public override void OnShutdown()
    {
        try
        {
            // Stop all services with timeout to prevent hanging
            var shutdownTask = Task.Run(async () =>
            {
                try
                {
                    await PythonBackendService.Instance.StopAsync();
                    Logs.Debug("[VoiceAssistant] Python backend stopped successfully");
                }
                catch (Exception ex)
                {
                    Logs.Error($"[VoiceAssistant] Error stopping backend during shutdown: {ex.Message}");
                }
                try
                {
                    PythonBackendClient.Instance.Dispose();
                    Logs.Debug("[VoiceAssistant] HTTP client disposed");
                }
                catch (Exception ex)
                {
                    Logs.Error($"[VoiceAssistant] Error disposing HTTP client: {ex.Message}");
                }
            });
            // Wait for shutdown with timeout
            if (!shutdownTask.Wait(TimeSpan.FromSeconds(15)))
            {
                Logs.Warning("[VoiceAssistant] Shutdown timed out, forcing termination");
                // Force kill any remaining processes
                PythonBackendService.Instance.ForceStop();
            }
            Logs.Info("[VoiceAssistant] Extension shutdown completed");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during shutdown: {ex.Message}");
        }
    }
}
