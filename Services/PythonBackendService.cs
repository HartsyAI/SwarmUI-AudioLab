using SwarmUI.Utils;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Common;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// Updated service for coordinating the Python backend with modern, composable endpoints.
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

    #region Modern Processing Endpoints

    /// <summary>
    /// Process pure Speech-to-Text request.
    /// </summary>
    /// <param name="request">STT request data</param>
    /// <returns>STT response data</returns>
    public async Task<STTResponse> ProcessSTTAsync(STTRequest request)
    {
        EnsureInitialized();
        request.Validate();

        var processingTracker = ProgressTracking.GetApiTracker($"stt_{Guid.NewGuid():N}");
        var startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("STT Processing");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Processing", "Transcribing audio...");

            // Call STT service
            var sttBackendRequest = new STTBackendRequest
            {
                AudioData = request.AudioData,
                Language = request.Language,
                Options = request.Options
            };

            var backendResponse = await BackendHttpClient.Instance.CallSTTServiceAsync(sttBackendRequest);

            var response = new STTResponse
            {
                Success = true,
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
                ProgressTracking.RemoveTracker(processingTracker.Id));
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

        var processingTracker = ProgressTracking.GetApiTracker($"tts_{Guid.NewGuid():N}");
        var startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("TTS Processing");
            processingTracker.UpdateProgress(10, "Starting", "Validating backend availability...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            processingTracker.UpdateProgress(30, "Processing", "Generating speech...");

            // Call TTS service
            var ttsBackendRequest = new TTSBackendRequest
            {
                Text = request.Text,
                Voice = request.Voice,
                Language = request.Language,
                Volume = request.Volume,
                Options = request.Options
            };

            var backendResponse = await BackendHttpClient.Instance.CallTTSServiceAsync(ttsBackendRequest);

            var response = new TTSResponse
            {
                Success = true,
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
                ProgressTracking.RemoveTracker(processingTracker.Id));
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

        var processingTracker = ProgressTracking.GetApiTracker($"pipeline_{Guid.NewGuid():N}");
        var startTime = DateTime.UtcNow;

        try
        {
            processingTracker.SetOperation("Pipeline Processing");
            processingTracker.UpdateProgress(10, "Starting", "Initializing pipeline...");

            // Ensure backend is available
            await EnsureBackendHealthyAsync();

            var response = new PipelineResponse
            {
                Success = true,
                PipelineResults = new Dictionary<string, JObject>(),
                ExecutedSteps = new List<string>()
            };

            var currentData = request.InputData;
            var enabledSteps = request.PipelineSteps.Where(s => s.Enabled).ToList();
            var totalSteps = enabledSteps.Count;

            Logs.Info($"[VoiceAssistant] Processing pipeline with {totalSteps} steps for input type: {request.InputType}");

            for (int i = 0; i < enabledSteps.Count; i++)
            {
                var step = enabledSteps[i];
                var progressPercent = 20 + (int)((double)i / totalSteps * 70);

                processingTracker.UpdateProgress(progressPercent, $"Step {i + 1}/{totalSteps}", $"Processing {step.Type}...");

                try
                {
                    var stepResult = await ProcessPipelineStepAsync(step, currentData, request.InputType);
                    response.PipelineResults[step.Type] = stepResult.Data;
                    response.ExecutedSteps.Add(step.Type);

                    // Update current data for next step
                    if (stepResult.OutputData != null)
                    {
                        currentData = stepResult.OutputData;
                    }

                    Logs.Debug($"[VoiceAssistant] Pipeline step '{step.Type}' completed successfully");
                }
                catch (Exception stepEx)
                {
                    Logs.Error($"[VoiceAssistant] Pipeline step '{step.Type}' failed: {stepEx.Message}");

                    // Add error to results
                    response.PipelineResults[step.Type] = new JObject
                    {
                        ["success"] = false,
                        ["error"] = stepEx.Message
                    };

                    response.ExecutedSteps.Add($"{step.Type} (failed)");

                    // Continue with other steps or fail based on configuration
                    // For now, we'll continue but mark the overall response as failed
                    response.Success = false;
                    response.Message = $"Pipeline step '{step.Type}' failed: {stepEx.Message}";
                }
            }

            response.TotalProcessingTime = (DateTime.UtcNow - startTime).TotalSeconds;
            response.ProcessingTime = response.TotalProcessingTime;

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
                ProgressTracking.RemoveTracker(processingTracker.Id));
        }
    }

    #endregion

    #region Pipeline Step Processing

    /// <summary>
    /// Process a single pipeline step.
    /// </summary>
    /// <param name="step">Pipeline step configuration</param>
    /// <param name="inputData">Input data for the step</param>
    /// <param name="inputType">Type of the input data</param>
    /// <returns>Step processing result</returns>
    private async Task<PipelineStepResult> ProcessPipelineStepAsync(PipelineStep step, string inputData, string inputType)
    {
        switch (step.Type.ToLower())
        {
            case "stt":
                return await ProcessSTTPipelineStep(step, inputData);

            case "tts":
                return await ProcessTTSPipelineStep(step, inputData);

            case "command_processing":
                return await ProcessCommandPipelineStep(step, inputData);

            default:
                throw new ArgumentException($"Unknown pipeline step type: {step.Type}");
        }
    }

    /// <summary>
    /// Process STT pipeline step.
    /// </summary>
    private async Task<PipelineStepResult> ProcessSTTPipelineStep(PipelineStep step, string audioData)
    {
        var sttRequest = new STTRequest
        {
            AudioData = audioData,
            Language = step.Config["language"]?.ToString() ?? "en-US",
            Options = new STTOptions()
        };

        // Parse step-specific options
        if (step.Config["options"] is JObject options)
        {
            sttRequest.Options.ReturnConfidence = options["return_confidence"]?.Value<bool>() ?? true;
            sttRequest.Options.ReturnAlternatives = options["return_alternatives"]?.Value<bool>() ?? false;
            sttRequest.Options.ModelPreference = options["model_preference"]?.ToString() ?? "accuracy";
        }

        var sttResponse = await ProcessSTTAsync(sttRequest);

        return new PipelineStepResult
        {
            Data = sttResponse.ToJObject(),
            OutputData = sttResponse.Transcription, // Pass transcription to next step
            Success = sttResponse.Success
        };
    }

    /// <summary>
    /// Process TTS pipeline step.
    /// </summary>
    private async Task<PipelineStepResult> ProcessTTSPipelineStep(PipelineStep step, string text)
    {
        var ttsRequest = new TTSRequest
        {
            Text = text,
            Voice = step.Config["voice"]?.ToString() ?? "default",
            Language = step.Config["language"]?.ToString() ?? "en-US",
            Volume = step.Config["volume"]?.Value<float>() ?? 0.8f,
            Options = new TTSOptions()
        };

        // Parse step-specific options
        if (step.Config["options"] is JObject options)
        {
            ttsRequest.Options.Speed = options["speed"]?.Value<float>() ?? 1.0f;
            ttsRequest.Options.Pitch = options["pitch"]?.Value<float>() ?? 1.0f;
            ttsRequest.Options.Format = options["format"]?.ToString() ?? "wav";
        }

        var ttsResponse = await ProcessTTSAsync(ttsRequest);

        return new PipelineStepResult
        {
            Data = ttsResponse.ToJObject(),
            OutputData = ttsResponse.AudioData, // Pass audio data to next step
            Success = ttsResponse.Success
        };
    }

    /// <summary>
    /// Process command pipeline step.
    /// TODO: Implement proper command processing in future versions.
    /// </summary>
    private async Task<PipelineStepResult> ProcessCommandPipelineStep(PipelineStep step, string text)
    {
        // TODO: Implement actual command processing
        await Task.Delay(100); // Simulate processing

        var commandResponse = new CommandResponse
        {
            Text = "Command processing is not yet implemented. This will be added in a future version.",
            Command = "placeholder",
            Confidence = 0.0f,
            Success = true
        };

        return new PipelineStepResult
        {
            Data = commandResponse.ToJObject(),
            OutputData = commandResponse.Text, // Pass response text to next step
            Success = true
        };
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

    #endregion

    #region Helper Classes

    /// <summary>
    /// Result of processing a single pipeline step.
    /// </summary>
    private class PipelineStepResult
    {
        public JObject Data { get; set; } = new();
        public string OutputData { get; set; } = string.Empty;
        public bool Success { get; set; } = false;
    }

    /// <summary>
    /// Backend request for STT processing.
    /// </summary>
    private class STTBackendRequest
    {
        public string AudioData { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public STTOptions Options { get; set; } = new();
    }

    /// <summary>
    /// Backend request for TTS processing.
    /// </summary>
    private class TTSBackendRequest
    {
        public string Text { get; set; } = string.Empty;
        public string Voice { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public float Volume { get; set; } = 0.8f;
        public TTSOptions Options { get; set; } = new();
    }

    #endregion

    #region Existing Helper Methods (Updated)

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
