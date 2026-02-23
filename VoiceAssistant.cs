using Hartsy.Extensions.VoiceAssistant.AudioAPI;
using Hartsy.Extensions.VoiceAssistant.AudioBackends;
using Hartsy.Extensions.VoiceAssistant.AudioProviders;
using Hartsy.Extensions.VoiceAssistant.AudioServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant;

/// <summary>SwarmUI Voice Assistant Extension - Main Entry Point.
/// Provides modular audio processing (TTS, STT, music gen, voice cloning, etc.)
/// through a provider-based architecture integrated into SwarmUI.</summary>
public class VoiceAssistant : Extension
{
    public static new readonly string Version = "3.0.0";

    /// <summary>Pre-initialization — registers providers and web assets before SwarmUI core is ready.</summary>
    public override void OnPreInit()
    {
        try
        {
            // Set extension directory for Python path resolution
            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            AudioConfiguration.ExtensionDirectory = Path.GetFullPath(Path.Combine(projectRoot, "Extensions", "SwarmUI-VoiceAssistant"));
            Logs.Info($"[VoiceAssistant] Extension directory: {AudioConfiguration.ExtensionDirectory}");

            // Register all built-in audio providers
            AudioProviderDefinitions.RegisterAll();
            Logs.Info($"[VoiceAssistant] Registered {AudioProviderDefinitions.All.Count} audio providers");

            // Register web assets
            ScriptFiles.Add("Assets/voice-api.js");
            ScriptFiles.Add("Assets/voice-ui.js");
            ScriptFiles.Add("Assets/voice-core.js");
            StyleSheetFiles.Add("Assets/voice-assistant.css");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during pre-initialization: {ex.Message}");
        }
    }

    /// <summary>Main initialization — registers the unified audio backend and API endpoints.</summary>
    public override async void OnInit()
    {
        // Register ONE unified backend (replaces separate STT + TTS backend registrations)
        Program.Backends.RegisterBackendType<DynamicAudioBackend>(
            "audio-backend", "Audio Backend",
            "Dynamic audio backend supporting TTS, STT, music generation, and more.", true);

        // Register API endpoints
        VoiceAssistantAPI.Register();
    }

    /// <summary>Creates a standardized error response for API endpoints.</summary>
    public static JObject CreateErrorResponse(string message, string errorCode = null, Exception exception = null)
    {
        JObject response = new()
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(errorCode))
        {
            response["error_code"] = errorCode;
        }
        if (exception != null)
        {
            Logs.Error($"[VoiceAssistant] Exception details: {exception}");
            response["error_type"] = exception.GetType().Name;
        }
        return response;
    }

    /// <summary>Creates a standardized success response for API endpoints.</summary>
    public static JObject CreateSuccessResponse(object data = null, string message = null)
    {
        JObject response = new()
        {
            ["success"] = true,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(message))
        {
            response["message"] = message;
        }
        if (data != null)
        {
            if (data is JObject jObject)
            {
                foreach (JProperty property in jObject.Properties())
                {
                    response[property.Name] = property.Value;
                }
            }
            else
            {
                response["data"] = JToken.FromObject(data);
            }
        }
        return response;
    }
}
