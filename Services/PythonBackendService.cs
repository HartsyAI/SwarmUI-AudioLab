using SwarmUI.Utils;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Progress;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// Main service for coordinating the Python backend, dependency installation, and health monitoring.
/// Acts as the primary interface between the API layer and the backend components.
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
            Logs.Debug("[VoiceAssistant] Initializing Python backend service");

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
            Logs.Debug("[VoiceAssistant] Python backend service initialized");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize Python backend service: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Starts the Python backend with automatic dependency installation if needed.
    /// </summary>
    /// <returns>Service status response</returns>
    public async Task<ServiceStatusResponse> StartAsync()
    {
        EnsureInitialized();

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

            // Check and install dependencies if needed
            await EnsureDependenciesAsync();

            // Start the Python process
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

            return new ServiceStatusResponse
            {
                Success = true,
                Message = "Backend started successfully",
                BackendRunning = true,
                BackendHealthy = true,
                BackendUrl = ServiceConfiguration.BackendUrl,
                ProcessId = _pythonProcess.ProcessId,
                HasExited = _pythonProcess.HasExited
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

    /// <summary>
    /// Processes voice input through the backend.
    /// </summary>
    /// <param name="request">Voice input request</param>
    /// <returns>Voice processing response</returns>
    public async Task<VoiceProcessingResponse> ProcessVoiceInputAsync(VoiceInputRequest request)
    {
        EnsureInitialized();
        request.Validate();

        var processingTracker = ProgressTracking.GetApiTracker($"voice_{Guid.NewGuid():N}");

        try
        {
            processingTracker.SetOperation("Voice Input Processing");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "STT Processing", "Transcribing audio...");

            // Call STT service
            var sttRequest = new STTRequest
            {
                AudioData = request.AudioData,
                Language = request.Language
            };

            var sttResponse = await BackendHttpClient.Instance.CallSTTServiceAsync(sttRequest);
            var transcription = sttResponse?["transcription"]?.ToString();

            if (string.IsNullOrEmpty(transcription))
            {
                throw new InvalidOperationException("Speech recognition returned no transcription");
            }

            processingTracker.UpdateProgress(60, "Command Processing", "Processing command...");

            // TODO: Process command properly in future version
            var commandResponse = await ProcessCommandPlaceholderAsync(transcription);

            processingTracker.UpdateProgress(80, "TTS Processing", "Generating audio response...");

            // Generate TTS response if needed
            string audioResponse = null;
            if (!string.IsNullOrEmpty(commandResponse.Text))
            {
                try
                {
                    var ttsRequest = new TTSRequest
                    {
                        Text = commandResponse.Text,
                        Voice = request.Voice,
                        Language = request.Language,
                        Volume = request.Volume
                    };

                    var ttsResponse = await BackendHttpClient.Instance.CallTTSServiceAsync(ttsRequest);
                    audioResponse = ttsResponse?["audio_data"]?.ToString();
                }
                catch (Exception ttsEx)
                {
                    Logs.Warning($"[VoiceAssistant] TTS generation failed: {ttsEx.Message}");
                    // Continue without audio response
                }
            }

            processingTracker.SetComplete();

            return new VoiceProcessingResponse
            {
                Success = true,
                Transcription = transcription,
                AiResponse = commandResponse.Text,
                AudioResponse = audioResponse,
                CommandType = commandResponse.Command,
                SessionId = request.SessionId,
                Confidence = commandResponse.Confidence,
                ProcessingTime = processingTracker.ProcessingTime
            };
        }
        catch (Exception ex)
        {
            processingTracker.SetError(ex.Message);
            Logs.Error($"[VoiceAssistant] Voice processing failed: {ex.Message}");

            return new VoiceProcessingResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex)
            };
        }
        finally
        {
            // Clean up tracker after a delay
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
                ProgressTracking.RemoveTracker(processingTracker.Id));
        }
    }

    /// <summary>
    /// Processes text command through the backend.
    /// </summary>
    /// <param name="request">Text command request</param>
    /// <returns>Voice processing response</returns>
    public async Task<VoiceProcessingResponse> ProcessTextCommandAsync(TextCommandRequest request)
    {
        EnsureInitialized();
        request.Validate();

        try
        {
            // TODO: Process command properly in future version
            var commandResponse = await ProcessCommandPlaceholderAsync(request.Text);

            // Generate TTS response if backend is available
            string audioResponse = null;
            if (!string.IsNullOrEmpty(commandResponse.Text) && IsBackendRunning)
            {
                try
                {
                    var ttsRequest = new TTSRequest
                    {
                        Text = commandResponse.Text,
                        Voice = request.Voice,
                        Language = request.Language,
                        Volume = request.Volume
                    };

                    var ttsResponse = await BackendHttpClient.Instance.CallTTSServiceAsync(ttsRequest);
                    audioResponse = ttsResponse?["audio_data"]?.ToString();
                }
                catch (Exception ttsEx)
                {
                    Logs.Warning($"[VoiceAssistant] TTS generation failed: {ttsEx.Message}");
                    // Continue without audio response
                }
            }

            return new VoiceProcessingResponse
            {
                Success = true,
                AiResponse = commandResponse.Text,
                AudioResponse = audioResponse,
                CommandType = commandResponse.Command,
                SessionId = request.SessionId,
                Confidence = commandResponse.Confidence
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Text processing failed: {ex.Message}");

            return new VoiceProcessingResponse
            {
                Success = false,
                Message = ErrorHandling.GetUserFriendlyMessage(ex)
            };
        }
    }

    #region Private Methods

    /// <summary>
    /// Ensures dependencies are installed before starting the backend.
    /// </summary>
    private async Task EnsureDependenciesAsync()
    {
        try
        {
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

            var healthInfo = await BackendHttpClient.Instance.CheckHealthAsync();
            if (healthInfo.IsHealthy)
            {
                healthTracker.SetHealthy();
                Logs.Info($"[VoiceAssistant] Backend healthy after {attempt} attempts");
                return healthInfo;
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

        var healthInfo = await BackendHttpClient.Instance.CheckHealthAsync();
        if (!healthInfo.IsHealthy)
        {
            throw new InvalidOperationException("Backend is not healthy");
        }
    }

    /// <summary>
    /// TODO: Placeholder for command processing.
    /// This will be implemented in a future version.
    /// </summary>
    private async Task<CommandResponse> ProcessCommandPlaceholderAsync(string text)
    {
        // TODO: Implement proper command processing
        await Task.Delay(1); // Placeholder for async work

        return new CommandResponse
        {
            Text = "Command processing is not yet implemented. This will be added in a future version.",
            Command = "placeholder",
            Confidence = 0.0f
        };
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
        Logs.Info("[VoiceAssistant] Backend process exited unexpectedly");
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
