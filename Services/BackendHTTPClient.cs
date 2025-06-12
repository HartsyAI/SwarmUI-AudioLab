using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Models;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// HTTP client service for communicating with the Python backend using modern endpoints.
/// Handles all HTTP communication, timeouts, and response parsing for backend services.
/// Implements singleton pattern for resource management with support for STT, TTS, and pipeline operations.
/// </summary>
public class BackendHttpClient : IDisposable
{
    private static readonly Lazy<BackendHttpClient> _instance = new(() => new BackendHttpClient());
    public static BackendHttpClient Instance => _instance.Value;

    private HttpClient _httpClient;
    private bool _isInitialized = false;
    private bool _disposed = false;

    private BackendHttpClient()
    {
        // Private constructor for singleton
    }

    /// <summary>
    /// Initializes the HTTP client with appropriate configuration.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized)
            return;

        try
        {
            _httpClient = new HttpClient
            {
                Timeout = ServiceConfiguration.ApiCallTimeout
            };

            _httpClient.DefaultRequestHeaders.Add("User-Agent", ServiceConfiguration.UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _isInitialized = true;
            Logs.Debug("[VoiceAssistant] HTTP client initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize HTTP client: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Performs a health check on the Python backend.
    /// </summary>
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
            var response = await _httpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint("/health"), cts.Token);

            healthInfo.IsRunning = true;
            healthInfo.IsResponding = true;
            healthInfo.IsHealthy = response.IsSuccessStatusCode;

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var healthData = JObject.Parse(content);

                // Extract additional health information if available
                if (healthData["services"] != null)
                {
                    var services = healthData["services"];
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

    #region Modern STT/TTS Endpoints

    /// <summary>
    /// Calls the pure STT (Speech-to-Text) service endpoint with enhanced options support.
    /// </summary>
    /// <param name="request">STT request data with options</param>
    /// <returns>STT response data</returns>
    public async Task<JObject> CallSTTServiceAsync(dynamic request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug("[VoiceAssistant] Calling STT service");

            var requestData = new JObject
            {
                ["audio_data"] = request.AudioData,
                ["language"] = request.Language
            };

            // Add options if provided
            if (request.Options != null)
            {
                var optionsObj = new JObject();

                if (request.Options.ReturnConfidence != null)
                    optionsObj["return_confidence"] = request.Options.ReturnConfidence;

                if (request.Options.ReturnAlternatives != null)
                    optionsObj["return_alternatives"] = request.Options.ReturnAlternatives;

                if (!string.IsNullOrEmpty(request.Options.ModelPreference))
                    optionsObj["model_preference"] = request.Options.ModelPreference;

                // Add custom options if any
                if (request.Options.CustomOptions != null && request.Options.CustomOptions.Count > 0)
                {
                    foreach (var customOption in request.Options.CustomOptions)
                    {
                        optionsObj[customOption.Key] = JToken.FromObject(customOption.Value);
                    }
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
    /// Calls the pure TTS (Text-to-Speech) service endpoint with enhanced options support.
    /// </summary>
    /// <param name="request">TTS request data with options</param>
    /// <returns>TTS response data</returns>
    public async Task<JObject> CallTTSServiceAsync(dynamic request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling TTS service for text: '{request.Text?.ToString()?.Substring(0, Math.Min(50, request.Text?.ToString()?.Length ?? 0))}...'");

            var requestData = new JObject
            {
                ["text"] = request.Text,
                ["voice"] = request.Voice,
                ["language"] = request.Language,
                ["volume"] = request.Volume
            };

            // Add options if provided
            if (request.Options != null)
            {
                var optionsObj = new JObject();

                if (request.Options.Speed != null && request.Options.Speed != 1.0f)
                    optionsObj["speed"] = request.Options.Speed;

                if (request.Options.Pitch != null && request.Options.Pitch != 1.0f)
                    optionsObj["pitch"] = request.Options.Pitch;

                if (!string.IsNullOrEmpty(request.Options.Format) && request.Options.Format != "wav")
                    optionsObj["format"] = request.Options.Format;

                // Add custom options if any
                if (request.Options.CustomOptions != null && request.Options.CustomOptions.Count > 0)
                {
                    foreach (var customOption in request.Options.CustomOptions)
                    {
                        optionsObj[customOption.Key] = JToken.FromObject(customOption.Value);
                    }
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
    /// <param name="inputType">Type of input (audio|text)</param>
    /// <param name="inputData">Input data</param>
    /// <param name="pipelineSteps">List of pipeline steps to execute</param>
    /// <returns>Pipeline response data</returns>
    public async Task<JObject> CallPipelineServiceAsync(string inputType, string inputData, List<PipelineStep> pipelineSteps)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling pipeline service with {pipelineSteps.Count} steps");

            var requestData = new JObject
            {
                ["input_type"] = inputType,
                ["input_data"] = inputData,
                ["pipeline_steps"] = new JArray()
            };

            // Convert pipeline steps to JSON
            var stepsArray = (JArray)requestData["pipeline_steps"];
            foreach (var step in pipelineSteps)
            {
                var stepObj = new JObject
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

    #region Legacy Support Methods (For Compatibility During Transition)

    /// <summary>
    /// Legacy method for backwards compatibility during transition.
    /// Converts old-style voice input to new STT + optional TTS pipeline.
    /// TODO: Remove this method once frontend is fully migrated.
    /// </summary>
    [Obsolete("Use CallSTTServiceAsync and CallTTSServiceAsync instead")]
    public async Task<JObject> CallLegacyVoiceInputAsync(string audioData, string language, string voice, float volume)
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Processing legacy voice input call");

            // Convert to pipeline call
            var pipelineSteps = new List<PipelineStep>
            {
                new PipelineStep
                {
                    Type = "stt",
                    Enabled = true,
                    Config = new JObject { ["language"] = language }
                }
            };

            // Add TTS step if voice is specified
            if (!string.IsNullOrEmpty(voice) && voice != "none")
            {
                pipelineSteps.Add(new PipelineStep
                {
                    Type = "tts",
                    Enabled = true,
                    Config = new JObject
                    {
                        ["voice"] = voice,
                        ["language"] = language,
                        ["volume"] = volume
                    }
                });
            }

            return await CallPipelineServiceAsync("audio", audioData, pipelineSteps);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Legacy voice input call failed: {ex.Message}");
            throw new InvalidOperationException($"Legacy voice input processing failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Legacy method for backwards compatibility during transition.
    /// Converts old-style text command to new TTS call.
    /// TODO: Remove this method once frontend is fully migrated.
    /// </summary>
    [Obsolete("Use CallTTSServiceAsync instead")]
    public async Task<JObject> CallLegacyTextCommandAsync(string text, string voice, string language, float volume)
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Processing legacy text command call");

            // Convert to pipeline call
            var pipelineSteps = new List<PipelineStep>
            {
                new PipelineStep
                {
                    Type = "tts",
                    Enabled = true,
                    Config = new JObject
                    {
                        ["voice"] = voice,
                        ["language"] = language,
                        ["volume"] = volume
                    }
                }
            };

            return await CallPipelineServiceAsync("text", text, pipelineSteps);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Legacy text command call failed: {ex.Message}");
            throw new InvalidOperationException($"Legacy text command processing failed: {ex.Message}", ex);
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
            var response = await _httpClient.PostAsync(
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
            var response = await _httpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint("/status"), cts.Token);

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
                        var response = await _httpClient.GetAsync(ServiceConfiguration.GetBackendEndpoint(endpoint), cts.Token);
                        results[endpoint] = response.IsSuccessStatusCode;
                    }
                    else
                    {
                        // For POST endpoints, just check if they respond (even with method not allowed)
                        var response = await _httpClient.PostAsync(
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
            var response = await _httpClient.PostAsync(ServiceConfiguration.GetBackendEndpoint(endpoint), content, cts.Token);

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
            throw new ObjectDisposedException(nameof(BackendHttpClient));
        }
    }

    #endregion

    /// <summary>
    /// Disposes of the HTTP client resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _httpClient?.Dispose();
                Logs.Debug("[VoiceAssistant] HTTP client disposed");
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error disposing HTTP client: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
