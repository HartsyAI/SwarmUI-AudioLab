using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.AudioLab.WebAPI.Models;

#region Base Models

/// <summary>Base request model with common validation.</summary>
public abstract class BaseRequest
{
    /// <summary>User session identifier</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Validates the request. Override in derived classes for specific validation.</summary>
    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            throw new ArgumentException("SessionId is required");
        }
    }
}

/// <summary>Base response model with common fields.</summary>
public class BaseResponse
{
    /// <summary>Whether the operation was successful</summary>
    public bool Success { get; set; }

    /// <summary>Human-readable message</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Error code for programmatic handling</summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>When the response was generated</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>How long the operation took in seconds</summary>
    public double ProcessingTime { get; set; } = 0.0;

    /// <summary>Converts the response to a JObject for API serialization.</summary>
    public virtual JObject ToJObject()
    {
        JObject result = new()
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

/// <summary>Configuration options for STT processing.</summary>
public class STTOptions
{
    /// <summary>Whether to return confidence scores</summary>
    public bool ReturnConfidence { get; set; } = true;

    /// <summary>Whether to return alternative transcriptions</summary>
    public bool ReturnAlternatives { get; set; } = false;

    /// <summary>Model optimization preference</summary>
    public string ModelPreference { get; set; } = "accuracy"; // accuracy|speed

    /// <summary>Custom processing options</summary>
    public Dictionary<string, object> CustomOptions { get; set; } = [];
}

/// <summary>Request for STT processing.</summary>
public class STTRequest : BaseRequest
{
    /// <summary>Base64 encoded audio data</summary>
    public string AudioData { get; set; } = string.Empty;

    /// <summary>Language code for transcription</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>STT processing options</summary>
    public STTOptions Options { get; set; } = new();

    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrWhiteSpace(AudioData))
        {
            throw new ArgumentException("AudioData is required");
        }

        if (!IsValidBase64(AudioData))
        {
            throw new ArgumentException("AudioData must be valid base64 encoded audio");
        }

        if (string.IsNullOrWhiteSpace(Language))
        {
            throw new ArgumentException("Language is required");
        }
    }

    private static bool IsValidBase64(string data)
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

/// <summary>Response from STT processing.</summary>
public class STTResponse : BaseResponse
{
    /// <summary>Transcribed text</summary>
    public string Transcription { get; set; } = string.Empty;

    /// <summary>Confidence score (0.0 to 1.0)</summary>
    public float Confidence { get; set; } = 0.0f;

    /// <summary>Language used for transcription</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Alternative transcriptions</summary>
    public string[] Alternatives { get; set; } = [];

    /// <summary>Session identifier</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Additional processing metadata</summary>
    public Dictionary<string, object> Metadata { get; set; } = [];

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["transcription"] = Transcription;
        result["confidence"] = Confidence;
        result["language"] = Language;
        result["alternatives"] = JArray.FromObject(Alternatives);
        result["session_id"] = SessionId;
        result["metadata"] = JObject.FromObject(Metadata);
        return result;
    }
}

#endregion

#region TTS Models

/// <summary>Configuration options for TTS processing.</summary>
public class TTSOptions
{
    /// <summary>Speech speed multiplier (0.1 to 3.0)</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>Voice pitch multiplier (0.1 to 3.0)</summary>
    public float Pitch { get; set; } = 1.0f;

    /// <summary>Audio output format</summary>
    public string Format { get; set; } = "wav";

    /// <summary>Custom processing options</summary>
    public Dictionary<string, object> CustomOptions { get; set; } = [];
}

/// <summary>Request for TTS processing.</summary>
public class TTSRequest : BaseRequest
{
    /// <summary>Text to convert to speech</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Voice identifier to use</summary>
    public string Voice { get; set; } = "default";

    /// <summary>Language code for synthesis</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Volume level (0.0 to 1.0)</summary>
    public float Volume { get; set; } = 0.8f;

    /// <summary>TTS processing options</summary>
    public TTSOptions Options { get; set; } = new();

    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Text is required");
        }

        if (Text.Length > 1000)
        {
            throw new ArgumentException("Text must be 1000 characters or less");
        }

        if (Volume < 0.0f || Volume > 1.0f)
        {
            throw new ArgumentException("Volume must be between 0.0 and 1.0");
        }

        if (Options.Speed < 0.1f || Options.Speed > 3.0f)
        {
            throw new ArgumentException("Speed must be between 0.1 and 3.0");
        }

        if (Options.Pitch < 0.1f || Options.Pitch > 3.0f)
        {
            throw new ArgumentException("Pitch must be between 0.1 and 3.0");
        }

        string[] validFormats = ["wav", "mp3"];
        if (!validFormats.Contains(Options.Format.ToLowerInvariant()))
        {
            throw new ArgumentException($"Format must be one of: {string.Join(", ", validFormats)}");
        }
    }
}

/// <summary>Response from TTS processing.</summary>
public class TTSResponse : BaseResponse
{
    /// <summary>Base64 encoded audio data</summary>
    public string AudioData { get; set; } = string.Empty;

    /// <summary>Original text that was synthesized</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Voice used for synthesis</summary>
    public string Voice { get; set; } = "default";

    /// <summary>Language used for synthesis</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Volume level applied</summary>
    public float Volume { get; set; } = 0.8f;

    /// <summary>Audio duration in seconds</summary>
    public double Duration { get; set; } = 0.0;

    /// <summary>Session identifier</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Additional processing metadata</summary>
    public Dictionary<string, object> Metadata { get; set; } = [];

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["audio_data"] = AudioData;
        result["text"] = Text;
        result["voice"] = Voice;
        result["language"] = Language;
        result["volume"] = Volume;
        result["duration"] = Duration;
        result["session_id"] = SessionId;
        result["metadata"] = JObject.FromObject(Metadata);
        return result;
    }
}

#endregion

#region Workflow Models

/// <summary>Individual step in a workflow.</summary>
public class WorkflowStep
{
    /// <summary>Step type (stt, tts, llm, custom)</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Whether this step is enabled</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Step execution order</summary>
    public int Order { get; set; } = 0;

    /// <summary>Step-specific configuration</summary>
    public JObject Config { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Type))
        {
            throw new ArgumentException("Step type is required");
        }

        string[] validTypes = ["stt", "tts", "llm", "custom"];
        if (!validTypes.Contains(Type.ToLowerInvariant()))
        {
            throw new ArgumentException($"Step type must be one of: {string.Join(", ", validTypes)}");
        }
    }
}

/// <summary>Request for workflow processing.</summary>
public class WorkflowRequest : BaseRequest
{
    /// <summary>Type of workflow (stt_to_tts, custom, etc.)</summary>
    public string WorkflowType { get; set; } = "custom";

    /// <summary>Type of input data (audio, text)</summary>
    public string InputType { get; set; } = "text";

    /// <summary>Input data (base64 audio or text)</summary>
    public string InputData { get; set; } = string.Empty;

    /// <summary>Workflow steps to execute</summary>
    public List<WorkflowStep> Steps { get; set; } = [];

    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrWhiteSpace(InputData))
        {
            throw new ArgumentException("InputData is required");
        }

        if (Steps.Count == 0)
        {
            throw new ArgumentException("At least one workflow step is required");
        }

        foreach (WorkflowStep step in Steps)
        {
            step.Validate();
        }

        // Validate workflow logic
        if (InputType.Equals("audio", StringComparison.InvariantCultureIgnoreCase) && !Steps.First().Type.Equals("stt", StringComparison.InvariantCultureIgnoreCase))
        {
            throw new ArgumentException("Audio input requires STT as first step");
        }
    }
}

/// <summary>Response from workflow processing.</summary>
public class WorkflowResponse : BaseResponse
{
    /// <summary>Type of workflow that was executed</summary>
    public string WorkflowType { get; set; } = string.Empty;

    /// <summary>Results from each workflow step</summary>
    public Dictionary<string, object> Results { get; set; } = [];

    /// <summary>Steps that were executed</summary>
    public string[] ExecutedSteps { get; set; } = [];

    /// <summary>Total processing time for all steps</summary>
    public double TotalProcessingTime { get; set; } = 0.0;

    /// <summary>Session identifier</summary>
    public string SessionId { get; set; } = string.Empty;

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["workflow_type"] = WorkflowType;
        result["results"] = JObject.FromObject(Results);
        result["executed_steps"] = JArray.FromObject(ExecutedSteps);
        result["total_processing_time"] = TotalProcessingTime;
        result["session_id"] = SessionId;
        return result;
    }
}

#endregion

#region Backend Management Models

/// <summary>Response for backend status and management operations.</summary>
public class BackendStatusResponse : BaseResponse
{
    /// <summary>Backend type (STT or TTS)</summary>
    public string BackendType { get; set; } = string.Empty;

    /// <summary>Current status (starting, running, stopped, error)</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Whether the backend is currently running</summary>
    public bool IsRunning { get; set; } = false;

    /// <summary>Whether the backend is healthy and responding</summary>
    public bool IsHealthy { get; set; } = false;

    /// <summary>Process ID of the backend (if running)</summary>
    public int ProcessId { get; set; } = 0;

    /// <summary>Whether dependencies are installed</summary>
    public bool DependenciesInstalled { get; set; } = false;

    /// <summary>Additional error details</summary>
    public string ErrorDetails { get; set; } = string.Empty;

    /// <summary>Detailed health information</summary>
    public BackendHealthInfo HealthInfo { get; set; } = new();

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["backend_type"] = BackendType;
        result["status"] = Status;
        result["is_running"] = IsRunning;
        result["is_healthy"] = IsHealthy;
        result["process_id"] = ProcessId;
        result["dependencies_installed"] = DependenciesInstalled;

        if (!string.IsNullOrEmpty(ErrorDetails))
            result["error_details"] = ErrorDetails;

        result["health_info"] = HealthInfo.ToJObject();
        return result;
    }
}

/// <summary>Backend health monitoring information.</summary>
public class BackendHealthInfo
{
    /// <summary>Whether the backend process is running</summary>
    public bool IsRunning { get; set; } = false;

    /// <summary>Whether the backend is healthy and responding</summary>
    public bool IsHealthy { get; set; } = false;

    /// <summary>Whether the backend is responding to requests</summary>
    public bool IsResponding { get; set; } = false;

    /// <summary>When the health check was performed</summary>
    public DateTime LastCheck { get; set; } = DateTime.UtcNow;

    /// <summary>Error message if unhealthy</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Backend type being checked</summary>
    public string BackendType { get; set; } = string.Empty;

    /// <summary>Maximum number of health check attempts before giving up.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>Number of health check attempts performed so far.</summary>
    public int AttemptCount { get; set; } = 0;

    /// <summary>Status of individual services</summary>
    public Dictionary<string, bool> Services { get; set; } = [];

    /// <summary>Resets all health info fields to their defaults and returns a fresh instance.</summary>
    public BackendHealthInfo Reset()
    {
        return new BackendHealthInfo
        {
            IsRunning = false,
            IsHealthy = false,
            IsResponding = false,
            LastCheck = DateTime.UtcNow,
            ErrorMessage = string.Empty,
            BackendType = string.Empty,
            MaxAttempts = 0,
            Services = []
        };
    }

    /// <summary>Converts health info to a JObject for API serialization.</summary>
    public JObject ToJObject()
    {
        return new JObject
        {
            ["is_running"] = IsRunning,
            ["is_healthy"] = IsHealthy,
            ["is_responding"] = IsResponding,
            ["last_check"] = LastCheck.ToString("O"),
            ["error_message"] = ErrorMessage,
            ["backend_type"] = BackendType,
            ["services"] = JObject.FromObject(Services)
        };
    }
}

#endregion

#region Installation and Progress Models

/// <summary>Response for installation status operations.</summary>
public class InstallationStatusResponse : BaseResponse
{
    /// <summary>Whether Python environment was detected</summary>
    public bool PythonDetected { get; set; } = false;

    /// <summary>Path to the Python executable</summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>Whether STT dependencies are installed</summary>
    public bool STTDependenciesInstalled { get; set; } = false;

    /// <summary>Whether TTS dependencies are installed</summary>
    public bool TTSDependenciesInstalled { get; set; } = false;

    /// <summary>Whether all dependencies are installed</summary>
    public bool AllDependenciesInstalled { get; set; } = false;

    /// <summary>Detailed installation information</summary>
    public Dictionary<string, object> InstallationDetails { get; set; } = [];

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["python_detected"] = PythonDetected;
        result["python_path"] = PythonPath;
        result["stt_dependencies_installed"] = STTDependenciesInstalled;
        result["tts_dependencies_installed"] = TTSDependenciesInstalled;
        result["all_dependencies_installed"] = AllDependenciesInstalled;
        result["installation_details"] = JObject.FromObject(InstallationDetails);
        return result;
    }
}

/// <summary>Response for installation progress tracking.</summary>
public class InstallationProgressResponse : BaseResponse
{
    /// <summary>Serialized version info for installed packages.</summary>
    public string PackageVersions { get; set; }

    /// <summary>Whether installation is currently running</summary>
    public bool IsInstalling { get; set; } = false;

    /// <summary>Overall progress percentage (0-100)</summary>
    public int Progress { get; set; } = 0;

    /// <summary>Download progress percentage for the current package (0-100).</summary>
    public int DownloadProgress { get; set; } = 0;

    /// <summary>Current installation step</summary>
    public string CurrentStep { get; set; } = string.Empty;

    /// <summary>Current package being installed</summary>
    public string CurrentPackage { get; set; } = string.Empty;

    /// <summary>List of completed packages</summary>
    public string[] CompletedPackages { get; set; } = [];

    /// <summary>Whether installation is complete</summary>
    public bool IsComplete { get; set; } = false;

    /// <summary>Whether an error occurred</summary>
    public bool HasError { get; set; } = false;

    /// <summary>Comma-separated list of packages that failed to install.</summary>
    public string FailedPackages { get; set; }

    /// <summary>Error message if an error occurred</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Human-readable status message for display.</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Resets all installation progress fields to their defaults.</summary>
    public InstallationProgressResponse Reset()
    {
        return new InstallationProgressResponse
        {
            IsInstalling = false,
            Progress = 0,
            CurrentStep = string.Empty,
            CurrentPackage = string.Empty,
            CompletedPackages = [],
            IsComplete = false,
            HasError = false,
            ErrorMessage = string.Empty
        };
    }

    public override JObject ToJObject()
    {
        JObject result = base.ToJObject();
        result["is_installing"] = IsInstalling;
        result["progress"] = Progress;
        result["current_step"] = CurrentStep;
        result["current_package"] = CurrentPackage;
        result["completed_packages"] = JArray.FromObject(CompletedPackages);
        result["is_complete"] = IsComplete;
        result["has_error"] = HasError;
        result["error_message"] = ErrorMessage;
        return result;
    }
}

#endregion

#region Internal Support Models

/// <summary>Python environment information for dependency management.</summary>
public class PythonEnvironmentInfo
{
    /// <summary>Path to the Python executable</summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>Operating system information</summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>Whether this is SwarmUI's embedded Python</summary>
    public bool IsEmbedded { get; set; } = false;

    /// <summary>Python version information</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Whether the Python environment is valid and usable</summary>
    public bool IsValid => !string.IsNullOrEmpty(PythonPath) && File.Exists(PythonPath);
}

/// <summary>Information about a running process.</summary>
public class ProcessInfo
{
    /// <summary>Process ID</summary>
    public int ProcessId { get; set; } = 0;

    /// <summary>Whether the process has exited</summary>
    public bool HasExited { get; set; } = true;

    /// <summary>When the process started</summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>Process name</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Working set memory usage</summary>
    public long WorkingSet { get; set; } = 0;

    /// <summary>Virtual memory usage</summary>
    public long VirtualMemory { get; set; } = 0;

    /// <summary>Backend type this process manages</summary>
    public string BackendType { get; set; } = string.Empty;
}

/// <summary>Package definition for dependency management.</summary>
public class PackageDefinition
{
    /// <summary>Display name of the package</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name to use for pip install</summary>
    public string InstallName { get; set; } = string.Empty;

    /// <summary>Name to use for import checks</summary>
    public string ImportName { get; set; } = string.Empty;

    /// <summary>Package category (core, pytorch, stt, tts)</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Whether this is a git package</summary>
    public bool IsGitPackage { get; set; } = false;

    /// <summary>Alternative names to check for</summary>
    public string[] AlternativeNames { get; set; } = [];

    /// <summary>Estimated installation time in minutes</summary>
    public int EstimatedInstallTimeMinutes { get; set; } = 2;

    /// <summary>Custom pip install arguments</summary>
    public string CustomInstallArgs { get; set; } = string.Empty;
}

/// <summary>Package installation status.</summary>
public class PackageStatus
{
    /// <summary>Package name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the package is installed</summary>
    public bool IsInstalled { get; set; } = false;

    /// <summary>Detected version if installed</summary>
    public string DetectedVersion { get; set; } = string.Empty;

    /// <summary>Package category</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Error message if installation failed</summary>
    public string Error { get; set; } = string.Empty;
}

#endregion

#region Job Tracking Models

/// <summary>Response for long-running voice processing job progress.</summary>
public class JobProgressResponse : BaseResponse
{
    /// <summary>Unique identifier for the processing job.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Type of job (tts_large, stt_batch, workflow).</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Overall progress percentage (0-100).</summary>
    public int Progress { get; set; } = 0;

    /// <summary>Description of the current processing step.</summary>
    public string CurrentStep { get; set; } = string.Empty;

    /// <summary>Index of the chunk currently being processed.</summary>
    public int CurrentChunk { get; set; } = 0;

    /// <summary>Total number of chunks to process.</summary>
    public int TotalChunks { get; set; } = 0;

    /// <summary>Bytes or characters processed so far.</summary>
    public long ProcessedSize { get; set; } = 0;

    /// <summary>Total bytes or characters to process.</summary>
    public long TotalSize { get; set; } = 0;

    /// <summary>Estimated time until the job completes.</summary>
    public TimeSpan EstimatedTimeRemaining { get; set; } = TimeSpan.Zero;

    /// <summary>Whether partial results are available for completed chunks.</summary>
    public bool HasPartialResults { get; set; } = false;

    /// <summary>Results from completed chunks, keyed by chunk identifier.</summary>
    public Dictionary<string, object> PartialResults { get; set; } = [];
}

/// <summary>Internal job state tracking data.</summary>
public class JobData
{
    /// <summary>Unique identifier for the job.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Type of job being tracked.</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Operation being performed (TTS, STT, Workflow).</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Index of the chunk currently being processed.</summary>
    public int CurrentChunk { get; set; } = 0;

    /// <summary>Total number of chunks to process.</summary>
    public int TotalChunks { get; set; } = 0;

    /// <summary>Results from successfully completed chunks.</summary>
    public Dictionary<string, object> CompletedChunks { get; set; } = [];

    /// <summary>Identifiers of chunks that failed processing.</summary>
    public List<string> FailedChunks { get; set; } = [];

    /// <summary>Resets all job tracking fields to their defaults.</summary>
    public void Reset()
    {
        CurrentChunk = 0;
        TotalChunks = 0;
        CompletedChunks.Clear();
        FailedChunks.Clear();
    }
}

#endregion
