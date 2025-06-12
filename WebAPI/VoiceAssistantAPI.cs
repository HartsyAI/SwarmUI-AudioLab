using SwarmUI.Core;
using SwarmUI.WebAPI;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Services;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

/// <summary>
/// Permission definitions for the Voice Assistant API endpoints.
/// Ensures proper access control for voice processing operations.
/// </summary>
public static class VoiceAssistantPermissions
{
    public static readonly PermInfoGroup VoiceAssistantPermGroup = new("VoiceAssistant", "Permissions related to Voice Assistant functionality for API calls and voice processing.");
    public static readonly PermInfo PermProcessAudio = Permissions.Register(new("voice_process_audio", "Process Audio", "Allows processing of audio through STT, TTS, and pipelines.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermManageService = Permissions.Register(new("voice_manage_service", "Manage Voice Service", "Allows starting and stopping the voice processing backend.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("voice_check_status", "Check Voice Status", "Allows checking the status and health of voice services.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));
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
            // Service management endpoints
            API.RegisterAPICall(StartVoiceService, false, VoiceAssistantPermissions.PermManageService);
            API.RegisterAPICall(StopVoiceService, false, VoiceAssistantPermissions.PermManageService);
            // Status and monitoring endpoints
            API.RegisterAPICall(GetVoiceStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(CheckInstallationStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, VoiceAssistantPermissions.PermCheckStatus);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

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
            var request = ParseSTTRequest(input, session.ID);
            // Process through service layer
            var response = await PythonBackendService.Instance.ProcessSTTAsync(request);
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
            var request = ParseTTSRequest(input, session.ID);
            // Process through service layer
            var response = await PythonBackendService.Instance.ProcessTTSAsync(request);
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
            var request = ParsePipelineRequest(input, session.ID);
            // Process through service layer
            var response = await PythonBackendService.Instance.ProcessPipelineAsync(request);
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
    /// <summary>Starts the voice service backend with automatic dependency installation.</summary>
    /// <param name="session">User session</param>
    /// <param name="input">Start service request data</param>
    /// <returns>Service start response</returns>
    public static async Task<JObject> StartVoiceService(Session session, JObject input)
    {
        const string operation = "StartVoiceService";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            // Start service through service layer
            var response = await PythonBackendService.Instance.StartAsync();
            return response.ToJObject();
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Failed to start voice service");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>
    /// Stops the voice service backend gracefully.
    /// </summary>
    /// <param name="session">User session</param>
    /// <param name="input">Stop service request data</param>
    /// <returns>Service stop response</returns>
    public static async Task<JObject> StopVoiceService(Session session, JObject input)
    {
        const string operation = "StopVoiceService";
        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");
            // Stop service through service layer
            var response = await PythonBackendService.Instance.StopAsync();
            return response.ToJObject();
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
                var installResponse = new ServiceStatusResponse
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
            // Performing backend health check
            Logs.Debug("[VoiceAssistant] Performing backend health check");
            var healthInfo = await PythonBackendService.Instance.GetHealthAsync();
            var response = new ServiceStatusResponse
            {
                Success = true,
                BackendRunning = healthInfo.IsRunning,
                BackendHealthy = healthInfo.IsHealthy,
                BackendUrl = Configuration.ServiceConfiguration.BackendUrl,
                ProcessId = healthInfo.ProcessId,
                HasExited = healthInfo.HasExited,
                Message = healthInfo.IsHealthy ? "Service is healthy" : healthInfo.ErrorMessage
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
            var response = await PythonBackendService.Instance.GetInstallationStatusAsync();
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
            var response = PythonBackendService.Instance.GetInstallationProgress();
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
            var request = new STTRequest
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
            var request = new TTSRequest
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
            var request = new PipelineRequest
            {
                SessionId = sessionId,
                InputType = input["input_type"]?.ToString() ?? "audio",
                InputData = input["input_data"]?.ToString() ?? string.Empty,
                PipelineSteps = new List<PipelineStep>()
            };
            // Parse pipeline steps
            if (input["pipeline_steps"] is JArray stepsArray)
            {
                foreach (var stepToken in stepsArray)
                {
                    if (stepToken is JObject stepObj)
                    {
                        var step = new PipelineStep
                        {
                            Type = stepObj["type"]?.ToString() ?? "unknown",
                            Enabled = stepObj["enabled"]?.Value<bool>() ?? true,
                            Config = stepObj["config"] as JObject ?? new JObject()
                        };
                        request.PipelineSteps.Add(step);
                    }
                }
            }
            // Validate the request
            request.Validate();
            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid pipeline request: {ex.Message}", ex);
        }
    }
    #endregion
}