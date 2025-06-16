using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
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
        VoiceAssistantAPI.Register(); // Register API endpoints to use in Swarm
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
