using Hartsy.Extensions.VoiceAssistant;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.SwarmBackends;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

/// <summary>Permission definitions for the Voice Assistant API endpoints.</summary>
public static class VoiceAssistantPermissions
{
    public static readonly PermInfoGroup VoiceAssistantPermGroup = new("VoiceAssistant", "Permissions related to Voice Assistant functionality for API calls and voice processing.");
    public static readonly PermInfo PermProcessAudio = Permissions.Register(new("voice_process_audio", "Process Audio", "Allows processing of audio through STT, TTS, and workflows.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermManageBackends = Permissions.Register(new("voice_manage_backends", "Manage Voice Backends", "Allows starting and stopping voice processing backends.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("voice_check_status", "Check Voice Status", "Allows checking the status and health of voice services.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
}

/// <summary>Simplified Voice Assistant API endpoints for SwarmBackends integration. Focuses on processing requests and managing split STT/TTS backends.</summary>
[API.APIClass("Voice Assistant API for SwarmBackends integration with split STT/TTS services")]
public static class VoiceAssistantAPI
{
    /// <summary>Registers all API endpoints with appropriate permissions.</summary>
    public static void Register()
    {
        try
        {
            // Core processing endpoints
            API.RegisterAPICall(ProcessSTT, false, VoiceAssistantPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessTTS, false, VoiceAssistantPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessWorkflow, false, VoiceAssistantPermissions.PermProcessAudio);

            // Backend management endpoints
            API.RegisterAPICall(StartSTTBackend, false, VoiceAssistantPermissions.PermManageBackends);
            API.RegisterAPICall(StopSTTBackend, false, VoiceAssistantPermissions.PermManageBackends);
            API.RegisterAPICall(StartTTSBackend, false, VoiceAssistantPermissions.PermManageBackends);
            API.RegisterAPICall(StopTTSBackend, false, VoiceAssistantPermissions.PermManageBackends);

            // Status and monitoring endpoints
            API.RegisterAPICall(GetSTTBackendStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetTTSBackendStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, VoiceAssistantPermissions.PermCheckStatus);

            // Register STT and TTS backends with SwarmUI
            Program.Backends.RegisterBackendType<TTSBackend>("tts-backend", "TTS Backend",
                "TTS backend for voice processing", true);
            Program.Backends.RegisterBackendType<STTBackend>("stt-backend", "STT Backend",
                "STT backend for voice processing", true);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    #region Core Processing Endpoints

    /// <summary>Process Speech-to-Text request from Generate page.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">STT request data</param>
    /// <returns>STT processing response</returns>
    public static async Task<JObject> ProcessSTT(Session session, JObject input)
    {
        try
        {
            STTRequest request = ParseSTTRequest(input, session.ID);

            // Convert request to proper format for backend processing
            byte[] audioData = Convert.FromBase64String(request.AudioData);
            STTResponse result = await STTBackend.ProcessSTTAsync(audioData, request.Language, request.Options);

            return result.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return VoiceAssistant.CreateErrorResponse("Invalid STT request parameters", "invalid_request", ex);
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("STT processing failed", "processing_error", ex);
        }
    }

    /// <summary>Process Text-to-Speech request from Generate page.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">TTS request data</param>
    /// <returns>TTS processing response</returns>
    public static async Task<JObject> ProcessTTS(Session session, JObject input)
    {
        try
        {
            TTSRequest request = ParseTTSRequest(input, session.ID);

            // Process TTS through backend client
            JObject backendResponse = await PythonVoiceProcessor.Instance.ProcessTTSAsync(request);

            TTSResponse response = new()
            {
                Success = backendResponse["success"]?.Value<bool>() ?? false,
                AudioData = backendResponse["audio_data"]?.ToString() ?? string.Empty,
                Text = backendResponse["text"]?.ToString() ?? request.Text,
                Voice = backendResponse["voice"]?.ToString() ?? request.Voice,
                Language = backendResponse["language"]?.ToString() ?? request.Language,
                Volume = backendResponse["volume"]?.Value<float>() ?? request.Volume,
                Duration = backendResponse["duration"]?.Value<double>() ?? 0.0,
                ProcessingTime = backendResponse["processing_time"]?.Value<double>() ?? 0.0,
                SessionId = session.ID,
                Metadata = backendResponse["metadata"]?.ToObject<Dictionary<string, object>>() ?? []
            };

            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return VoiceAssistant.CreateErrorResponse("Invalid TTS request parameters", "invalid_request", ex);
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("TTS processing failed", "processing_error", ex);
        }
    }

    /// <summary>Process modular workflow request for complex voice operations.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Workflow request data</param>
    /// <returns>Workflow processing response</returns>
    public static async Task<JObject> ProcessWorkflow(Session session, JObject input)
    {
        try
        {
            WorkflowRequest request = ParseWorkflowRequest(input, session.ID);

            // Determine which backend to start the workflow on
            ServiceConfiguration.BackendType primaryBackend = DetermineWorkflowPrimaryBackend(request);

            // Convert to workflow request for backend client
            WorkflowRequest backendRequest = new()
            {
                InputType = request.InputType,
                InputData = request.InputData,
                SessionId = request.SessionId,
                Steps = [.. request.Steps.Select(s => new WorkflowStep
                {
                    Type = s.Type,
                    Enabled = s.Enabled,
                    Config = s.Config
                })]
            };

            // Process workflow through backend client
            JObject backendResponse = await PythonVoiceProcessor.Instance.CallWorkflowServiceAsync(backendRequest, primaryBackend);

            WorkflowResponse response = new()
            {
                Success = backendResponse["success"]?.Value<bool>() ?? false,
                WorkflowType = request.WorkflowType,
                Results = backendResponse["pipeline_results"]?.ToObject<Dictionary<string, object>>() ?? [],
                ExecutedSteps = backendResponse["executed_steps"]?.ToObject<string[]>() ?? [],
                TotalProcessingTime = backendResponse["total_processing_time"]?.Value<double>() ?? 0.0,
                SessionId = session.ID,
                Message = backendResponse["message"]?.ToString() ?? (backendResponse["success"]?.Value<bool>() == true ? "Workflow completed successfully" : "Workflow failed")
            };

            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return VoiceAssistant.CreateErrorResponse("Invalid workflow request parameters", "invalid_request", ex);
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Workflow processing failed", "processing_error", ex);
        }
    }

    #endregion

    #region Backend Management Endpoints

    /// <summary>Start the STT backend service.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Start request data</param>
    /// <returns>Backend start response</returns>
    public static async Task<JObject> StartSTTBackend(Session session, JObject input)
    {
        try
        {
            bool forceRestart = input["force_restart"]?.Value<bool>() ?? false;
            BackendStatusResponse response = await STTBackend.Instance.StartAsync(forceRestart);
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to start STT backend", "backend_error", ex);
        }
    }

    /// <summary>Stop the STT backend service.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Stop request data</param>
    /// <returns>Backend stop response</returns>
    public static async Task<JObject> StopSTTBackend(Session session, JObject input)
    {
        try
        {
            BackendStatusResponse response = await STTBackend.Instance.StopAsync();
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to stop STT backend", "backend_error", ex);
        }
    }

    /// <summary>Start the TTS backend service.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Start request data</param>
    /// <returns>Backend start response</returns>
    public static async Task<JObject> StartTTSBackend(Session session, JObject input)
    {
        try
        {
            bool forceRestart = input["force_restart"]?.Value<bool>() ?? false;
            BackendStatusResponse response = await TTSBackend.Instance.StartAsync(forceRestart);
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to start TTS backend", "backend_error", ex);
        }
    }

    /// <summary>Stop the TTS backend service.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Stop request data</param>
    /// <returns>Backend stop response</returns>
    public static async Task<JObject> StopTTSBackend(Session session, JObject input)
    {
        try
        {
            BackendStatusResponse response = await TTSBackend.Instance.StopAsync();
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to stop TTS backend", "backend_error", ex);
        }
    }

    #endregion

    #region Status and Monitoring Endpoints

    /// <summary>Get STT backend status and health.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Status request data</param>
    /// <returns>STT backend status response</returns>
    public static async Task<JObject> GetSTTBackendStatus(Session session, JObject input)
    {
        try
        {
            BackendStatusResponse response = await STTBackend.Instance.GetStatusAsync();
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to get STT backend status", "status_error", ex);
        }
    }

    /// <summary>Get TTS backend status and health.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Status request data</param>
    /// <returns>TTS backend status response</returns>
    public static async Task<JObject> GetTTSBackendStatus(Session session, JObject input)
    {
        try
        {
            BackendStatusResponse response = await TTSBackend.Instance.GetStatusAsync();
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to get TTS backend status", "status_error", ex);
        }
    }

    /// <summary>Get installation status for voice dependencies.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Installation check request data</param>
    /// <returns>Installation status response</returns>
    public static async Task<JObject> GetInstallationStatus(Session session, JObject input)
    {
        try
        {
            DependencyInstaller installer = new();
            PythonEnvironmentInfo pythonInfo = installer.DetectPythonEnvironment();

            if (pythonInfo?.IsValid != true)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "Python environment not detected",
                    ["python_detected"] = false,
                    ["stt_dependencies"] = false,
                    ["tts_dependencies"] = false
                };
            }

            bool sttDependencies = await installer.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.STT);
            bool ttsDependencies = await installer.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.TTS);

            return new JObject
            {
                ["success"] = true,
                ["message"] = "Installation status retrieved successfully",
                ["python_detected"] = true,
                ["python_path"] = pythonInfo.PythonPath,
                ["stt_dependencies"] = sttDependencies,
                ["tts_dependencies"] = ttsDependencies,
                ["all_dependencies"] = sttDependencies && ttsDependencies
            };
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to check installation status", "status_error", ex);
        }
    }

    /// <summary>Get real-time installation progress.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Progress request data</param>
    /// <returns>Installation progress response</returns>
    public static async Task<JObject> GetInstallationProgress(Session session, JObject input)
    {
        try
        {
            DependencyInstaller installer = new();
            ProgressTracker tracker = ProgressTracking.Installation;

            return new JObject
            {
                ["success"] = true,
                ["is_installing"] = installer.IsInstalling,
                ["progress"] = tracker.Progress,
                ["current_step"] = tracker.CurrentStep,
                ["current_package"] = tracker.CurrentPackage,
                ["completed_packages"] = new JArray(tracker.CompletedPackages),
                ["is_complete"] = tracker.IsComplete,
                ["has_error"] = tracker.HasError,
                ["error_message"] = tracker.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            return VoiceAssistant.CreateErrorResponse("Failed to get installation progress", "status_error", ex);
        }
    }

    #endregion

    #region Request Parsing Methods

    /// <summary>Parse and validate STT request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated STT request</returns>
    public static STTRequest ParseSTTRequest(JObject input, string sessionId)
    {
        STTRequest request = new()
        {
            SessionId = sessionId,
            AudioData = input["audio_data"]?.ToString() ?? string.Empty,
            Language = input["language"]?.ToString() ?? ServiceConfiguration.DefaultLanguage,
            Options = new STTOptions()
        };
        // Parse options if provided
        if (input["options"] is JObject optionsObj)
        {
            request.Options.ReturnConfidence = optionsObj["return_confidence"]?.Value<bool>() ?? true;
            request.Options.ReturnAlternatives = optionsObj["return_alternatives"]?.Value<bool>() ?? false;
            request.Options.ModelPreference = optionsObj["model_preference"]?.ToString() ?? "accuracy";
            // Parse custom options
            if (optionsObj["custom"] is JObject customObj)
            {
                foreach (JProperty prop in customObj.Properties())
                {
                    request.Options.CustomOptions[prop.Name] = prop.Value?.ToObject<object>();
                }
            }
        }
        request.Validate();
        return request;
    }

    /// <summary>Parse and validate TTS request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated TTS request</returns>
    public static TTSRequest ParseTTSRequest(JObject input, string sessionId)
    {
        TTSRequest request = new()
        {
            SessionId = sessionId,
            Text = input["text"]?.ToString() ?? string.Empty,
            Voice = input["voice"]?.ToString() ?? ServiceConfiguration.DefaultVoice,
            Language = input["language"]?.ToString() ?? ServiceConfiguration.DefaultLanguage,
            Volume = input["volume"]?.Value<float>() ?? ServiceConfiguration.DefaultVolume,
            Options = new Models.TTSOptions()
        };
        // Parse options if provided
        if (input["options"] is JObject optionsObj)
        {
            request.Options.Speed = optionsObj["speed"]?.Value<float>() ?? 1.0f;
            request.Options.Pitch = optionsObj["pitch"]?.Value<float>() ?? 1.0f;
            request.Options.Format = optionsObj["format"]?.ToString() ?? "wav";
            // Parse custom options
            if (optionsObj["custom"] is JObject customObj)
            {
                foreach (JProperty prop in customObj.Properties())
                {
                    request.Options.CustomOptions[prop.Name] = prop.Value?.ToObject<object>();
                }
            }
        }
        request.Validate();
        return request;
    }

    /// <summary>Parse and validate workflow request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated workflow request</returns>
    public static WorkflowRequest ParseWorkflowRequest(JObject input, string sessionId)
    {
        WorkflowRequest request = new()
        {
            SessionId = sessionId,
            WorkflowType = input["workflow_type"]?.ToString() ?? "custom",
            InputData = input["input_data"]?.ToString() ?? string.Empty,
            InputType = input["input_type"]?.ToString() ?? "text",
            Steps = []
        };
        // Parse workflow steps
        if (input["steps"] is JArray stepsArray)
        {
            foreach (JToken stepToken in stepsArray)
            {
                if (stepToken is JObject stepObj)
                {
                    WorkflowStep step = new()
                    {
                        Type = stepObj["type"]?.ToString() ?? "unknown",
                        Enabled = stepObj["enabled"]?.Value<bool>() ?? true,
                        Order = stepObj["order"]?.Value<int>() ?? 0,
                        Config = stepObj["config"] as JObject ?? []
                    };
                    step.Validate();
                    request.Steps.Add(step);
                }
            }
        }
        // Sort steps by order
        request.Steps = [.. request.Steps.OrderBy(s => s.Order)];
        request.Validate();
        return request;
    }

    #endregion

    #region Utility Methods

    /// <summary>Determine which backend should be the primary for a workflow.</summary>
    /// <param name="request">Workflow request</param>
    /// <returns>Primary backend type</returns>
    public static ServiceConfiguration.BackendType DetermineWorkflowPrimaryBackend(WorkflowRequest request)
    {
        // If input is audio, start with STT
        if (request.InputType.Equals("audio", StringComparison.InvariantCultureIgnoreCase))
        {
            return ServiceConfiguration.BackendType.STT;
        }
        // If workflow has STT steps, start with STT
        if (request.Steps.Any(s => s.Type.Equals("stt", StringComparison.InvariantCultureIgnoreCase)))
        {
            return ServiceConfiguration.BackendType.STT;
        }
        // Otherwise start with TTS
        return ServiceConfiguration.BackendType.TTS;
    }

    /// <summary>Get API endpoint information for diagnostics.</summary>
    /// <returns>API endpoint information</returns>
    public static JObject GetAPIInfo()
    {
        return new JObject
        {
            ["version"] = "0.0.1",
            ["architecture"] = "split_backends",
            ["supported_endpoints"] = new JArray
            {
                "ProcessSTT",
                "ProcessTTS",
                "ProcessWorkflow",
                "StartSTTBackend",
                "StopSTTBackend",
                "StartTTSBackend",
                "StopTTSBackend",
                "GetSTTBackendStatus",
                "GetTTSBackendStatus",
                "GetInstallationStatus",
                "GetInstallationProgress"
            },
            ["backend_types"] = new JArray { "STT", "TTS" },
            ["workflow_step_types"] = new JArray { "stt", "tts", "llm", "custom" },
            ["supported_languages"] = new JArray(ServiceConfiguration.SupportedLanguages),
            ["supported_voices"] = new JArray(ServiceConfiguration.AvailableVoices)
        };
    }

    /// <summary>Validate a workflow step configuration.</summary>
    /// <param name="step">Workflow step to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool ValidateWorkflowStep(WorkflowStep step)
    {
        try
        {
            step.Validate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
