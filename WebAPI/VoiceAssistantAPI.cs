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
    public static readonly PermInfoGroup VoiceAssistantPermGroup = new(
        "VoiceAssistant",
        "Permissions related to Voice Assistant functionality for API calls and voice processing."
    );

    public static readonly PermInfo PermProcessVoice = Permissions.Register(new(
        "voice_process_input",
        "Process Voice Input",
        "Allows processing of voice commands and audio transcription.",
        PermissionDefault.POWERUSERS,
        VoiceAssistantPermGroup
    ));

    public static readonly PermInfo PermManageService = Permissions.Register(new(
        "voice_manage_service",
        "Manage Voice Service",
        "Allows starting and stopping the voice processing backend.",
        PermissionDefault.POWERUSERS,
        VoiceAssistantPermGroup
    ));

    public static readonly PermInfo PermCheckStatus = Permissions.Register(new(
        "voice_check_status",
        "Check Voice Status",
        "Allows checking the status and health of voice services.",
        PermissionDefault.POWERUSERS,
        VoiceAssistantPermGroup
    ));
}

/// <summary>
/// Clean API layer for the Voice Assistant extension.
/// Provides RESTful endpoints with consistent error handling and response formatting.
/// All business logic is delegated to the service layer.
/// </summary>
[API.APIClass("API routes for the VoiceAssistant extension")]
public static class VoiceAssistantAPI
{
    /// <summary>
    /// Registers all API endpoints with appropriate permissions.
    /// </summary>
    public static void Register()
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Registering Voice Assistant API endpoints");

            // Voice processing endpoints
            API.RegisterAPICall(ProcessVoiceInput, false, VoiceAssistantPermissions.PermProcessVoice);
            API.RegisterAPICall(ProcessTextCommand, false, VoiceAssistantPermissions.PermProcessVoice);

            // Service management endpoints
            API.RegisterAPICall(StartVoiceService, false, VoiceAssistantPermissions.PermManageService);
            API.RegisterAPICall(StopVoiceService, false, VoiceAssistantPermissions.PermManageService);

            // Status and monitoring endpoints
            API.RegisterAPICall(GetVoiceStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(CheckInstallationStatus, false, VoiceAssistantPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, VoiceAssistantPermissions.PermCheckStatus);

            Logs.Debug("[VoiceAssistant] Voice Assistant API endpoints registered successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    #region Voice Processing Endpoints

    /// <summary>
    /// Processes voice input through the complete STT -> Command -> TTS pipeline.
    /// </summary>
    /// <param name="session">User session</param>
    /// <param name="input">Voice input request data</param>
    /// <returns>Voice processing response</returns>
    public static async Task<JObject> ProcessVoiceInput(Session session, JObject input)
    {
        const string operation = "ProcessVoiceInput";

        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");

            // Parse and validate request
            var request = ParseVoiceInputRequest(input, session.ID);

            // Process through service layer
            var response = await PythonBackendService.Instance.ProcessVoiceInputAsync(request);

            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    /// <summary>
    /// Processes text commands with optional TTS response generation.
    /// </summary>
    /// <param name="session">User session</param>
    /// <param name="input">Text command request data</param>
    /// <returns>Text processing response</returns>
    public static async Task<JObject> ProcessTextCommand(Session session, JObject input)
    {
        const string operation = "ProcessTextCommand";

        try
        {
            Logs.Debug($"[VoiceAssistant] {operation} request from session: {session.ID}");

            // Parse and validate request
            var request = ParseTextCommandRequest(input, session.ID);

            // Process through service layer
            var response = await PythonBackendService.Instance.ProcessTextCommandAsync(request);

            return response.ToJObject();
        }
        catch (ArgumentException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Invalid request parameters");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorHandling.HandleException(operation, ex, "Service not available");
        }
        catch (Exception ex)
        {
            return ErrorHandling.HandleException(operation, ex);
        }
    }

    #endregion

    #region Service Management Endpoints

    /// <summary>
    /// Starts the voice service backend with automatic dependency installation.
    /// </summary>
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

    /// <summary>
    /// Gets the current status and health of the voice service backend.
    /// </summary>
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

    /// <summary>
    /// Checks the installation status of required Python dependencies.
    /// </summary>
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

    /// <summary>
    /// Gets real-time installation progress for dependency installation.
    /// </summary>
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

    /// <summary>
    /// Parses and validates voice input request data.
    /// </summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated request</returns>
    private static VoiceInputRequest ParseVoiceInputRequest(JObject input, string sessionId)
    {
        try
        {
            var request = new VoiceInputRequest
            {
                SessionId = sessionId,
                AudioData = input["audio_data"]?.ToString() ?? string.Empty,
                Language = input["language"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultLanguage,
                Voice = input["voice"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultVoice,
                Volume = input["volume"]?.Value<float>() ?? Configuration.ServiceConfiguration.DefaultVolume
            };

            // Validate the request
            request.Validate();

            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid voice input request: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses and validates text command request data.
    /// </summary>
    /// <param name="input">Raw request JSON</param>
    /// <param name="sessionId">User session ID</param>
    /// <returns>Parsed and validated request</returns>
    private static TextCommandRequest ParseTextCommandRequest(JObject input, string sessionId)
    {
        try
        {
            var request = new TextCommandRequest
            {
                SessionId = sessionId,
                Text = input["text"]?.ToString() ?? string.Empty,
                Language = input["language"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultLanguage,
                Voice = input["voice"]?.ToString() ?? Configuration.ServiceConfiguration.DefaultVoice,
                Volume = input["volume"]?.Value<float>() ?? Configuration.ServiceConfiguration.DefaultVolume
            };

            // Validate the request
            request.Validate();

            return request;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid text command request: {ex.Message}", ex);
        }
    }

    #endregion
}
