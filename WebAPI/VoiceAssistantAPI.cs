using SwarmUI.Core;
using SwarmUI.WebAPI;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Services;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

/// <summary>Permission definitions for the Voice Assistant API endpoints.
/// Ensures proper access control for voice processing operations.</summary>
public static class VoiceAssistantPermissions
{
    public static readonly PermInfoGroup VoiceAssistantPermGroup = new("VoiceAssistant", "Permissions related to Voice Assistant functionality for API calls and voice processing.");
    public static readonly PermInfo PermProcessAudio = Permissions.Register(new("voice_process_audio", "Process Audio", "Allows processing of audio through STT, TTS, and pipelines.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermManageService = Permissions.Register(new("voice_manage_service", "Manage Voice Service", "Allows starting and stopping the voice processing backend.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("voice_check_status", "Check Voice Status", "Allows checking the status and health of voice services.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermRecordAudio = Permissions.Register(new("voice_record_audio", "Record Audio", "Allows server-side audio recording for voice processing.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
}

/// <summary>Modern, generic, and reusable API endpoints for Voice Assistant.
/// Clean separation of concerns with composable services.</summary>
[API.APIClass("Modern Voice Assistant API with generic, reusable endpoints")]
public static class VoiceAssistantAPI
{
    /// <summary>Registers all API endpoints with appropriate permissions.</summary>
    public static void Register()
    {
        try
        {
            // Core processing endpoints - pure and stateless
            API.RegisterAPICall(ProcessSTT, false, VoiceAssistantPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessTTS, false, VoiceAssistantPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessPipeline, false, VoiceAssistantPermissions.PermProcessAudio);
            
            // Server-side recording endpoints
            API.RegisterAPICall(StartServerRecording, false, VoiceAssistantPermissions.PermRecordAudio);
            API.RegisterAPICall(StopServerRecording, false, VoiceAssistantPermissions.PermRecordAudio);
            API.RegisterAPICall(GetRecordingStatus, false, VoiceAssistantPermissions.PermRecordAudio);
            
            // Service management endpoints
            API.RegisterAPICall(StartVoiceService, false, VoiceAssistantPermissions.PermManageService);
            API.RegisterAPICall(StopVoiceService, false, VoiceAssistantPermissions.PermManageService);
            
            // Status and monitoring endpoints
            API.RegisterAPICall(GetVoiceStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(CheckInstallationStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, VoiceAssistantPermissions.PermCheckStatus);

            Logs.Info("[VoiceAssistant] Generic API endpoints registered successfully with server-side recording");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    #region Server-Side Recording Endpoints

    /// <summary>Start server-side audio recording.
    /// This bypasses browser security restrictions by recording on the server.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Recording request data</param>
    /// <returns>Recording start response</returns>
    public static async Task<JObject> StartServerRecording(Session session, JObject input)
    {
        const string operation = "StartServerRecording";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Parse recording options
            int duration = input["duration"]?.Value<int>() ?? 10; // Default 10 seconds
            string language = input["language"]?.ToString() ?? "en-US";
            string mode = input["mode"]?.ToString() ?? "stt"; // stt, sts, or raw
            
            // Validate duration (1-30 seconds)
            duration = Math.Max(1, Math.Min(30, duration));
            
            // Start recording through service layer
            RecordingRequest request = new RecordingRequest
            {
                SessionId = session.ID,
                Duration = duration,
                Language = language,
                Mode = mode,
                Options = input["options"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
            };
            
            RecordingResponse response = await PythonBackendService.Instance.StartServerRecordingAsync(request);
            
            Logs.Info($"[VoiceAssistant] Server recording started: {(response.Success ? "Success" : "Failed")}");
            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid recording request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Recording service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>Stop server-side audio recording.
    /// Stops ongoing recording and processes the audio.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Stop recording request data</param>
    /// <returns>Recording stop response with processed audio</returns>
    public static async Task<JObject> StopServerRecording(Session session, JObject input)
    {
        const string operation = "StopServerRecording";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Stop recording and get results
            RecordingResponse response = await PythonBackendService.Instance.StopServerRecordingAsync(session.ID);
            
            Logs.Info($"[VoiceAssistant] Server recording stopped: {(response.Success ? "Success" : "Failed")}");
            return response.ToJObject();
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "No active recording to stop");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>Get current recording status.
    /// Returns information about ongoing recording session.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Status request data</param>
    /// <returns>Recording status response</returns>
    public static async Task<JObject> GetRecordingStatus(Session session, JObject input)
    {
        const string operation = "GetRecordingStatus";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Get recording status
            RecordingStatusResponse response = await PythonBackendService.Instance.GetRecordingStatusAsync(session.ID);
            
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    #endregion

    #region Core Processing Endpoints

    /// <summary>Pure Speech-to-Text processing endpoint.
    /// Converts audio data to text with optional configuration.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">STT request data</param>
    /// <returns>STT processing response</returns>
    public static async Task<JObject> ProcessSTT(Session session, JObject input)
    {
        const string operation = "ProcessSTT";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Parse and validate request
            STTRequest request = ParseSTTRequest(input, session.ID);
            
            // Process through service layer
            STTResponse response = await PythonBackendService.Instance.ProcessSTTAsync(request);
            
            Logs.Info($"[VoiceAssistant] STT processing completed: '{response.Transcription?.Substring(0, Math.Min(50, response.Transcription?.Length ?? 0))}...'");
            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid STT request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "STT service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>Pure Text-to-Speech processing endpoint.
    /// Converts text to audio data with optional voice configuration.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">TTS request data</param>
    /// <returns>TTS processing response</returns>
    public static async Task<JObject> ProcessTTS(Session session, JObject input)
    {
        const string operation = "ProcessTTS";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Parse and validate request
            TTSRequest request = ParseTTSRequest(input, session.ID);
            
            // Process through service layer
            TTSResponse response = await PythonBackendService.Instance.ProcessTTSAsync(request);
            
            Logs.Info($"[VoiceAssistant] TTS processing completed for text: '{request.Text.Substring(0, Math.Min(50, request.Text.Length))}...'");
            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid TTS request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "TTS service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>Configurable pipeline processing endpoint.
    /// Orchestrates multiple processing steps (STT, Commands, TTS) based on configuration.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Pipeline request data</param>
    /// <returns>Pipeline processing response</returns>
    public static async Task<JObject> ProcessPipeline(Session session, JObject input)
    {
        const string operation = "ProcessPipeline";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Parse and validate request
            PipelineRequest request = ParsePipelineRequest(input, session.ID);
            
            // Process through service layer
            PipelineResponse response = await PythonBackendService.Instance.ProcessPipelineAsync(request);
            
            Logs.Info($"[VoiceAssistant] Pipeline processing completed with {response.ExecutedSteps.Count} steps");
            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid pipeline request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Pipeline service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    #endregion

    #region Service Management Endpoints

    // Implementation removed, see complete version below


    #endregion

    #region Service Management Endpoints
    
    /// <summary>Starts the voice service and initializes the Python backend.
    /// This will also install any missing dependencies if needed.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Service start request data</param>
    /// <returns>Service start response</returns>
    public static async Task<JObject> StartVoiceService(Session session, JObject input)
    {
        const string operation = "StartVoiceService";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Don't start if already running or installing
            if (PythonBackendService.Instance.IsBackendRunning)
            {
                Logs.Debug("[VoiceAssistant] Backend already running, skipping startup");
                return new ServiceStatusResponse
                {
                    Success = true,
                    BackendRunning = true,
                    BackendHealthy = true,
                    Message = "Backend already running",
                    BackendUrl = Configuration.ServiceConfiguration.BackendUrl
                }.ToJObject();
            }
            
            if (PythonBackendService.Instance.IsInstalling)
            {
                Logs.Debug("[VoiceAssistant] Installation already in progress");
                return new ServiceStatusResponse
                {
                    Success = true,
                    BackendRunning = false,
                    BackendHealthy = false,
                    Message = "Installation already in progress",
                    BackendUrl = Configuration.ServiceConfiguration.BackendUrl
                }.ToJObject();
            }
            
            // Start the backend service and handle dependency installation
            ServiceStatusResponse response = await PythonBackendService.Instance.StartAsync();
            bool startResult = response.Success;
            
            // Return appropriate response
            if (startResult)
            {
                Logs.Info("[VoiceAssistant] Voice service started successfully");
                return new ServiceStatusResponse
                {
                    Success = true,
                    BackendRunning = true,
                    BackendHealthy = true,
                    Message = "Voice service started successfully",
                    BackendUrl = Configuration.ServiceConfiguration.BackendUrl
                }.ToJObject();
            }
            else
            {
                // StartBackendAsync returns false when installation is needed/started
                Logs.Info("[VoiceAssistant] Voice service installation initiated");
                return new ServiceStatusResponse
                {
                    Success = true,
                    BackendRunning = false,
                    BackendHealthy = false,
                    Message = "Installation in progress",
                    BackendUrl = Configuration.ServiceConfiguration.BackendUrl
                }.ToJObject();
            }
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to start voice service");
        }
    }
    
    /// <summary>Stops the voice service and shuts down the Python backend.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Service stop request data</param>
    /// <returns>Service stop response</returns>
    public static async Task<JObject> StopVoiceService(Session session, JObject input)
    {
        const string operation = "StopVoiceService";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Stop the backend service
            ServiceStatusResponse stopResponse = await PythonBackendService.Instance.StopAsync();
            bool stopResult = stopResponse.Success;
            
            return new ServiceStatusResponse
            {
                Success = stopResult,
                BackendRunning = !stopResult,
                BackendHealthy = false,
                Message = stopResult ? "Voice service stopped successfully" : "Failed to stop voice service",
                BackendUrl = Configuration.ServiceConfiguration.BackendUrl
            }.ToJObject();
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to stop voice service");
        }
    }
    
    #endregion
    
    #region Status and Monitoring Endpoints

    /// <summary>Gets the current status and health of the voice service backend.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Status request data</param>
    /// <returns>Service status response</returns>
    public static async Task<JObject> GetVoiceStatus(Session session, JObject input)
    {
        const string operation = "GetVoiceStatus";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Check if installation is in progress
            if (PythonBackendService.Instance.IsInstalling)
            {
                Logs.Debug("[VoiceAssistant] Skipping health check during installation");
                ServiceStatusResponse installResponse = new ServiceStatusResponse
                {
                    Success = true,
                    BackendRunning = false,
                    BackendHealthy = false,
                    BackendUrl = Configuration.ServiceConfiguration.BackendUrl,
                    ProcessId = 0,
                    HasExited = false,
                    Message = "Installation in progress, status check deferred"
                };
                return installResponse.ToJObject();
            }
            
            // Perform backend health check
            Logs.Debug("[VoiceAssistant] Performing backend health check");
            BackendHealthInfo healthInfo = await PythonBackendService.Instance.GetHealthAsync();
            
            ServiceStatusResponse response = new ServiceStatusResponse
            {
                Success = true,
                BackendRunning = healthInfo.IsRunning,
                BackendHealthy = healthInfo.IsHealthy,
                BackendUrl = Configuration.ServiceConfiguration.BackendUrl,
                ProcessId = healthInfo.ProcessId,
                HasExited = healthInfo.HasExited,
                Message = healthInfo.IsHealthy ? "Service is healthy" : healthInfo.ErrorMessage,
                Services = healthInfo.Services
            };
            
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to get service status");
        }
    }

    /// <summary>Checks the installation status of required Python dependencies.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Installation check request data</param>
    /// <returns>Installation status response</returns>
    public static async Task<JObject> CheckInstallationStatus(Session session, JObject input)
    {
        const string operation = "CheckInstallationStatus";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Get installation status through service layer
            InstallationStatusResponse response = await PythonBackendService.Instance.GetInstallationStatusAsync();
            
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to check installation status");
        }
    }

    /// <summary>Gets real-time installation progress for dependency installation.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Progress request data</param>
    /// <returns>Installation progress response</returns>
    public static async Task<JObject> GetInstallationProgress(Session session, JObject input)
    {
        const string operation = "GetInstallationProgress";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            
            // Get progress through service layer
            InstallationProgressResponse response = PythonBackendService.Instance.GetInstallationProgress();
            
            return response.ToJObject();
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to get installation progress");
        }
    }

    #endregion

    #region Request Parsing Methods

    /// <summary>Parses and validates STT request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated request</returns>
    private static STTRequest ParseSTTRequest(JObject input, string sessionId)
    {
        try
        {
            STTRequest request = new STTRequest
            {
                SessionId = sessionId,
                AudioData = input["audio_data"]?.ToString() ?? string.Empty,
                Language = input["language"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultLanguage,
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
                    foreach (Newtonsoft.Json.Linq.JProperty prop in customObj.Properties())
                    {
                        request.Options.CustomOptions[prop.Name] = prop.Value?.ToObject<object>();
                    }
                }
            }

            // Validate the request
            request.Validate();
            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid STT request: {ex.Message}", ex);
        }
    }

    /// <summary>Parses and validates TTS request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated request</returns>
    private static TTSRequest ParseTTSRequest(JObject input, string sessionId)
    {
        try
        {
            TTSRequest request = new TTSRequest
            {
                SessionId = sessionId,
                Text = input["text"]?.ToString() ?? string.Empty,
                Voice = input["voice"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultVoice,
                Language = input["language"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultLanguage,
                Volume = input["volume"]?.Value<float>() ?? Configuration.ServiceConfiguration.DefaultVolume,
                Options = new TTSOptions()
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
                    foreach (Newtonsoft.Json.Linq.JProperty prop in customObj.Properties())
                    {
                        request.Options.CustomOptions[prop.Name] = prop.Value?.ToObject<object>();
                    }
                }
            }

            // Validate the request
            request.Validate();
            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid TTS request: {ex.Message}", ex);
        }
    }

    /// <summary>Parses and validates pipeline request data.</summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated request</returns>
    private static PipelineRequest ParsePipelineRequest(JObject input, string sessionId)
    {
        try
        {
            PipelineRequest request = new PipelineRequest
            {
                SessionId = sessionId,
                InputType = input["input_type"]?.ToString() ?? "audio",
                InputData = input["input_data"]?.ToString() ?? string.Empty,
                PipelineSteps = new List<PipelineStep>()
            };

            // Parse pipeline steps
            if (input["pipeline_steps"] is JArray stepsArray)
            {
                foreach (JToken stepToken in stepsArray)
                {
                    if (stepToken is JObject stepObj)
                    {
                        PipelineStep step = new PipelineStep
                        {
                            Type = stepObj["type"]?.ToString() ?? "unknown",
                            Enabled = stepObj["enabled"]?.Value<bool>() ?? true,
                            Config = stepObj["config"] as JObject ?? new JObject()
                        };
                        
                        // Validate the step
                        step.Validate();
                        request.PipelineSteps.Add(step);
                    }
                }
            }

            // Validate the entire request
            request.Validate();
            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid pipeline request: {ex.Message}", ex);
        }
    }

    #endregion

    #region Utility Methods

    /// <summary>Gets API endpoint information for diagnostics.</summary>
    /// <returns>API endpoint information</returns>
    public static JObject GetAPIInfo()
    {
        return new JObject
        {
            ["version"] = "2.0.0",
            ["endpoint_version"] = "generic",
            ["supported_endpoints"] = new JArray
            {
                "ProcessSTT",
                "ProcessTTS", 
                "ProcessPipeline",
                "StartServerRecording",
                "StopServerRecording",
                "GetRecordingStatus",
                "GetVoiceStatus",
                "StartVoiceService",
                "StopVoiceService",
                "CheckInstallationStatus",
                "GetInstallationProgress"
            },
            ["endpoint_types"] = new JObject
            {
                ["processing"] = new JArray { "ProcessSTT", "ProcessTTS", "ProcessPipeline" },
                ["recording"] = new JArray { "StartServerRecording", "StopServerRecording", "GetRecordingStatus" },
                ["service_management"] = new JArray { "StartVoiceService", "StopVoiceService" },
                ["monitoring"] = new JArray { "GetVoiceStatus", "CheckInstallationStatus", "GetInstallationProgress" }
            },
            ["features"] = new JObject
            {
                ["server_side_recording"] = true,
                ["bypasses_browser_restrictions"] = true,
                ["microphone_access"] = "server_side"
            },
            ["pipeline_step_types"] = new JArray { "stt", "tts", "command_processing" },
            ["supported_languages"] = new JArray(Configuration.ServiceConfiguration.SupportedLanguages),
            ["supported_voices"] = new JArray(Configuration.ServiceConfiguration.AvailableVoices)
        };
    }

    /// <summary>Validates a pipeline step configuration.</summary>
    /// <param name="step">Pipeline step to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool ValidatePipelineStep(PipelineStep step)
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
