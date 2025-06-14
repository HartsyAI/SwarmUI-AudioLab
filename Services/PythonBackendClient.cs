using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using SwarmUI.Backends;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>client service for communicating with the Python backend using generic endpoints.
/// Handles communication and response parsing for modern STT, TTS, and pipeline operations.
/// Implements singleton pattern for resource management with clean separation of concerns.</summary>
public class PythonBackendClient
{
    private static readonly Lazy<PythonBackendClient> PythonInstance = new(() => new PythonBackendClient());
    public static PythonBackendClient Instance => PythonInstance.Value;

    /// <summary>Shared HttpClient for all Voice Assistant API requests</summary>
    protected static readonly HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();
    private bool _isInitialized = false;
    private bool _disposed = false;

    /// <summary>Initializes the HTTP client with appropriate configuration.</summary>
    public void Initialize()
    {
        if (_isInitialized)
            return;
        try
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", ServiceConfiguration.UserAgent);
            HttpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize HTTP client: {ex.Message}");
            throw;
        }
    }

    /// <summary>Performs a health check on the Python backend.</summary>
    /// <returns>Backend health information</returns>
    public async Task<BackendHealthInfo> CheckHealthAsync()
    {
        EnsureInitialized();

        var healthInfo = new BackendHealthInfo
        {
            IsRunning = false,
            IsHealthy = false,
            IsResponding = false,
            LastCheck = DateTime.UtcNow
        };

        try
        {
            Logs.Debug("[VoiceAssistant] Performing backend health check");

            using var cts = new CancellationTokenSource(ServiceConfiguration.HealthCheckTimeout);
            var response = await HttpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint("/health"), cts.Token);

            healthInfo.IsRunning = true;
            healthInfo.IsResponding = true;
            healthInfo.IsHealthy = response.IsSuccessStatusCode;

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                JObject healthData = JObject.Parse(content);

                // Extract additional health information if available
                if (healthData["services"] is JObject services)
                {
                    var sttReady = services["stt"]?.Value<bool>() ?? false;
                    var ttsReady = services["tts"]?.Value<bool>() ?? false;

                    healthInfo.IsHealthy = sttReady && ttsReady;
                    healthInfo.Services = new Dictionary<string, bool>
                    {
                        ["stt"] = sttReady,
                        ["tts"] = ttsReady
                    };
                }

                Logs.Debug($"[VoiceAssistant] Health check passed: {healthInfo.IsHealthy}");
            }
            else
            {
                healthInfo.ErrorMessage = $"Health check returned {response.StatusCode}";
                Logs.Debug($"[VoiceAssistant] Health check failed: {response.StatusCode}");
            }
        }
        catch (OperationCanceledException)
        {
            healthInfo.ErrorMessage = "Health check timed out";
            Logs.Debug("[VoiceAssistant] Health check timed out");
        }
        catch (HttpRequestException ex)
        {
            healthInfo.ErrorMessage = $"Network error: {ex.Message}";
            Logs.Debug($"[VoiceAssistant] Health check network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            healthInfo.ErrorMessage = $"Unexpected error: {ex.Message}";
            Logs.Debug($"[VoiceAssistant] Health check unexpected error: {ex.Message}");
        }

        return healthInfo;
    }

    #region Generic Processing Endpoints

    /// <summary>
    /// Calls the pure STT (Speech-to-Text) service endpoint.
    /// </summary>
    /// <param name="request">STT request data</param>
    /// <returns>STT response data</returns>
    public async Task<JObject> CallSTTServiceAsync(STTRequest request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug("[VoiceAssistant] Calling STT service");

            JObject requestData = new()
            {
                ["audio_data"] = request.AudioData,
                ["language"] = request.Language
            };

            // Add options if provided
            if (request.Options != null)
            {
                JObject optionsObj = [];

                if (request.Options.ReturnConfidence)
                    optionsObj["return_confidence"] = request.Options.ReturnConfidence;

                if (request.Options.ReturnAlternatives)
                    optionsObj["return_alternatives"] = request.Options.ReturnAlternatives;

                if (!string.IsNullOrEmpty(request.Options.ModelPreference))
                    optionsObj["model_preference"] = request.Options.ModelPreference;

                // Add custom options if any
                if (request.Options.CustomOptions?.Count > 0)
                {
                    JObject customObj = [];
                    foreach (var customOption in request.Options.CustomOptions)
                    {
                        customObj[customOption.Key] = JToken.FromObject(customOption.Value);
                    }
                    optionsObj["custom"] = customObj;
                }

                if (optionsObj.Count > 0)
                {
                    requestData["options"] = optionsObj;
                }
            }

            return await PostToBackendAsync("/stt/transcribe", requestData);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] STT service call failed: {ex.Message}");
            throw new InvalidOperationException($"STT service call failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Calls the pure TTS (Text-to-Speech) service endpoint.
    /// </summary>
    /// <param name="request">TTS request data</param>
    /// <returns>TTS response data</returns>
    public async Task<JObject> CallTTSServiceAsync(TTSRequest request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling TTS service for text: '{request.Text?[..Math.Min(50, request.Text?.Length ?? 0)]}...'");

            JObject requestData = new()
            {
                ["text"] = request.Text,
                ["voice"] = request.Voice,
                ["language"] = request.Language,
                ["volume"] = request.Volume
            };

            // Add options if provided
            if (request.Options != null)
            {
                JObject optionsObj = [];

                if (request.Options.Speed != 1.0f)
                    optionsObj["speed"] = request.Options.Speed;

                if (request.Options.Pitch != 1.0f)
                    optionsObj["pitch"] = request.Options.Pitch;

                if (!string.IsNullOrEmpty(request.Options.Format) && request.Options.Format != "wav")
                    optionsObj["format"] = request.Options.Format;

                // Add custom options if any
                if (request.Options.CustomOptions?.Count > 0)
                {
                    JObject customObj = [];
                    foreach (var customOption in request.Options.CustomOptions)
                    {
                        customObj[customOption.Key] = JToken.FromObject(customOption.Value);
                    }
                    optionsObj["custom"] = customObj;
                }

                if (optionsObj.Count > 0)
                {
                    requestData["options"] = optionsObj;
                }
            }

            return await PostToBackendAsync("/tts/synthesize", requestData);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] TTS service call failed: {ex.Message}");
            throw new InvalidOperationException($"TTS service call failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Calls the pipeline processing endpoint for configurable workflows.
    /// </summary>
    /// <param name="request">Pipeline request data</param>
    /// <returns>Pipeline response data</returns>
    public async Task<JObject> CallPipelineServiceAsync(PipelineRequest request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling pipeline service with {request.PipelineSteps.Count} steps");

            JObject requestData = new()
            {
                ["input_type"] = request.InputType,
                ["input_data"] = request.InputData,
                ["session_id"] = request.SessionId,
                ["pipeline_steps"] = new JArray()
            };

            // Convert pipeline steps to JSON
            var stepsArray = (JArray)requestData["pipeline_steps"];
            foreach (var step in request.PipelineSteps)
            {
                JObject stepObj = new()
                {
                    ["type"] = step.Type,
                    ["enabled"] = step.Enabled,
                    ["config"] = step.Config
                };
                stepsArray.Add(stepObj);
            }

            return await PostToBackendAsync("/pipeline/process", requestData);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Pipeline service call failed: {ex.Message}");
            throw new InvalidOperationException($"Pipeline service call failed: {ex.Message}", ex);
        }
    }

    #endregion

    #region Service Management

    /// <summary>
    /// Sends a shutdown signal to the Python backend.
    /// </summary>
    /// <returns>True if shutdown signal was sent successfully</returns>
    public async Task<bool> SendShutdownSignalAsync()
    {
        EnsureInitialized();

        try
        {
            Logs.Debug("[VoiceAssistant] Sending shutdown signal to backend");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await HttpClient.PostAsync(
                ServiceConfiguration.GetBackendEndpoint("/shutdown"),
                null,
                cts.Token);

            bool success = response.IsSuccessStatusCode;
            Logs.Debug($"[VoiceAssistant] Shutdown signal sent: {success}");
            return success;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error sending shutdown signal: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets detailed status information from the backend.
    /// </summary>
    /// <returns>Backend status data</returns>
    public async Task<JObject> GetBackendStatusAsync()
    {
        EnsureInitialized();

        try
        {
            Logs.Debug("[VoiceAssistant] Getting backend status");

            using var cts = new CancellationTokenSource(ServiceConfiguration.HealthCheckTimeout);
            var response = await HttpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint("/status"), cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return JObject.Parse(content);
            }
            else
            {
                throw new HttpRequestException($"Status request failed with {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to get backend status: {ex.Message}");
            throw new InvalidOperationException($"Backend status request failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Tests connectivity to specific backend endpoints.
    /// </summary>
    /// <returns>Connectivity test results</returns>
    public async Task<Dictionary<string, bool>> TestEndpointConnectivityAsync()
    {
        var results = new Dictionary<string, bool>();

        try
        {
            var endpoints = new[]
            {
                "/health",
                "/status",
                "/stt/transcribe",
                "/tts/synthesize",
                "/pipeline/process"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                    // For GET endpoints
                    if (endpoint == "/health" || endpoint == "/status")
                    {
                        var response = await HttpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint(endpoint), cts.Token);
                        results[endpoint] = response.IsSuccessStatusCode;
                    }
                    else
                    {
                        // For POST endpoints, just check if they respond (even with method not allowed)
                        var response = await HttpClient.PostAsync(
                            ServiceConfiguration.GetBackendEndpoint(endpoint),
                            new StringContent("{}", Encoding.UTF8, "application/json"),
                            cts.Token);

                        // Accept both success and method not allowed as "endpoint exists"
                        results[endpoint] = response.IsSuccessStatusCode ||
                                          response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ||
                                          response.StatusCode == System.Net.HttpStatusCode.BadRequest;
                    }
                }
                catch
                {
                    results[endpoint] = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Endpoint connectivity test failed: {ex.Message}");
        }

        return results;
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Gets information about supported endpoints and capabilities.
    /// </summary>
    /// <returns>Backend capability information</returns>
    public async Task<JObject> GetBackendCapabilitiesAsync()
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Getting backend capabilities");

            using var cts = new CancellationTokenSource(ServiceConfiguration.HealthCheckTimeout);
            var response = await HttpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint("/capabilities"), cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return JObject.Parse(content);
            }
            else
            {
                // Return default capabilities if endpoint doesn't exist
                return new JObject
                {
                    ["version"] = "2.0.0",
                    ["endpoint_version"] = "generic",
                    ["supported_endpoints"] = new JArray
                    {
                        "/stt/transcribe",
                        "/tts/synthesize",
                        "/pipeline/process",
                        "/health", 
                        "/status",
                        "/shutdown"
                    },
                    ["pipeline_step_types"] = new JArray { "stt", "tts", "command_processing" }
                };
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error getting backend capabilities: {ex.Message}");
            
            // Return minimal capabilities on error
            return new JObject
            {
                ["version"] = "unknown",
                ["endpoint_version"] = "generic",
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>
    /// Validates that the backend supports the required endpoints.
    /// </summary>
    /// <returns>True if all required endpoints are available</returns>
    public async Task<bool> ValidateBackendCompatibilityAsync()
    {
        try
        {
            var connectivity = await TestEndpointConnectivityAsync();
            
            var requiredEndpoints = new[] { "/health", "/stt/transcribe", "/tts/synthesize" };
            var availableEndpoints = connectivity.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
            
            var missingEndpoints = requiredEndpoints.Except(availableEndpoints).ToList();
            
            if (missingEndpoints.Any())
            {
                Logs.Warning($"[VoiceAssistant] Backend missing required endpoints: {string.Join(", ", missingEndpoints)}");
                return false;
            }
            
            Logs.Info("[VoiceAssistant] Backend compatibility validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Backend compatibility validation failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Server-Side Recording Services

    /// <summary>Call the server-side recording start service endpoint.</summary>
    /// <param name="request">Recording request data</param>
    /// <returns>Recording response data</returns>
    public async Task<RecordingResponse> CallStartRecordingServiceAsync(RecordingRequest request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling server recording start service: Duration={request.Duration}s, Mode={request.Mode}");

            JObject requestData = new()
            {
                ["session_id"] = request.SessionId,
                ["duration"] = request.Duration,
                ["language"] = request.Language,
                ["mode"] = request.Mode
            };

            // Add options if provided
            if (request.Options?.Count > 0)
            {
                JObject optionsObj = [];
                foreach (KeyValuePair<string, object> option in request.Options)
                {
                    optionsObj[option.Key] = JToken.FromObject(option.Value);
                }
                requestData["options"] = optionsObj;
            }

            JObject backendResponse = await PostToBackendAsync("/recording/start", requestData);

            RecordingResponse response = new()
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                IsRecording = backendResponse?["is_recording"]?.Value<bool>() ?? false,
                RecordingId = backendResponse?["recording_id"]?.ToString() ?? string.Empty,
                Duration = backendResponse?["duration"]?.Value<int>() ?? request.Duration,
                Mode = backendResponse?["mode"]?.ToString() ?? request.Mode
            };

            // Parse metadata if available
            if (backendResponse?["metadata"] is JObject metadata)
            {
                response.Metadata = new RecordingMetadata
                {
                    DeviceUsed = metadata["device_used"]?.ToString() ?? "default",
                    SampleRate = metadata["sample_rate"]?.Value<int>() ?? 16000,
                    AudioFormat = metadata["audio_format"]?.ToString() ?? "wav",
                    AudioChannels = metadata["audio_channels"]?.Value<int>() ?? 1,
                    StartTime = DateTime.TryParse(metadata["start_time"]?.ToString(), out DateTime startTime) ? startTime : DateTime.UtcNow
                };
            }

            if (!response.Success)
            {
                response.Message = backendResponse?["error"]?.ToString() ?? "Recording start failed";
                throw new InvalidOperationException(response.Message);
            }

            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Server recording start service call failed: {ex.Message}");
            throw new InvalidOperationException($"Server recording start service call failed: {ex.Message}", ex);
        }
    }

    /// <summary>Call the server-side recording stop service endpoint.</summary>
    /// <param name="sessionId">Session ID of the recording to stop</param>
    /// <returns>Recording response with processed results</returns>
    public async Task<RecordingResponse> CallStopRecordingServiceAsync(string sessionId)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling server recording stop service: SessionId={sessionId}");

            JObject requestData = new()
            {
                ["session_id"] = sessionId
            };

            JObject backendResponse = await PostToBackendAsync("/recording/stop", requestData);

            RecordingResponse response = new()
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                IsRecording = backendResponse?["is_recording"]?.Value<bool>() ?? false,
                RecordingId = backendResponse?["recording_id"]?.ToString() ?? string.Empty,
                AudioData = backendResponse?["audio_data"]?.ToString() ?? string.Empty,
                Transcription = backendResponse?["transcription"]?.ToString() ?? string.Empty,
                AIResponse = backendResponse?["ai_response"]?.ToString() ?? string.Empty
            };

            // Parse metadata if available
            if (backendResponse?["metadata"] is JObject metadata)
            {
                response.Metadata = new RecordingMetadata
                {
                    DeviceUsed = metadata["device_used"]?.ToString() ?? "default",
                    SampleRate = metadata["sample_rate"]?.Value<int>() ?? 16000,
                    AudioFormat = metadata["audio_format"]?.ToString() ?? "wav",
                    AudioChannels = metadata["audio_channels"]?.Value<int>() ?? 1,
                    ActualDuration = metadata["actual_duration"]?.Value<double>() ?? 0.0
                };

                if (DateTime.TryParse(metadata["start_time"]?.ToString(), out DateTime startTime))
                    response.Metadata.StartTime = startTime;
                if (DateTime.TryParse(metadata["end_time"]?.ToString(), out DateTime endTime))
                    response.Metadata.EndTime = endTime;
            }

            if (!response.Success)
            {
                response.Message = backendResponse?["error"]?.ToString() ?? "Recording stop failed";
                throw new InvalidOperationException(response.Message);
            }

            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Server recording stop service call failed: {ex.Message}");
            throw new InvalidOperationException($"Server recording stop service call failed: {ex.Message}", ex);
        }
    }

    /// <summary>Call the server-side recording status service endpoint.</summary>
    /// <param name="sessionId">Session ID of the recording to check</param>
    /// <returns>Recording status response</returns>
    public async Task<RecordingStatusResponse> CallRecordingStatusServiceAsync(string sessionId)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling server recording status service: SessionId={sessionId}");

            JObject requestData = new()
            {
                ["session_id"] = sessionId
            };

            JObject backendResponse = await PostToBackendAsync("/recording/status", requestData);

            RecordingStatusResponse response = new()
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                IsRecording = backendResponse?["is_recording"]?.Value<bool>() ?? false,
                RecordingId = backendResponse?["recording_id"]?.ToString() ?? string.Empty,
                ElapsedSeconds = backendResponse?["elapsed_seconds"]?.Value<int>() ?? 0,
                TotalDuration = backendResponse?["total_duration"]?.Value<int>() ?? 0,
                Status = backendResponse?["status"]?.ToString() ?? "unknown"
            };

            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Server recording status service call failed: {ex.Message}");
            return new RecordingStatusResponse
            {
                Success = false,
                Message = $"Recording status service call failed: {ex.Message}",
                Status = "error"
            };
        }
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Posts JSON data to a backend endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint path (starting with /)</param>
    /// <param name="data">JSON data to post</param>
    /// <returns>Response JSON data</returns>
    private async Task<JObject> PostToBackendAsync(string endpoint, JObject data)
    {
        try
        {
            var json = data.ToString();
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(ServiceConfiguration.ApiCallTimeout);
            var response = await HttpClient.PostAsync(ServiceConfiguration.GetBackendEndpoint(endpoint), content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Backend request failed: {response.StatusCode} - {errorContent}");
            }

            var responseText = await response.Content.ReadAsStringAsync();
            return JObject.Parse(responseText);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Backend request to {endpoint} timed out");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unexpected error calling {endpoint}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ensures the HTTP client is initialized before use.
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("HTTP client not initialized. Call Initialize() first.");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PythonBackendClient));
        }
    }

    #endregion

}
