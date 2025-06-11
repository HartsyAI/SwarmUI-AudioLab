using SwarmUI.Core;
using SwarmUI.Utils;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Hartsy.Extensions.VoiceAssistant.Services;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant;

/// <summary>
/// SwarmUI Voice Assistant Extension - Main Entry Point
/// Provides speech-to-text, text-to-speech, and voice command processing integrated into SwarmUI.
/// This extension manages a Python backend for STT/TTS processing and registers API endpoints
/// for voice interaction with the SwarmUI interface.
/// </summary>
public class VoiceAssistant : Extension
{
    /// <summary>
    /// Extension version for compatibility tracking.
    /// </summary>
    public static new readonly string Version = "1.0.0";

    /// <summary>
    /// Pre-initialization phase - registers web assets before SwarmUI core initialization.
    /// This runs before the main UI is ready, so we only register static assets here.
    /// </summary>
    public override void OnPreInit()
    {
        Logs.Info($"[VoiceAssistant] Starting Voice Assistant Extension v{Version} pre-initialization");

        try
        {
            // Set extension directory for configuration
            ServiceConfiguration.ExtensionDirectory = Path.Combine("src", "Extensions", "SwarmUI-VoiceAssistant");
            Logs.Debug($"[VoiceAssistant] Extension directory: {ServiceConfiguration.ExtensionDirectory}");

            // Register web assets
            RegisterWebAssets();

            Logs.Debug("[VoiceAssistant] Pre-initialization completed successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during pre-initialization: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Pre-init stack trace: {ex}");
        }
    }

    /// <summary>
    /// Main initialization phase - registers API endpoints and validates configuration.
    /// This runs after SwarmUI core is ready, allowing us to register API calls and validate setup.
    /// </summary>
    public override async void OnInit()
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Starting main initialization phase");

            // Validate configuration
            if (!ServiceConfiguration.ValidateConfiguration())
            {
                Logs.Error("[VoiceAssistant] Configuration validation failed. Extension may not function properly.");
            }

            // Register API endpoints
            VoiceAssistantAPI.Register();

            // Initialize services (this doesn't start the backend, just prepares the service layer)
            await InitializeServices();

            Logs.Info($"[VoiceAssistant] Voice Assistant Extension v{Version} initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize extension: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Initialization stack trace: {ex}");
        }
    }

    /// <summary>
    /// Extension shutdown - cleans up resources and stops services.
    /// Ensures graceful termination of background processes and proper resource disposal.
    /// </summary>
    public override void OnShutdown()
    {
        try
        {
            Logs.Info("[VoiceAssistant] Starting extension shutdown");

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
                    BackendHttpClient.Instance.Dispose();
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
            Logs.Debug($"[VoiceAssistant] Shutdown stack trace: {ex}");
        }
    }

    /// <summary>
    /// Registers web assets (JavaScript and CSS files) for the extension.
    /// </summary>
    private void RegisterWebAssets()
    {
        try
        {
            // Register JavaScript files in correct order
            ScriptFiles.Add("Assets/voice-assistant-api.js");
            ScriptFiles.Add("Assets/voice-assistant-ui.js");
            ScriptFiles.Add("Assets/voice-assistant-core.js");
            ScriptFiles.Add("Assets/voice-assistant-main.js");

            // Register CSS file
            StyleSheetFiles.Add("Assets/voice-assistant.css");

            Logs.Debug("[VoiceAssistant] Web assets registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to register web assets: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Initializes the service layer without starting any backends.
    /// This prepares the services for use when needed.
    /// </summary>
    private async Task InitializeServices()
    {
        try
        {
            // Initialize the HTTP client for backend communication
            BackendHttpClient.Instance.Initialize();

            // Prepare the Python backend service (doesn't start the process)
            await PythonBackendService.Instance.InitializeAsync();

            Logs.Debug("[VoiceAssistant] Services initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VoiceAssistant] Service initialization warning: {ex.Message}");
            // Don't fail the extension if service initialization has issues
        }
    }
}

/// <summary>
/// Extension methods for string operations used in the Voice Assistant.
/// These provide utility methods for text analysis and command detection.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Checks if a string contains any of the specified substrings (case-insensitive).
    /// Used for command detection and intent recognition in voice processing.
    /// </summary>
    /// <param name="text">The text to search in</param>
    /// <param name="values">Array of strings to search for</param>
    /// <returns>True if any of the values are found in the text</returns>
    public static bool ContainsAny(this string text, string[] values)
    {
        if (string.IsNullOrEmpty(text) || values == null)
            return false;

        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalizes text for command processing by converting to lowercase and trimming.
    /// </summary>
    /// <param name="text">The text to normalize</param>
    /// <returns>Normalized text suitable for command matching</returns>
    public static string NormalizeForCommand(this string text)
    {
        return text?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>
    /// Truncates text to a maximum length with ellipsis.
    /// </summary>
    /// <param name="text">The text to truncate</param>
    /// <param name="maxLength">Maximum length including ellipsis</param>
    /// <returns>Truncated text with ellipsis if needed</returns>
    public static string Truncate(this string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text.Length > maxLength ? text[..(maxLength - 3)] + "..." : text;
    }
}

/// <summary>
/// TODO: Future command processing system.
/// This will be implemented in a future version to handle voice commands and image generation.
/// For now, we return placeholder responses.
/// </summary>
public static class CommandProcessor
{
    /// <summary>
    /// TODO: Process voice commands and generate appropriate responses.
    /// This is a placeholder for future implementation.
    /// </summary>
    /// <param name="text">The transcribed voice command</param>
    /// <returns>A placeholder command response</returns>
    public static async Task<Models.CommandResponse> ProcessCommandAsync(string text)
    {
        // TODO: Implement proper command processing
        // This should:
        // 1. Parse the command text to understand intent
        // 2. Extract parameters (image description, settings, etc.)
        // 3. Execute the appropriate action (image generation, settings change, etc.)
        // 4. Return a structured response

        await Task.Delay(1); // Placeholder for async work

        return new Models.CommandResponse
        {
            Text = "Command processing is not yet implemented. This will be added in a future version.",
            Command = "placeholder",
            Confidence = 0.0f
        };
    }
}
