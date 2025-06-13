using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Models;

#region Base Models
/// <summary>Base request model with common validation.</summary>
public abstract class BaseRequest
{
    public string SessionId { get; set; } = string.Empty;

    public virtual void Validate()
    {
        // TODO: Add base validation logic
    }
}

/// <summary>Base response model with common fields.</summary>
public class BaseResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double ProcessingTime { get; set; } = 0.0;

    public virtual JObject ToJObject()
    {
        var result = new JObject
        {
            ["success"] = Success,
            ["timestamp"] = Timestamp.ToString("O"),
            ["processing_time"] = ProcessingTime
        };
        if (!string.IsNullOrEmpty(Message))
            result["message"] = Message;
        if (!string.IsNullOrEmpty(ErrorCode))
            result["error_code"] = ErrorCode;
        return result;
    }
}
#endregion

#region STT Models
/// <summary>Options for STT processing configuration.</summary>
public class STTOptions
{
    public bool ReturnConfidence { get; set; } = true;
    public bool ReturnAlternatives { get; set; } = false;
    public string ModelPreference { get; set; } = "accuracy"; // accuracy|speed
    public Dictionary<string, object> CustomOptions { get; set; } = new();
}

/// <summary>Request model for pure STT processing.</summary>
public class STTRequest : BaseRequest
{
    public string AudioData { get; set; } = string.Empty;
    public string Language { get; set; } = "en-US";
    public STTOptions Options { get; set; } = new();

    public override void Validate()
    {
        base.Validate();
        Common.ErrorHandling.Validation.RequireNonEmpty(AudioData, nameof(AudioData));
        Common.ErrorHandling.Validation.RequireValidLanguage(Language);

        // Validate audio data is base64
        if (!IsValidBase64(AudioData))
        {
            throw new ArgumentException("AudioData must be valid base64 encoded audio");
        }
    }

    private bool IsValidBase64(string data)
    {
        try
        {
            Convert.FromBase64String(data);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Response model for STT processing.</summary>
public class STTResponse : BaseResponse
{
    public string Transcription { get; set; } = string.Empty;
    public float Confidence { get; set; } = 0.0f;
    public string[] Alternatives { get; set; } = Array.Empty<string>();
    public STTMetadata Metadata { get; set; } = new();

    public override JObject ToJObject()
    {
        var result = base.ToJObject();
        result["transcription"] = Transcription;
        result["confidence"] = Confidence;
        result["alternatives"] = JArray.FromObject(Alternatives);
        result["metadata"] = Metadata.ToJObject();
        return result;
    }
}

/// <summary>Metadata for STT processing results.</summary>
public class STTMetadata
{
    public string ModelUsed { get; set; } = string.Empty;
    public double AudioDuration { get; set; } = 0.0;
    public string AudioFormat { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 0;

    public JObject ToJObject()
    {
        return new JObject
        {
            ["model_used"] = ModelUsed,
            ["audio_duration"] = AudioDuration,
            ["audio_format"] = AudioFormat,
            ["sample_rate"] = SampleRate
        };
    }
}
#endregion

#region TTS Models
/// <summary>Options for TTS processing configuration.</summary>
public class TTSOptions
{
    public float Speed { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public string Format { get; set; } = "wav"; // wav|mp3
    public Dictionary<string, object> CustomOptions { get; set; } = new();
}

/// <summary>Request model for pure TTS processing.</summary>
public class TTSRequest : BaseRequest
{
    public string Text { get; set; } = string.Empty;
    public string Voice { get; set; } = "default";
    public string Language { get; set; } = "en-US";
    public float Volume { get; set; } = 0.8f;
    public TTSOptions Options { get; set; } = new();

    public override void Validate()
    {
        base.Validate();
        Common.ErrorHandling.Validation.RequireValidTextLength(Text);
        Common.ErrorHandling.Validation.RequireValidLanguage(Language);
        Common.ErrorHandling.Validation.RequireValidVoice(Voice);
        Common.ErrorHandling.Validation.RequireValidVolume(Volume);
        // Validate options
        if (Options.Speed < 0.1f || Options.Speed > 3.0f)
        {
            throw new ArgumentException("Speed must be between 0.1 and 3.0");
        }
        if (Options.Pitch < 0.1f || Options.Pitch > 3.0f)
        {
            throw new ArgumentException("Pitch must be between 0.1 and 3.0");
        }
        var validFormats = new[] { "wav", "mp3" };
        if (!validFormats.Contains(Options.Format.ToLower()))
        {
            throw new ArgumentException($"Format must be one of: {string.Join(", ", validFormats)}");
        }
    }
}

/// <summary>Response model for TTS processing.</summary>
public class TTSResponse : BaseResponse
{
    public string AudioData { get; set; } = string.Empty;
    public double Duration { get; set; } = 0.0;
    public TTSMetadata Metadata { get; set; } = new();

    public override JObject ToJObject()
    {
        var result = base.ToJObject();
        result["audio_data"] = AudioData;
        result["duration"] = Duration;
        result["metadata"] = Metadata.ToJObject();
        return result;
    }
}

/// <summary>Metadata for TTS processing results.</summary>
public class TTSMetadata
{
    public string VoiceUsed { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 0;
    public string AudioFormat { get; set; } = string.Empty;
    public int AudioChannels { get; set; } = 1;

    public JObject ToJObject()
    {
        return new JObject
        {
            ["voice_used"] = VoiceUsed,
            ["sample_rate"] = SampleRate,
            ["audio_format"] = AudioFormat,
            ["audio_channels"] = AudioChannels
        };
    }
}
#endregion

#region Pipeline Models
/// <summary>Configuration for a single pipeline step.</summary>
public class PipelineStep
{
    public string Type { get; set; } = string.Empty; // stt|tts|command_processing
    public bool Enabled { get; set; } = true;
    public JObject Config { get; set; } = new();

    public void Validate()
    {
        var validTypes = new[] { "stt", "tts", "command_processing" };
        if (!validTypes.Contains(Type.ToLower()))
        {
            throw new ArgumentException($"Pipeline step type must be one of: {string.Join(", ", validTypes)}");
        }
    }
}

/// <summary>Request model for configurable pipeline processing.</summary>
public class PipelineRequest : BaseRequest
{
    public string InputType { get; set; } = "audio"; // audio|text
    public string InputData { get; set; } = string.Empty;
    public List<PipelineStep> PipelineSteps { get; set; } = new();

    public override void Validate()
    {
        base.Validate();
        var validInputTypes = new[] { "audio", "text" };
        if (!validInputTypes.Contains(InputType.ToLower()))
        {
            throw new ArgumentException($"InputType must be one of: {string.Join(", ", validInputTypes)}");
        }
        Common.ErrorHandling.Validation.RequireNonEmpty(InputData, nameof(InputData));
        if (PipelineSteps.Count == 0)
        {
            throw new ArgumentException("At least one pipeline step must be specified");
        }
        // Validate each step
        foreach (var step in PipelineSteps)
        {
            step.Validate();
        }
        // Validate pipeline logic
        ValidatePipelineLogic();
    }

    private void ValidatePipelineLogic()
    {
        var enabledSteps = PipelineSteps.Where(s => s.Enabled).ToList();
        if (enabledSteps.Count == 0)
        {
            throw new ArgumentException("At least one pipeline step must be enabled");
        }
        // If input is audio, first step must be STT (unless we're only doing audio processing)
        if (InputType.ToLower() == "audio")
        {
            var firstStep = enabledSteps.First();
            if (firstStep.Type.ToLower() != "stt")
            {
                throw new ArgumentException("When input type is 'audio', first enabled step must be 'stt'");
            }
        }
        // If input is text, first step cannot be STT
        if (InputType.ToLower() == "text")
        {
            var firstStep = enabledSteps.First();
            if (firstStep.Type.ToLower() == "stt")
            {
                throw new ArgumentException("When input type is 'text', first step cannot be 'stt'");
            }
        }
    }
}

/// <summary>Response model for pipeline processing.</summary>
public class PipelineResponse : BaseResponse
{
    public Dictionary<string, JObject> PipelineResults { get; set; } = new();
    public List<string> ExecutedSteps { get; set; } = new();
    public double TotalProcessingTime { get; set; } = 0.0;

    public override JObject ToJObject()
    {
        var result = base.ToJObject();
        JObject pipelineResultsObj = [];
        foreach (var kvp in PipelineResults)
        {
            pipelineResultsObj[kvp.Key] = kvp.Value;
        }
        result["pipeline_results"] = pipelineResultsObj;
        result["executed_steps"] = JArray.FromObject(ExecutedSteps);
        result["total_processing_time"] = TotalProcessingTime;
        return result;
    }
}
#endregion

#region Service Management Models (Updated)
/// <summary>Response model for service status operations.</summary>
public class ServiceStatusResponse : BaseResponse
{
    public bool BackendRunning { get; set; }
    public bool BackendHealthy { get; set; }
    public string BackendUrl { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool HasExited { get; set; }
    public string Version { get; set; } = "2.0.0";
    public Dictionary<string, bool> Services { get; set; } = new();

    public override JObject ToJObject()
    {
        var result = base.ToJObject();
        result["backend_running"] = BackendRunning;
        result["backend_healthy"] = BackendHealthy;
        result["backend_url"] = BackendUrl;
        result["process_id"] = ProcessId;
        result["has_exited"] = HasExited;
        result["version"] = Version;
        JObject servicesObj = [];
        foreach (var kvp in Services)
        {
            servicesObj[kvp.Key] = kvp.Value;
        }
        result["services"] = servicesObj;
        return result;
    }
}

/// <summary>Response model for installation status operations.</summary>
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

    public override JObject ToJObject()
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

/// <summary>Response model for installation progress operations.</summary>
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

    public override JObject ToJObject()
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

#region Internal Models (Updated)
/// <summary>Data structure for command processing results.
/// Used in pipeline processing for command steps.</summary>
public class CommandResponse
{
    public string Text { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public float Confidence { get; set; } = 0.0f;
    public Dictionary<string, object> Parameters { get; set; } = new();
    public bool Success { get; set; } = false;
    public string ErrorMessage { get; set; } = string.Empty;

    public JObject ToJObject()
    {
        var result = new JObject
        {
            ["text"] = Text,
            ["command"] = Command,
            ["confidence"] = Confidence,
            ["success"] = Success
        };
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            result["error_message"] = ErrorMessage;
        }
        if (Parameters.Count > 0)
        {
            result["parameters"] = JObject.FromObject(Parameters);
        }
        return result;
    }
}

/// <summary>Python environment information for dependency management.</summary>
public class PythonEnvironmentInfo
{
    public string PythonPath { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public bool IsEmbedded { get; set; }
    public string Version { get; set; } = string.Empty;
    public bool IsValid => !string.IsNullOrEmpty(PythonPath) && File.Exists(PythonPath);
}

/// <summary>Backend service health information.</summary>
public class BackendHealthInfo
{
    public bool IsRunning { get; set; }
    public bool IsHealthy { get; set; }
    public bool IsResponding { get; set; }
    public DateTime LastCheck { get; set; } = DateTime.UtcNow;
    public string ErrorMessage { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool HasExited { get; set; }
    public Dictionary<string, bool> Services { get; set; } = new();
}
#endregion

#region Server-Side Recording Models

/// <summary>Request model for server-side recording operations.</summary>
public class RecordingRequest : BaseRequest
{
    public int Duration { get; set; } = 10; // Duration in seconds
    public string Language { get; set; } = "en-US";
    public string Mode { get; set; } = "stt"; // stt, sts, or raw
    public Dictionary<string, object> Options { get; set; } = new();

    public override void Validate()
    {
        base.Validate();
        
        // Validate duration (1-30 seconds)
        if (Duration < 1 || Duration > 30)
        {
            throw new ArgumentException("Duration must be between 1 and 30 seconds");
        }
        
        Common.ErrorHandling.Validation.RequireValidLanguage(Language);
        
        string[] validModes = new[] { "stt", "sts", "raw" };
        if (!validModes.Contains(Mode.ToLower()))
        {
            throw new ArgumentException($"Mode must be one of: {string.Join(", ", validModes)}");
        }
    }
}

/// <summary>Response model for recording operations.</summary>
public class RecordingResponse : BaseResponse
{
    public bool IsRecording { get; set; }
    public string RecordingId { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string AudioData { get; set; } = string.Empty; // Base64 encoded when recording completes
    public string Transcription { get; set; } = string.Empty; // For STT mode
    public string AIResponse { get; set; } = string.Empty; // For STS mode
    public RecordingMetadata Metadata { get; set; } = new();

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["is_recording"] = IsRecording;
        result["recording_id"] = RecordingId;
        result["duration"] = Duration;
        result["mode"] = Mode;
        
        if (!string.IsNullOrEmpty(AudioData))
            result["audio_data"] = AudioData;
        if (!string.IsNullOrEmpty(Transcription))
            result["transcription"] = Transcription;
        if (!string.IsNullOrEmpty(AIResponse))
            result["ai_response"] = AIResponse;
            
        result["metadata"] = Metadata.ToJObject();
        return result;
    }
}

/// <summary>Response model for recording status queries.</summary>
public class RecordingStatusResponse : BaseResponse
{
    public bool IsRecording { get; set; }
    public string RecordingId { get; set; } = string.Empty;
    public int ElapsedSeconds { get; set; }
    public int TotalDuration { get; set; }
    public string Status { get; set; } = string.Empty; // "idle", "recording", "processing", "completed"

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["is_recording"] = IsRecording;
        result["recording_id"] = RecordingId;
        result["elapsed_seconds"] = ElapsedSeconds;
        result["total_duration"] = TotalDuration;
        result["status"] = Status;
        return result;
    }
}

/// <summary>Metadata for recording operations.</summary>
public class RecordingMetadata
{
    public string DeviceUsed { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 16000;
    public string AudioFormat { get; set; } = "wav";
    public int AudioChannels { get; set; } = 1;
    public double ActualDuration { get; set; } = 0.0;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }

    public JObject ToJObject()
    {
        return new JObject
        {
            ["device_used"] = DeviceUsed,
            ["sample_rate"] = SampleRate,
            ["audio_format"] = AudioFormat,
            ["audio_channels"] = AudioChannels,
            ["actual_duration"] = ActualDuration,
            ["start_time"] = StartTime.ToString("O"),
            ["end_time"] = EndTime?.ToString("O")
        };
    }
}

#endregion