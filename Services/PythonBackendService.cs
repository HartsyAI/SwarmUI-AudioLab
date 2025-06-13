using SwarmUI.Utils;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// Service for coordinating the Python backend with modern, generic endpoints.
/// Handles STT, TTS, and pipeline processing with proper separation of concerns.
/// Implements singleton pattern for centralized service management.
/// </summary>
public class PythonBackendService : IDisposable
{
    private static readonly Lazy<PythonBackendService> _instance = new(() => new PythonBackendService());
    public static PythonBackendService Instance => _instance.Value;

    private readonly object _serviceLock = new();
    private readonly PythonProcess _pythonProcess;
    private readonly DependencyInstaller _dependencyInstaller;
    private PythonEnvironmentInfo _pythonEnvironment;
    private volatile bool _isInitialized = false;
    private volatile bool _disposed = false;

    public bool IsBackendRunning => _pythonProcess?.IsRunning ?? false;
    public bool IsInstalling => _dependencyInstaller?.IsInstalling ?? false;

    private PythonBackendService()
    {
        _pythonProcess = new PythonProcess();
        _dependencyInstaller = new DependencyInstaller();

        // Subscribe to process events
        _pythonProcess.ProcessExited += OnProcessExited;
        _pythonProcess.OutputReceived += OnProcessOutput;
        _pythonProcess.ErrorReceived += OnProcessError;
    }

    /// <summary>
    /// Initializes the service and detects the Python environment.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        try
        {
            Logs.Debug("[VoiceAssistant] Initializing Python backend service with generic endpoints");

            // Detect Python environment
            _pythonEnvironment = _dependencyInstaller.DetectPythonEnvironment();

            if (_pythonEnvironment == null)
            {
                Logs.Warning("[VoiceAssistant] Python environment not detected during initialization");
            }
            else
            {
                Logs.Info($"[VoiceAssistant] Python environment detected: {_pythonEnvironment.PythonPath}");
            }

            _isInitialized = true;
            Logs.Debug("[VoiceAssistant] Python backend service initialized for generic endpoints");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize Python backend service: {ex.Message}");
            throw;
        }
    }

    #region Generic Processing Endpoints

    /// <summary>
    /// Process pure Speech-to-Text request.
    /// </summary>
    /// <param name="request">STT request data</param>
    /// <returns>STT response data</returns>
    public async Task<STTResponse> ProcessSTTAsync(STTRequest request)
    {
        EnsureInitialized();
        request.Validate();

        ApiProgressTracker processingTracker = ProgressTracking.GetApiTracker($"stt_{Guid.NewGuid():N}");
        DateTime startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("STT Processing");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Processing", "Transcribing audio...");

            // Call STT service using the generic endpoint
            JObject backendResponse = await BackendHttpClient.Instance.CallSTTServiceAsync(request);

            STTResponse response = new STTResponse
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                Transcription = backendResponse?["transcription"]?.ToString() ?? string.Empty,
                Confidence = backendResponse?["confidence"]?.Value<float>() ?? 0.0f,
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };

            // Parse alternatives if requested and available
            if (request.Options.ReturnAlternatives && backendResponse?["alternatives"] is JArray alternatives)
            {
                response.Alternatives = alternatives.Select(a => a.ToString()).ToArray();
            }

            // Parse metadata
            if (backendResponse?["metadata"] is JObject metadata)
            {
                response.Metadata = new STTMetadata
                {
                    ModelUsed = metadata["model_used"]?.ToString() ?? "unknown",
                    AudioDuration = metadata["audio_duration"]?.Value<double>() ?? 0.0,
                    AudioFormat = metadata["audio_format"]?.ToString() ?? "unknown",
                    SampleRate = metadata["sample_rate"]?.Value<int>() ?? 0
                };
            }

            if (!response.Success)
            {
                response.Message = backendResponse?["error"]?.ToString() ?? "STT processing failed";
                throw new InvalidOperationException(response.Message);
            }

            if (string.IsNullOrEmpty(response.Transcription))
            {
                throw new InvalidOperationException("Speech recognition returned no transcription");
            }

            processingTracker.SetComplete();
            Logs.Info($"[VoiceAssistant] STT processing completed: '{response.Transcription}'");

            return response;
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] STT processing failed: {ex.Message}");

            return new STTResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };
        }
        finally
        {
            // Clean up tracker after a delay
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
            {
                try
                {
                    ProgressTracking.RemoveTracker(processingTracker.Id);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"[VoiceAssistant] Error cleaning up tracker {processingTracker.Id}: {ex.Message}");
                }
            }, TaskScheduler.Current);
        }
    }

    /// <summary>
    /// Process pure Text-to-Speech request.
    /// </summary>
    /// <param name="request">TTS request data</param>
    /// <returns>TTS response data</returns>
    public async Task<TTSResponse> ProcessTTSAsync(TTSRequest request)
    {
        EnsureInitialized();
        request.Validate();

        ApiProgressTracker processingTracker = ProgressTracking.GetApiTracker($"tts_{Guid.NewGuid():N}");
        DateTime startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("TTS Processing");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Processing", "Generating speech...");

            // Call TTS service using the generic endpoint
            JObject backendResponse = await BackendHttpClient.Instance.CallTTSServiceAsync(request);

            TTSResponse response = new TTSResponse
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                AudioData = backendResponse?["audio_data"]?.ToString() ?? string.Empty,
                Duration = backendResponse?["duration"]?.Value<double>() ?? 0.0,
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };

            // Parse metadata
            if (backendResponse?["metadata"] is JObject metadata)
            {
                response.Metadata = new TTSMetadata
                {
                    VoiceUsed = metadata["voice_used"]?.ToString() ?? request.Voice,
                    SampleRate = metadata["sample_rate"]?.Value<int>() ?? 22050,
                    AudioFormat = metadata["audio_format"]?.ToString() ?? request.Options.Format,
                    AudioChannels = metadata["audio_channels"]?.Value<int>() ?? 1
                };
            }

            if (!response.Success)
            {
                response.Message = backendResponse?["error"]?.ToString() ?? "TTS processing failed";
                throw new InvalidOperationException(response.Message);
            }

            if (string.IsNullOrEmpty(response.AudioData))
            {
                throw new InvalidOperationException("Text-to-speech returned no audio data");
            }

            processingTracker.SetComplete();
            Logs.Info($"[VoiceAssistant] TTS processing completed for text: '{request.Text.Substring(0, Math.Min(50, request.Text.Length))}...'");

            return response;
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] TTS processing failed: {ex.Message}");

            return new TTSResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };
        }
        finally
        {
            // Clean up tracker after a delay
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
            {
                try
                {
                    ProgressTracking.RemoveTracker(processingTracker.Id);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"[VoiceAssistant] Error cleaning up tracker {processingTracker.Id}: {ex.Message}");
                }
            }, TaskScheduler.Current);
        }
    }

    /// <summary>
    /// Process configurable pipeline request.
    /// </summary>
    /// <param name="request">Pipeline request data</param>
    /// <returns>Pipeline response data</returns>
    public async Task<PipelineResponse> ProcessPipelineAsync(PipelineRequest request)
    {
        EnsureInitialized();
        request.Validate();

        ApiProgressTracker processingTracker = ProgressTracking.GetApiTracker($"pipeline_{Guid.NewGuid():N}");
        DateTime startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("Pipeline Processing");
            processingTracker.UpdateProgress(10, "Starting", "Initializing pipeline...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Processing", "Executing pipeline...");

            // Call pipeline service using the generic endpoint
            JObject backendResponse = await BackendHttpClient.Instance.CallPipelineServiceAsync(request);

            PipelineResponse response = new PipelineResponse
            {
                Success = backendResponse?["success"]?.Value<bool>() ?? false,
                PipelineResults = new Dictionary<string, JObject>(),
                ExecutedSteps = new List<string>(),
                TotalProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds,
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };

            // Parse pipeline results
            if (backendResponse?["pipeline_results"] is JObject pipelineResults)
            {
                foreach (JProperty prop in pipelineResults.Properties())
                {
                    if (prop.Value is JObject stepResult)
                    {
                        response.PipelineResults[prop.Name] = stepResult;
                    }
                }
            }

            // Parse executed steps
            if (backendResponse?["executed_steps"] is JArray executedSteps)
            {
                response.ExecutedSteps = executedSteps.Select(s => s.ToString()).ToList();
            }

            // Get total processing time from backend if available
            if (backendResponse?["total_processing_time"]?.Value<double>() is double backendTime)
            {
                response.TotalProcessingTime = backendTime;
            }

            if (!response.Success)
            {
                response.Message = backendResponse?["error"]?.ToString() ?? "Pipeline processing failed";
                throw new InvalidOperationException(response.Message);
            }

            processingTracker.SetComplete();
            Logs.Info($"[VoiceAssistant] Pipeline processing completed with {response.ExecutedSteps.Count} steps");

            return response;
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] Pipeline processing failed: {ex.Message}");

            return new PipelineResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                TotalProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds,
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };
        }
        finally
        {
            // Clean up tracker after a delay
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
            {
                try
                {
                    ProgressTracking.RemoveTracker(processingTracker.Id);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"[VoiceAssistant] Error cleaning up tracker {processingTracker.Id}: {ex.Message}");
                }
            }, TaskScheduler.Current);
        }
    }

    #endregion

    #region Service Management (Existing Methods)

    /// <summary>
    /// Starts the Python backend with automatic dependency installation if needed.
    /// </summary>
    /// <returns>Service status response</returns>
    public async Task<ServiceStatusResponse> StartAsync()
    {
        EnsureInitialized();

        // Check for ongoing installation first, before taking lock
        if (_dependencyInstaller.IsInstalling)
        {
            Logs.Info("[VoiceAssistant] Installation in progress, deferring backend start");
            return new ServiceStatusResponse
            {
                Success = false,
                Message = "Dependencies are currently being installed. Please wait for installation to complete before starting the backend.",
                BackendRunning = false,
                BackendHealthy = false
            };
        }

        lock (_serviceLock)
        {
            if (IsBackendRunning)
            {
                return new ServiceStatusResponse
                {
                    Success = true,
                    Message = "Backend is already running",
                    BackendRunning = true,
                    BackendHealthy = true,
                    BackendUrl = ServiceConfiguration.BackendUrl,
                    ProcessId = _pythonProcess.ProcessId,
                    HasExited = _pythonProcess.HasExited
                };
            }
        }

        try
        {
            // Ensure we have a valid Python environment
            if (_pythonEnvironment?.IsValid != true)
            {
                _pythonEnvironment = _dependencyInstaller.DetectPythonEnvironment();
                if (_pythonEnvironment?.IsValid != true)
                {
                    throw new InvalidOperationException(
                        "SwarmUI Python environment not found. Voice Assistant requires SwarmUI with ComfyUI backend installed.");
                }
            }

            // Check and install dependencies if needed - lock prevents concurrent installation attempts
            await EnsureDependenciesAsync();

            // Check again if another thread started installation while we were waiting
            if (_dependencyInstaller.IsInstalling)
            {
                Logs.Info("[VoiceAssistant] Another thread started installation, deferring backend start");
                return new ServiceStatusResponse
                {
                    Success = false,
                    Message = "Dependencies are currently being installed. Please wait for installation to complete before starting the backend.",
                    BackendRunning = false,
                    BackendHealthy = false
                };
            }

            // Start the Python process
            if (_pythonEnvironment?.PythonPath == null)
            {
                throw new InvalidOperationException("Python path is not defined");
            }

            bool started = await _pythonProcess.StartAsync(_pythonEnvironment.PythonPath);
            if (!started)
            {
                throw new InvalidOperationException("Failed to start Python backend process");
            }

            // Wait for backend to become healthy
            var healthInfo = await WaitForBackendHealthAsync();
            if (!healthInfo.IsHealthy)
            {
                throw new InvalidOperationException("Backend started but failed health check");
            }

            // Validate backend compatibility with generic endpoints
            var isCompatible = await BackendHttpClient.Instance.ValidateBackendCompatibilityAsync();
            if (!isCompatible)
            {
                Logs.Warning("[VoiceAssistant] Backend may not support all generic endpoints");
            }

            return new ServiceStatusResponse
            {
                Success = true,
                Message = "Backend started successfully with generic endpoints",
                BackendRunning = true,
                BackendHealthy = true,
                BackendUrl = ServiceConfiguration.BackendUrl,
                ProcessId = _pythonProcess.ProcessId,
                HasExited = _pythonProcess.HasExited,
                Services = healthInfo.Services
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to start backend: {ex.Message}");

            var errorMessage = GetUserFriendlyStartupError(ex);
            return new ServiceStatusResponse
            {
                Success = false,
                Message = errorMessage,
                BackendRunning = false,
                BackendHealthy = false
            };
        }
    }

    /// <summary>
    /// Stops the Python backend gracefully.
    /// </summary>
    /// <returns>Service status response</returns>
    public async Task<ServiceStatusResponse> StopAsync()
    {
        EnsureInitialized();

        try
        {
            if (!IsBackendRunning)
            {
                return new ServiceStatusResponse
                {
                    Success = true,
                    Message = "Backend is not running",
                    BackendRunning = false,
                    BackendHealthy = false
                };
            }

            // Try graceful shutdown via HTTP first
            try
            {
                await BackendHttpClient.Instance.SendShutdownSignalAsync();
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Graceful shutdown signal failed: {ex.Message}");
            }

            // Stop the process
            bool stopped = await _pythonProcess.StopAsync();

            return new ServiceStatusResponse
            {
                Success = stopped,
                Message = stopped ? "Backend stopped successfully" : "Backend stop encountered issues",
                BackendRunning = IsBackendRunning,
                BackendHealthy = false
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error stopping backend: {ex.Message}");
            return new ServiceStatusResponse
            {
                Success = false,
                Message = $"Error stopping backend: {ex.Message}",
                BackendRunning = IsBackendRunning,
                BackendHealthy = false
            };
        }
    }

    /// <summary>
    /// Forces immediate termination of the backend.
    /// </summary>
    public void ForceStop()
    {
        try
        {
            _pythonProcess?.ForceStop();
            Logs.Warning("[VoiceAssistant] Backend force stopped");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error during force stop: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current backend health status.
    /// </summary>
    /// <returns>Backend health information</returns>
    public async Task<BackendHealthInfo> GetHealthAsync()
    {
        try
        {
            var healthInfo = await BackendHttpClient.Instance.CheckHealthAsync();

            // Add process information
            if (_pythonProcess != null)
            {
                healthInfo.ProcessId = _pythonProcess.ProcessId;
                healthInfo.HasExited = _pythonProcess.HasExited;
                healthInfo.IsRunning = _pythonProcess.IsRunning;
            }

            return healthInfo;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Health check error: {ex.Message}");
            return new BackendHealthInfo
            {
                IsRunning = IsBackendRunning,
                IsHealthy = false,
                IsResponding = false,
                ErrorMessage = ex.Message,
                ProcessId = _pythonProcess?.ProcessId ?? 0,
                HasExited = _pythonProcess?.HasExited ?? true
            };
        }
    }

    /// <summary>
    /// Gets installation status for dependencies.
    /// </summary>
    /// <returns>Installation status response</returns>
    public async Task<InstallationStatusResponse> GetInstallationStatusAsync()
    {
        try
        {
            var pythonInfo = _pythonEnvironment ?? _dependencyInstaller.DetectPythonEnvironment();

            if (pythonInfo?.IsValid != true)
            {
                return new InstallationStatusResponse
                {
                    Success = false,
                    Message = "SwarmUI Python environment not found",
                    PythonDetected = false,
                    RequiredLibraries = new[] { ServiceConfiguration.PrimarySTTEngine, "Chatterbox TTS" }
                };
            }

            var dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(pythonInfo);
            var installationDetails = await _dependencyInstaller.GetDetailedInstallationStatusAsync(pythonInfo);

            return new InstallationStatusResponse
            {
                Success = true,
                PythonDetected = true,
                PythonPath = pythonInfo.PythonPath,
                OperatingSystem = pythonInfo.OperatingSystem,
                IsEmbeddedPython = pythonInfo.IsEmbedded,
                DependenciesInstalled = dependenciesInstalled,
                InstallationDetails = installationDetails,
                RequiredLibraries = new[] { ServiceConfiguration.PrimarySTTEngine, "Chatterbox TTS" }
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting installation status: {ex.Message}");
            return new InstallationStatusResponse
            {
                Success = false,
                Message = $"Error checking installation: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets current installation progress.
    /// </summary>
    /// <returns>Installation progress response</returns>
    public InstallationProgressResponse GetInstallationProgress()
    {
        return ProgressTracking.Installation.ToResponse();
    }

    #endregion

    #region Server-Side Recording Methods

    /// <summary>Start server-side recording session.
    /// Records audio directly on the server, bypassing browser limitations.</summary>
    /// <param name="request">Recording request parameters</param>
    /// <returns>Recording response with session information</returns>
    public async Task<RecordingResponse> StartServerRecordingAsync(RecordingRequest request)
    {
        EnsureInitialized();
        request.Validate();

        string processingTrackerId = $"recording_{Guid.NewGuid():N}";
        var processingTracker = ProgressTracking.GetApiTracker(processingTrackerId);
        DateTime startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("Server Recording Start");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Recording", "Starting server-side recording...");

            // Call server recording service using HTTP client
            RecordingResponse response = await BackendHttpClient.Instance.CallStartRecordingServiceAsync(request);

            response.ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds;

            if (!response.Success)
            {
                response.Message = response.Message ?? "Server recording failed to start";
                throw new InvalidOperationException(response.Message);
            }

            processingTracker.SetComplete();
            Logs.Info($"[VoiceAssistant] Server recording started: ID={response.RecordingId}, Duration={response.Duration}s");

            return response;
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] Server recording start failed: {ex.Message}");

            return new RecordingResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };
        }
        finally
        {
            // Clean up tracker after a delay
            Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
                ProgressTracking.RemoveTracker(processingTracker.Id));
        }
    }

    /// <summary>Stop server-side recording session and get results.
    /// Stops recording and processes the audio based on the original request mode.</summary>
    /// <param name="sessionId">Session ID that started the recording</param>
    /// <returns>Recording response with processed results</returns>
    public async Task<RecordingResponse> StopServerRecordingAsync(string sessionId)
    {
        EnsureInitialized();

        string processingTrackerId = $"recording_stop_{Guid.NewGuid():N}";
        var processingTracker = ProgressTracking.GetApiTracker(processingTrackerId);
        DateTime startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("Server Recording Stop");
            processingTracker.UpdateProgress(10, "Stopping", "Stopping server recording...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(50, "Processing", "Processing recorded audio...");

            // Call stop recording service using HTTP client
            RecordingResponse response = await BackendHttpClient.Instance.CallStopRecordingServiceAsync(sessionId);

            response.ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds;

            if (!response.Success)
            {
                response.Message = response.Message ?? "Server recording failed to stop";
                throw new InvalidOperationException(response.Message);
            }

            processingTracker.SetComplete();
            Logs.Info($"[VoiceAssistant] Server recording completed: ID={response.RecordingId}");

            // Log what we got back
            if (!string.IsNullOrEmpty(response.Transcription))
            {
                Logs.Info($"[VoiceAssistant] Recording transcription: '{response.Transcription}'");
            }

            return response;
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] Server recording stop failed: {ex.Message}");

            return new RecordingResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                ProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds
            };
        }
        finally
        {
            // Clean up tracker after a delay
            Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
                ProgressTracking.RemoveTracker(processingTracker.Id));
        }
    }

    /// <summary>Get status of current server-side recording session.</summary>
    /// <param name="sessionId">Session ID that started the recording</param>
    /// <returns>Recording status information</returns>
    public async Task<RecordingStatusResponse> GetRecordingStatusAsync(string sessionId)
    {
        EnsureInitialized();

        try
        {
            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            // Call recording status service using HTTP client
            return await BackendHttpClient.Instance.CallRecordingStatusServiceAsync(sessionId);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Recording status check failed: {ex.Message}");

            return new RecordingStatusResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex),
                Status = "error"
            };
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Ensures dependencies are installed before starting the backend.
    /// </summary>
    private async Task EnsureDependenciesAsync()
    {
        try
        {
            if (_pythonEnvironment == null)
            {
                throw new InvalidOperationException("Python environment not detected");
            }
            
            var dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(_pythonEnvironment);

            if (!dependenciesInstalled)
            {
                Logs.Info("[VoiceAssistant] Dependencies not found, starting installation...");
                await _dependencyInstaller.InstallDependenciesAsync(_pythonEnvironment);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to ensure dependencies: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Waits for the backend to become healthy.
    /// </summary>
    private async Task<BackendHealthInfo> WaitForBackendHealthAsync()
    {
        var healthTracker = new HealthCheckProgressTracker
        {
            MaxAttempts = ServiceConfiguration.MaxHealthCheckAttempts
        };

        for (int attempt = 1; attempt <= ServiceConfiguration.MaxHealthCheckAttempts; attempt++)
        {
            healthTracker.IncrementAttempt();

            // Check if process is still running
            if (!IsBackendRunning)
            {
                healthTracker.SetError("Backend process died during health check");
                throw new InvalidOperationException("Backend process exited unexpectedly");
            }

            try
            {
                var healthInfo = await BackendHttpClient.Instance.CheckHealthAsync();
                if (healthInfo.IsHealthy)
                {
                    healthTracker.SetHealthy();
                    Logs.Info($"[VoiceAssistant] Backend healthy after {attempt} attempts");
                    return healthInfo;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Log the error but continue trying
                Logs.Debug($"[VoiceAssistant] Health check attempt {attempt} failed: {ex.Message}");
            }

            // Wait before retrying
            var delay = attempt <= 10 ? 1000 : Math.Min(5000, 1000 * attempt / 10);
            await Task.Delay(delay);
        }

        healthTracker.SetError("Backend failed to become healthy within timeout");
        throw new TimeoutException("Backend failed to become healthy within timeout");
    }

    /// <summary>
    /// Ensures the backend is healthy for processing requests.
    /// </summary>
    private async Task EnsureBackendHealthyAsync()
    {
        if (!IsBackendRunning)
        {
            throw new InvalidOperationException("Backend is not running");
        }

        try
        {
            var healthInfo = await BackendHttpClient.Instance.CheckHealthAsync();
            if (!healthInfo.IsHealthy)
            {
                throw new InvalidOperationException($"Backend is not healthy: {healthInfo.ErrorMessage ?? "Unknown error"}");
            }
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw new InvalidOperationException($"Failed to check backend health: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets user-friendly error messages for startup failures.
    /// </summary>
    private string GetUserFriendlyStartupError(Exception ex)
    {
        var message = ex.Message;

        if (message.Contains("Python environment"))
        {
            return "Voice Assistant requires SwarmUI with ComfyUI backend installed. Please ensure ComfyUI is properly set up and working in SwarmUI.";
        }

        if (message.Contains("RealtimeSTT"))
        {
            return "Failed to install RealtimeSTT speech recognition library. This may be due to Python version compatibility (requires Python 3.9-3.12) or network issues.";
        }

        if (message.Contains("Chatterbox") || message.Contains("TTS"))
        {
            return "Failed to install Chatterbox TTS speech synthesis library. This may be due to Python version compatibility or network issues.";
        }

        if (message.Contains("timeout") || message.Contains("time"))
        {
            return "The installation process is taking longer than expected. Large packages like TorchAudio can take 10+ minutes to download and install.";
        }

        if (message.Contains("endpoint") || message.Contains("compatibility"))
        {
            return "Backend started but may not support all generic endpoints. Please check the Python backend version.";
        }

        return $"Failed to start voice service: {message}";
    }

    /// <summary>
    /// Ensures the service is initialized.
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PythonBackendService));
        }
    }

    #endregion

    #region Event Handlers

    private void OnProcessExited(object sender, EventArgs e)
    {
        try
        {
            Logs.Info("[VoiceAssistant] Backend process exited unexpectedly");
            // You might want to trigger cleanup or handle the exit in some way
            // This would be a good place to notify any subscribers or log detailed info
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error handling process exit: {ex.Message}");
        }
    }

    private void OnProcessOutput(object sender, string output)
    {
        // Process output is already logged by PythonProcess
        // Could add additional processing here if needed
    }

    private void OnProcessError(object sender, string error)
    {
        // Process errors are already logged by PythonProcess
        // Could add additional processing here if needed
    }

    #endregion

    /// <summary>
    /// Disposes of service resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _pythonProcess?.Dispose();
                Logs.Debug("[VoiceAssistant] Python backend service disposed");
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error disposing service: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
