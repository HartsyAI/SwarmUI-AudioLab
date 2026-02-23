using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.SwarmBackends;

/// <summary>Speech-to-Text backend for SwarmUI integration using direct Python function calls. Processes audio input through STT service and returns transcription results. Also serves as the STT service for API endpoints.</summary>
public class STTBackend : VoiceAssistantBackends
{
    /// <summary>Configuration settings for the STT Swarm Backend</summary>
    public class STTBackendSettings : AutoConfiguration
    {
        /// <summary>Debug mode setting</summary>
        [AutoConfiguration.ConfigComment("Enable debug logging for voice processing")]
        public bool DebugMode = false;

        /// <summary>Preferred STT engine</summary>
        [AutoConfiguration.ConfigComment("Preferred STT engine (realtimestt, whisper, etc.)")]
        public string PreferredEngine = "realtimestt";
    }

    public static readonly Lazy<STTBackend> InstanceLazy = new(() => new STTBackend());
    public static STTBackend Instance => InstanceLazy.Value;

    private readonly object _serviceLock = new();
    private readonly DependencyInstaller _dependencyInstaller;
    private bool _isStarted = false;
    private bool _isInitialized = false;

    /// <summary>Backend type identifier</summary>
    protected override ServiceConfiguration.BackendType BackendType => ServiceConfiguration.BackendType.STT;

    /// <summary>Supported features for STT processing</summary>
    public override IEnumerable<string> SupportedFeatures =>
    [
        "transcription",
        "language_detection",
        "confidence_scoring",
        "real_time_processing"
    ];

    /// <summary>Gets whether the STT backend is currently available</summary>
    public bool IsBackendRunning
    {
        get
        {
            lock (_serviceLock)
            {
                return _isStarted && _isInitialized;
            }
        }
    }

    /// <summary>Private constructor for singleton pattern</summary>
    public STTBackend()
    {
        _dependencyInstaller = new DependencyInstaller();
        Logs.Debug("[STTBackend] STT backend instance created");
    }

    /// <summary>Initialize the STT backend</summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[STTBackend] Initializing Speech-to-Text backend");

            // Start the STT backend service (handles dependencies and initialization)
            BackendStatusResponse startResult = await StartAsync();

            if (!startResult.Success)
            {
                Logs.Warning($"[STTBackend] STT backend service failed to start: {startResult.Message}");
                Status = BackendStatus.ERRORED;
                return;
            }

            Status = BackendStatus.RUNNING;
            Logs.Info("[STTBackend] STT backend initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Failed to initialize: {ex.Message}");
            Status = BackendStatus.ERRORED;
            throw;
        }
    }

    /// <summary>Starts the STT backend with dependency checking and Python initialization.</summary>
    /// <param name="forceRestart">Whether to restart if already running</param>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> StartAsync(bool forceRestart = false)
    {
        lock (_serviceLock)
        {
            if (_isStarted && !forceRestart)
            {
                return new BackendStatusResponse
                {
                    Success = true,
                    Message = "STT backend already running",
                    Status = "running",
                    BackendType = "STT",
                    IsRunning = true
                };
            }
        }

        try
        {
            Logs.Info("[STTBackend] Starting STT backend service");

            // Step 1: Check dependencies
            bool dependenciesReady = await EnsureDependenciesAsync();
            if (!dependenciesReady)
            {
                return new BackendStatusResponse
                {
                    Success = false,
                    Message = "STT dependencies not available",
                    Status = "error",
                    BackendType = "STT"
                };
            }

            // Step 2: Initialize Python voice processor
            bool processorReady = await InitializePythonProcessorAsync();
            if (!processorReady)
            {
                return new BackendStatusResponse
                {
                    Success = false,
                    Message = "Failed to initialize Python voice processor",
                    Status = "error",
                    BackendType = "STT"
                };
            }

            lock (_serviceLock)
            {
                _isStarted = true;
                _isInitialized = true;
            }

            Logs.Info("[STTBackend] STT backend started successfully");

            return new BackendStatusResponse
            {
                Success = true,
                Message = "STT backend started successfully",
                Status = "running",
                BackendType = "STT",
                IsRunning = true
            };
        }
        catch (Exception ex)
        {
            lock (_serviceLock)
            {
                _isStarted = false;
                _isInitialized = false;
            }

            Logs.Error($"[STTBackend] Failed to start STT backend: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"STT backend startup failed: {ex.Message}",
                Status = "error",
                BackendType = "STT",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Stops the STT backend gracefully.</summary>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> StopAsync()
    {
        try
        {
            Logs.Info("[STTBackend] Stopping STT backend service");

            // Cleanup Python resources
            await PythonVoiceProcessor.Instance.CleanupAsync();

            lock (_serviceLock)
            {
                _isStarted = false;
                _isInitialized = false;
            }

            return new BackendStatusResponse
            {
                Success = true,
                Message = "STT backend stopped successfully",
                Status = "stopped",
                BackendType = "STT",
                IsRunning = false
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Failed to stop STT backend: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to stop STT backend: {ex.Message}",
                Status = "error",
                BackendType = "STT",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Gets the current status of the STT backend.</summary>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> GetStatusAsync()
    {
        try
        {
            bool isRunning = IsBackendRunning;
            string status = isRunning ? "running" : "stopped";

            BackendStatusResponse response = new()
            {
                Success = true,
                Status = status,
                BackendType = "STT",
                IsRunning = isRunning
            };

            // Add health information if running
            if (isRunning)
            {
                try
                {
                    bool isHealthy = await PythonVoiceProcessor.Instance.IsSTTAvailableAsync();
                    response.IsHealthy = isHealthy;
                    response.Message = isHealthy ? "STT backend running and healthy" : "STT backend running but unhealthy";
                }
                catch (Exception ex)
                {
                    response.IsHealthy = false;
                    response.Message = $"STT backend status check failed: {ex.Message}";
                }
            }
            else
            {
                response.Message = "STT backend not running";
            }

            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Failed to get STT backend status: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to get STT backend status: {ex.Message}",
                Status = "error",
                BackendType = "STT",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Load STT model - maps to language/engine configuration</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Verbose($"[STTBackend] Loading STT model: {model.Name}");

            // Validate model compatibility
            if (!IsModelCompatible(model))
            {
                Logs.Warning($"[STTBackend] Model not compatible: {model.Name}");
                return false;
            }

            CurrentModelName = model.Name;
            Logs.Verbose($"[STTBackend] Successfully loaded STT model: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error loading model {model.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Process STT input when the generate button is pressed</summary>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            // Extract audio data from input parameters
            byte[] audioData = ExtractAudioFromInput(input);
            if (audioData == null || audioData.Length == 0)
            {
                Logs.Warning("[STTBackend] No audio data found in input parameters");
                return [];
            }

            // Process transcription with options
            STTOptions options = GetSTTOptions(input);
            STTResponse result = await ProcessSTTAsync(audioData, GetSTTLanguage(input), options);

            // Create SwarmUI-compatible result
            return await CreateSTTResultAsync(result, input);
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error during STT processing: {ex.Message}");
            throw;
        }
    }

    /// <summary>Process audio through STT service using direct Python calls</summary>
    public static async Task<STTResponse> ProcessSTTAsync(byte[] audioData, string language, STTOptions options)
    {
        try
        {
            // Convert audio to base64 for Python processing
            string audioBase64 = Convert.ToBase64String(audioData);

            // Prepare STT request
            STTRequest request = new()
            {
                AudioData = audioBase64,
                Language = language,
                Options = new STTOptions
                {
                    ReturnConfidence = options.ReturnConfidence,
                    ReturnAlternatives = options.ReturnAlternatives,
                    ModelPreference = options.ModelPreference,
                    CustomOptions = options.CustomOptions
                }
            };

            // Call STT service directly
            JObject response = await PythonVoiceProcessor.Instance.ProcessSTTAsync(request);

            if (response["success"]?.Value<bool>() != true)
            {
                string error = response["error"]?.ToString() ?? "Unknown STT error";
                throw new Exception($"STT processing failed: {error}");
            }

            // Parse STT response
            return new STTResponse
            {
                Success = true,
                Transcription = response["transcription"]?.ToString() ?? string.Empty,
                Confidence = response["confidence"]?.Value<float>() ?? 0.0f,
                Language = response["language"]?.ToString() ?? language,
                ProcessingTime = response["processing_time"]?.Value<double>() ?? 0.0,
                Alternatives = response["alternatives"]?.ToObject<string[]>() ?? [],
                Metadata = response["metadata"]?.ToObject<Dictionary<string, object>>() ?? []
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] STT processing error: {ex.Message}");
            throw new InvalidOperationException($"STT processing failed: {ex.Message}", ex);
        }
    }

    /// <summary>Extract audio data from SwarmUI input parameters</summary>
    public byte[] ExtractAudioFromInput(T2IParamInput input)
    {
        try
        {
            // Look for audio data in various possible parameter locations
            if (input.TryGet(VoiceParameters.AudioInput, out object audioParam))
            {
                if (audioParam is byte[] audioBytes)
                    return audioBytes;
                if (audioParam is string audioBase64)
                    return Convert.FromBase64String(audioBase64);
            }

            // Check for file path in init image parameter (repurposed for audio)
            if (input.TryGet(T2IParamTypes.InitImage, out Image initImage) && initImage != null)
            {
                // Convert image back to audio file if it was stored as image metadata
                return ExtractAudioFromImage(initImage);
            }

            // Check for audio file in user data directory
            string audioFile = GetLatestUserAudioFile();
            if (audioFile != null && File.Exists(audioFile))
            {
                return File.ReadAllBytes(audioFile);
            }

            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error extracting audio from input: {ex.Message}");
            return null;
        }
    }

    /// <summary>Get STT language from input parameters</summary>
    public string GetSTTLanguage(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.STTLanguage, out string language)
            ? language
            : ServiceConfiguration.DefaultLanguage;
    }

    /// <summary>Get STT processing options from input parameters</summary>
    public STTOptions GetSTTOptions(T2IParamInput input)
    {
        return new STTOptions
        {
            ReturnConfidence = input.TryGet(VoiceParameters.STTReturnConfidence, out bool confidence) && confidence,
            ReturnAlternatives = input.TryGet(VoiceParameters.STTReturnAlternatives, out bool alternatives) && alternatives,
            ModelPreference = input.TryGet(VoiceParameters.STTModelPreference, out string preference) ? preference : "accuracy"
        };
    }

    /// <summary>Create SwarmUI-compatible result from STT transcription</summary>
    public async Task<Image[]> CreateSTTResultAsync(STTResponse sttResult, T2IParamInput input)
    {
        try
        {
            // Save transcription as text file
            await SaveTranscriptionAsync(sttResult, input);

            // STT produces text, not images/media. Results are saved to disk and returned via API.
            // TODO: Return proper media output once SwarmUI supports non-image results from Generate()
            return [];
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error creating STT result: {ex.Message}");
            throw;
        }
    }

    /// <summary>Create a text result for STT transcription display.
    /// Returns null because STT results are text, not images. Results are saved to disk via SaveTranscriptionAsync.</summary>
    public Task<Image> CreateTranscriptionImageAsync(STTResponse sttResult)
    {
        // STT produces text output, not media. The transcription is saved to disk separately.
        // TODO: Return proper media output once SwarmUI supports non-image results from Generate()
        Logs.Info($"[STTBackend] Transcription result: {sttResult.Transcription} (confidence: {sttResult.Confidence:P1})");
        return Task.FromResult<Image>(null);
    }

    /// <summary>Save transcription result to file system</summary>
    public static async Task SaveTranscriptionAsync(STTResponse sttResult, T2IParamInput input)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string filename = $"transcription_{timestamp}.json";
            string outputPath = Path.Combine(ServiceConfiguration.ExtensionDirectory, "outputs", filename);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            object outputData = new
            {
                timestamp = DateTime.UtcNow,
                transcription = sttResult.Transcription,
                confidence = sttResult.Confidence,
                language = sttResult.Language,
                processing_time = sttResult.ProcessingTime,
                alternatives = sttResult.Alternatives,
                metadata = sttResult.Metadata
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(outputData, Newtonsoft.Json.Formatting.Indented);
            await File.WriteAllTextAsync(outputPath, json);

            Logs.Info($"[STTBackend] Transcription saved to: {outputPath}");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[STTBackend] Failed to save transcription: {ex.Message}");
        }
    }

    /// <summary>Extract audio data from image metadata (if stored there)</summary>
    public byte[] ExtractAudioFromImage(Image image)
    {
        try
        {
            // TODO: Implement proper audio extraction logic based on how audio is stored in image metadata
            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[STTBackend] Failed to extract audio from image: {ex.Message}");
            return null;
        }
    }

    /// <summary>Get the latest audio file from user data directory</summary>
    public static string GetLatestUserAudioFile()
    {
        try
        {
            string audioDir = Path.Combine(ServiceConfiguration.ExtensionDirectory, "audio_input");
            if (!Directory.Exists(audioDir))
                return null;

            return Directory.GetFiles(audioDir, "*.wav")
                .Concat(Directory.GetFiles(audioDir, "*.mp3"))
                .Concat(Directory.GetFiles(audioDir, "*.webm"))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Free memory and resources</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            // Cleanup Python resources
            JObject result = await PythonVoiceProcessor.Instance.CleanupAsync();
            return result["success"]?.Value<bool>() ?? false;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[STTBackend] Failed to free memory: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shutdown the STT backend</summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info("[STTBackend] Shutting down STT backend");

            // Stop the STT backend service
            await StopAsync();

            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error during shutdown: {ex.Message}");
        }
    }

    #region Service Management Methods

    /// <summary>Ensures STT dependencies are available.</summary>
    /// <returns>True if dependencies are ready</returns>
    private async Task<bool> EnsureDependenciesAsync()
    {
        try
        {
            // Detect Python environment
            PythonEnvironmentInfo pythonInfo = _dependencyInstaller.DetectPythonEnvironment();
            if (pythonInfo?.IsValid != true)
            {
                Logs.Error("[STTBackend] Python environment not detected");
                return false;
            }

            // Check if dependencies are already installed
            bool dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.STT);

            if (!dependenciesInstalled)
            {
                Logs.Info("[STTBackend] STT dependencies not installed. Attempting to install them now...");
                try
                {
                    // Attempt to install the missing dependencies
                    bool installSuccess = await _dependencyInstaller.InstallDependenciesAsync(pythonInfo, ServiceConfiguration.BackendType.STT);
                    if (!installSuccess)
                    {
                        Logs.Warning("[STTBackend] STT dependencies installation failed - voice transcription will not be available");
                        Logs.Info("[STTBackend] Install STT dependencies using the SwarmUI extension manager or manually install RealtimeSTT");
                        return false;
                    }
                    
                    // Verify that installation was successful - force a refresh of the cached package list
                    dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.STT, forceRefresh: true);
                    if (!dependenciesInstalled)
                    {
                        Logs.Warning("[STTBackend] STT dependencies verification failed after installation - voice transcription may not work correctly");
                        return false;
                    }
                    
                    Logs.Info("[STTBackend] Successfully installed STT dependencies");
                }
                catch (Exception ex)
                {
                    Logs.Error($"[STTBackend] Error installing STT dependencies: {ex.Message}");
                    Logs.Info("[STTBackend] Install STT dependencies using the SwarmUI extension manager or manually install RealtimeSTT");
                    return false;
                }
            }

            Logs.Info("[STTBackend] STT dependencies verified");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error checking STT dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>Initialize the Python voice processor.</summary>
    /// <returns>True if initialization succeeded</returns>
    public async Task<bool> InitializePythonProcessorAsync()
    {
        try
        {
            // Initialize the processor
            bool initialized = await PythonVoiceProcessor.Instance.InitializeAsync();
            if (!initialized)
            {
                Logs.Error("[STTBackend] Failed to initialize Python voice processor");
                return false;
            }

            // Initialize voice services with STT-specific config
            JObject config = new()
            {
                ["stt_engine"] = "realtimestt"
            };

            JObject result = await PythonVoiceProcessor.Instance.InitializeVoiceServicesAsync(config);

            if (result["success"]?.Value<bool>() != true)
            {
                string error = result["error"]?.ToString() ?? "Unknown initialization error";
                Logs.Error($"[STTBackend] Voice services initialization failed: {error}");
                return false;
            }

            // Check if STT is available
            bool sttAvailable = await PythonVoiceProcessor.Instance.IsSTTAvailableAsync();
            if (!sttAvailable)
            {
                Logs.Warning("[STTBackend] STT service not available in Python processor");
                return false;
            }

            Logs.Info("[STTBackend] Python voice processor initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error initializing Python processor: {ex.Message}");
            return false;
        }
    }

    #endregion
}
