using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Models;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// HTTP client service for communicating with the Python backend.
/// Handles all HTTP communication, timeouts, and response parsing for backend services.
/// Implements singleton pattern for resource management.
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

    /// <summary>
    /// Calls the STT (Speech-to-Text) service endpoint.
    /// </summary>
    /// <param name="request">STT request data</param>
    /// <returns>STT response data</returns>
    public async Task<JObject> CallSTTServiceAsync(STTRequest request)
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

            return await PostToBackendAsync("/stt/transcribe", requestData);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] STT service call failed: {ex.Message}");
            throw new InvalidOperationException($"STT service call failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Calls the TTS (Text-to-Speech) service endpoint.
    /// </summary>
    /// <param name="request">TTS request data</param>
    /// <returns>TTS response data</returns>
    public async Task<JObject> CallTTSServiceAsync(TTSRequest request)
    {
        EnsureInitialized();

        try
        {
            Logs.Debug($"[VoiceAssistant] Calling TTS service for text: '{request.Text.Truncate(50)}'");

            var requestData = new JObject
            {
                ["text"] = request.Text,
                ["voice"] = request.Voice,
                ["language"] = request.Language,
                ["volume"] = request.Volume
            };

            return await PostToBackendAsync("/tts/synthesize", requestData);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] TTS service call failed: {ex.Message}");
            throw new InvalidOperationException($"TTS service call failed: {ex.Message}", ex);
        }
    }

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