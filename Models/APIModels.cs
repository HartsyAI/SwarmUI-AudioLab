using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Models;

/// <summary>
/// API request and response models for the Voice Assistant extension.
/// </summary>

#region Request Models

/// <summary>
/// Base request model with common validation.
/// </summary>
public abstract class BaseRequest
{
    public string SessionId { get; set; } = string.Empty;

    public virtual void Validate()
    {
        // Base validation can be added here
    }
}

/// <summary>
/// Request model for voice input processing.
/// </summary>
public class VoiceInputRequest : BaseRequest
{
    public string AudioData { get; set; } = string.Empty;
    public string Language { get; set; } = "en-US";
    public string Voice { get; set; } = "default";
    public float Volume { get; set; } = 0.8f;

    public override void Validate()
    {
        base.Validate();
        Common.ErrorHandling.Validation.RequireNonEmpty(AudioData, nameof(AudioData));
        Common.ErrorHandling.Validation.RequireValidLanguage(Language);
        Common.ErrorHandling.Validation.RequireValidVoice(Voice);
        Common.ErrorHandling.Validation.RequireValidVolume(Volume);
    }
}

/// <summary>
/// Request model for text command processing.
/// </summary>
public class TextCommandRequest : BaseRequest
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = "en-US";
    public string Voice { get; set; } = "default";
    public float Volume { get; set; } = 0.8f;

    public override void Validate()
    {
        base.Validate();
        Common.ErrorHandling.Validation.RequireValidTextLength(Text);
        Common.ErrorHandling.Validation.RequireValidLanguage(Language);
        Common.ErrorHandling.Validation.RequireValidVoice(Voice);
        Common.ErrorHandling.Validation.RequireValidVolume(Volume);
    }
}

/// <summary>
/// Request model for Python backend STT calls.
/// </summary>
public class STTRequest
{
    public string AudioData { get; set; } = string.Empty;
    public string Language { get; set; } = "en-US";
}

/// <summary>
/// Request model for Python backend TTS calls.
/// </summary>
public class TTSRequest
{
    public string Text { get; set; } = string.Empty;
    public string Voice { get; set; } = "default";
    public string Language { get; set; } = "en-US";
    public float Volume { get; set; } = 0.8f;
}

#endregion

#region Response Models

/// <summary>
/// Base response model with common fields.
/// </summary>
public class BaseResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public JObject ToJObject()
    {
        var result = new JObject
        {
            ["success"] = Success,
            ["timestamp"] = Timestamp.ToString("O")
        };

        if (!string.IsNullOrEmpty(Message))
            result["message"] = Message;

        if (!string.IsNullOrEmpty(ErrorCode))
            result["error_code"] = ErrorCode;

        return result;
    }
}

/// <summary>
/// Response model for voice processing operations.
/// </summary>
public class VoiceProcessingResponse : BaseResponse
{
    public string Transcription { get; set; } = string.Empty;
    public string AiResponse { get; set; } = string.Empty;
    public string AudioResponse { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public float Confidence { get; set; } = 0.0f;
    public double ProcessingTime { get; set; } = 0.0;

    public new JObject ToJObject()
    {
        var result = base.ToJObject();

        if (!string.IsNullOrEmpty(Transcription))
            result["transcription"] = Transcription;

        if (!string.IsNullOrEmpty(AiResponse))
            result["ai_response"] = AiResponse;

        if (!string.IsNullOrEmpty(AudioResponse))
            result["audio_response"] = AudioResponse;

        if (!string.IsNullOrEmpty(CommandType))
            result["command"] = CommandType;

        if (!string.IsNullOrEmpty(SessionId))
            result["session_id"] = SessionId;

        if (Confidence > 0)
            result["confidence"] = Confidence;

        if (ProcessingTime > 0)
            result["processing_time"] = ProcessingTime;

        return result;
    }
}

/// <summary>
/// Response model for service status operations.
/// </summary>
public class ServiceStatusResponse : BaseResponse
{
    public bool BackendRunning { get; set; }
    public bool BackendHealthy { get; set; }
    public string BackendUrl { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool HasExited { get; set; }
    public string Version { get; set; } = "1.0.0";

    public new JObject ToJObject()
    {
        var result = base.ToJObject();
        result["backend_running"] = BackendRunning;
        result["backend_healthy"] = BackendHealthy;
        result["backend_url"] = BackendUrl;
        result["process_id"] = ProcessId;
        result["has_exited"] = HasExited;
        result["version"] = Version;
        return result;
    }
}

/// <summary>
/// Response model for installation status operations.
/// </summary>
public class InstallationStatusResponse : BaseResponse
{
    public bool PythonDetected { get; set; }
    public string PythonPath { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public bool IsEmbeddedPython { get; set; }
    public bool DependenciesInstalled { get; set; }
    public JObject InstallationDetails { get; set; } = new();
    public string[] RequiredLibraries { get; set; } = Array.Empty<string>();
    public bool NoFallbacks { get; set; } = true;
    public bool StrictRequirements { get; set; } = true;

    public new JObject ToJObject()
    {
        var result = base.ToJObject();
        result["python_detected"] = PythonDetected;
        result["python_path"] = PythonPath;
        result["operating_system"] = OperatingSystem;
        result["is_embedded_python"] = IsEmbeddedPython;
        result["dependencies_installed"] = DependenciesInstalled;
        result["installation_details"] = InstallationDetails;
        result["required_libraries"] = JArray.FromObject(RequiredLibraries);
        result["no_fallbacks"] = NoFallbacks;
        result["strict_requirements"] = StrictRequirements;
        return result;
    }
}

/// <summary>
/// Response model for installation progress operations.
/// </summary>
public class InstallationProgressResponse : BaseResponse
{
    public int Progress { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public string CurrentPackage { get; set; } = string.Empty;
    public int DownloadProgress { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    public new JObject ToJObject()
    {
        var result = base.ToJObject();
        result["progress"] = Progress;
        result["current_step"] = CurrentStep;
        result["current_package"] = CurrentPackage;
        result["download_progress"] = DownloadProgress;
        result["status_message"] = StatusMessage;
        result["is_complete"] = IsComplete;
        result["has_error"] = HasError;
        result["error_message"] = ErrorMessage;
        return result;
    }
}

#endregion

#region Internal Models

/// <summary>
/// Data structure for command processing results.
/// TODO: Implement proper command processing in future versions.
/// </summary>
public class CommandResponse
{
    public string Text { get; set; } = string.Empty;
    public string AudioBase64 { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public float Confidence { get; set; } = 0.0f;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Python environment information for dependency management.
/// </summary>
public class PythonEnvironmentInfo
{
    public string PythonPath { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public bool IsEmbedded { get; set; }
    public string Version { get; set; } = string.Empty;
    public bool IsValid => !string.IsNullOrEmpty(PythonPath) && File.Exists(PythonPath);
}

/// <summary>
/// Backend service health information.
/// </summary>
public class BackendHealthInfo
{
    public bool IsRunning { get; set; }
    public bool IsHealthy { get; set; }
    public bool IsResponding { get; set; }
    public DateTime LastCheck { get; set; } = DateTime.UtcNow;
    public string ErrorMessage { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool HasExited { get; set; }
}

#endregion
