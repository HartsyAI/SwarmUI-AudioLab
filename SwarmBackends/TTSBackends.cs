using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Diagnostics;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.SwarmBackends;

/// <summary>Text-to-Speech backend for SwarmUI integration using direct Python function calls. Processes text input through TTS service and returns audio results. Also serves as the TTS service for API endpoints.</summary>
public class TTSBackend : VoiceAssistantBackends
{
    /// <summary>Configuration settings for Voice Assistant Backends</summary>
    public class TTSBackendSettings : AutoConfiguration
    {
        /// <summary>Debug mode setting</summary>
        [AutoConfiguration.ConfigComment("Enable debug logging for voice processing")]
        public bool DebugMode = false;

        /// <summary>Preferred TTS engine</summary>
        [AutoConfiguration.ConfigComment("Preferred TTS engine (chatterbox, bark, etc.)")]
        public string PreferredEngine = "chatterbox";
    }

    public static readonly Lazy<TTSBackend> InstanceLazy = new(() => new TTSBackend());
    public static TTSBackend Instance => InstanceLazy.Value;

    private readonly object _serviceLock = new();
    private readonly DependencyInstaller _dependencyInstaller;
    private bool _isStarted = false;
    private bool _isInitialized = false;

    /// <summary>Backend type identifier</summary>
    protected override ServiceConfiguration.BackendType BackendType => ServiceConfiguration.BackendType.TTS;

    /// <summary>Supported features for TTS processing</summary>
    public override IEnumerable<string> SupportedFeatures =>
    [
        "text_to_speech",
        "voice_cloning",
        "multi_language",
        "emotional_synthesis",
        "volume_control",
        "speed_control"
    ];

    /// <summary>Gets whether the TTS backend is currently running</summary>
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
    public TTSBackend()
    {
        _dependencyInstaller = new DependencyInstaller();
        Logs.Debug("[TTSBackend] TTS backend instance created");
    }

    /// <summary>Initialize the TTS backend</summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[TTSBackend] Initializing Text-to-Speech backend");

            // Start the TTS backend service (handles dependencies, installation, and process startup)
            BackendStatusResponse startResult = await StartAsync();

            if (!startResult.Success)
            {
                Logs.Warning($"[TTSBackend] TTS backend service failed to start: {startResult.Message}");
                Status = BackendStatus.ERRORED;
                return;
            }

            Status = BackendStatus.RUNNING;
            Logs.Info("[TTSBackend] TTS backend initialized successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Failed to initialize: {ex.Message}");
            Status = BackendStatus.ERRORED;
            throw;
        }
    }

    /// <summary>Starts the TTS backend with dependency checking and Python initialization.</summary>
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
                    Message = "TTS backend already running",
                    Status = "running",
                    BackendType = "TTS",
                    IsRunning = true
                };
            }
        }

        try
        {
            Logs.Info("[TTSBackend] Starting TTS backend service");

            // Step 1: Check dependencies
            bool dependenciesReady = await EnsureDependenciesAsync();
            if (!dependenciesReady)
            {
                return new BackendStatusResponse
                {
                    Success = false,
                    Message = "TTS dependencies not available",
                    Status = "error",
                    BackendType = "TTS"
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
                    BackendType = "TTS"
                };
            }

            lock (_serviceLock)
            {
                _isStarted = true;
                _isInitialized = true;
            }

            Logs.Info("[TTSBackend] TTS backend started successfully");

            // Run a test TTS generation after successful initialization
            _ = PerformPostInitTTSTestAsync();

            return new BackendStatusResponse
            {
                Success = true,
                Message = "TTS backend started successfully",
                Status = "running",
                BackendType = "TTS",
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

            Logs.Error($"[TTSBackend] Failed to start TTS backend: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"TTS backend startup failed: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Stops the TTS backend gracefully.</summary>
    /// <returns>Backend status response</returns>
    public async Task<BackendStatusResponse> StopAsync()
    {
        try
        {
            Logs.Info("[TTSBackend] Stopping TTS backend service");

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
                Message = "TTS backend stopped successfully",
                Status = "stopped",
                BackendType = "TTS",
                IsRunning = false
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Failed to stop TTS backend: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to stop TTS backend: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Gets the current status of the TTS backend.</summary>
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
                BackendType = "TTS",
                IsRunning = isRunning
            };
            // Add health information if running
            if (isRunning)
            {
                try
                {
                    bool isHealthy = await PythonVoiceProcessor.Instance.IsTTSAvailableAsync();
                    response.IsHealthy = isHealthy;
                    response.Message = isHealthy ? "TTS backend running and healthy" : "TTS backend running but unhealthy";
                }
                catch (Exception ex)
                {
                    response.IsHealthy = false;
                    response.Message = $"TTS backend status check failed: {ex.Message}";
                }
            }
            else
            {
                response.Message = "TTS backend not running";
            }
            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Failed to get TTS backend status: {ex.Message}");

            return new BackendStatusResponse
            {
                Success = false,
                Message = $"Failed to get TTS backend status: {ex.Message}",
                Status = "error",
                BackendType = "TTS",
                ErrorDetails = ex.ToString()
            };
        }
    }

    /// <summary>Load TTS model/voice configuration</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[TTSBackend] Loading TTS model: {model.Name}");
            // Validate model compatibility
            if (!IsModelCompatible(model))
            {
                Logs.Error($"[TTSBackend] Model not compatible: {model.Name}");
                return false;
            }
            CurrentModelName = model.Name;
            Logs.Info($"[TTSBackend] Successfully loaded TTS voice: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error loading model {model.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Process text input and generate speech audio</summary>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            Logs.Info("[TTSBackend] Processing TTS generation request");
            // Extract text from input (use prompt as text input)
            string text = ExtractTextFromInput(input);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("No text provided for TTS processing");
            }
            // Get TTS parameters
            string voice = GetTTSVoice(input);
            string language = GetTTSLanguage(input);
            float volume = GetTTSVolume(input);
            TTSOptions options = GetTTSOptions(input);
            // Process through TTS service
            TTSResponse ttsResult = await ProcessTTSAsync(text, voice, language, volume, options);
            // Convert TTS result to SwarmUI output format
            Image[] result = await CreateTTSResultAsync(ttsResult, input, text);
            Logs.Info($"[TTSBackend] TTS processing completed: {text.Length} chars -> {ttsResult.AudioData?.Length ?? 0} bytes");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] TTS generation failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Process text through TTS service using direct Python calls</summary>
    public async Task<TTSResponse> ProcessTTSAsync(string text, string voice, string language, float volume, TTSOptions options)
    {
        try
        {
            // Prepare TTS request
            TTSRequest request = new()
            {
                Text = text,
                Voice = voice,
                Language = language,
                Volume = volume,
                Options = options // Direct assignment if types match
            };
            // Call TTS service directly
            JObject response = await PythonVoiceProcessor.Instance.ProcessTTSAsync(request);
            if (response["success"]?.Value<bool>() != true)
            {
                string error = response["error"]?.ToString() ?? "Unknown TTS error";
                throw new Exception($"TTS processing failed: {error}");
            }
            // Parse TTS response
            TTSResponse result = new()
            {
                Success = true,
                AudioData = response["audio_data"]?.ToString() ?? string.Empty,
                Text = response["text"]?.ToString() ?? text,
                Voice = response["voice"]?.ToString() ?? voice,
                Language = response["language"]?.ToString() ?? language,
                Volume = response["volume"]?.Value<float>() ?? volume,
                Duration = response["duration"]?.Value<double>() ?? 0.0,
                ProcessingTime = response["processing_time"]?.Value<double>() ?? 0.0,
                Metadata = response["metadata"]?.ToObject<Dictionary<string, object>>() ?? []
            };
            if (string.IsNullOrEmpty(result.AudioData))
            {
                throw new InvalidOperationException("TTS service did not return valid audio data");
            }
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] TTS processing error: {ex.Message}");
            throw new InvalidOperationException($"TTS processing failed: {ex.Message}", ex);
        }
    }

    /// <summary>Extract text from SwarmUI input parameters</summary>
    private static string ExtractTextFromInput(T2IParamInput input)
    {
        try
        {
            // Primary: Check for TTS-specific text parameter
            if (input.TryGet(VoiceParameters.TTSText, out string ttsText) && !string.IsNullOrWhiteSpace(ttsText))
            {
                return ttsText.Trim();
            }
            // Secondary: Use main prompt as text input
            if (input.TryGet(T2IParamTypes.Prompt, out string prompt) && !string.IsNullOrWhiteSpace(prompt))
            {
                return prompt.Trim();
            }
            // Tertiary: Check negative prompt as fallback
            if (input.TryGet(T2IParamTypes.NegativePrompt, out string negPrompt) && !string.IsNullOrWhiteSpace(negPrompt))
            {
                return negPrompt.Trim();
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[TTSBackend] Error extracting text from input: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Get TTS voice from input parameters</summary>
    private string GetTTSVoice(T2IParamInput input)
    {
        // Check for voice parameter, fallback to current model, then default
        if (input.TryGet(VoiceParameters.TTSVoice, out string voice) && !string.IsNullOrWhiteSpace(voice))
        {
            return voice;
        }

        return GetVoiceFromModel(CurrentModelName) ?? ServiceConfiguration.DefaultVoice;
    }

    /// <summary>Get TTS language from input parameters</summary>
    public string GetTTSLanguage(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.TTSLanguage, out string language)
            ? language
            : ServiceConfiguration.DefaultLanguage;
    }

    /// <summary>Get TTS volume from input parameters</summary>
    public float GetTTSVolume(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.TTSVolume, out float volume)
            ? Math.Clamp(volume, 0.0f, 1.0f)
            : ServiceConfiguration.DefaultVolume;
    }

    /// <summary>Get TTS processing options from input parameters</summary>
    public TTSOptions GetTTSOptions(T2IParamInput input)
    {
        return new TTSOptions
        {
            Speed = input.TryGet(VoiceParameters.TTSSpeed, out float speed) ? speed : 1.0f,
            Pitch = input.TryGet(VoiceParameters.TTSPitch, out float pitch) ? pitch : 1.0f,
            Format = input.TryGet(VoiceParameters.TTSFormat, out string format) ? format : "wav"
        };
    }

    /// <summary>Create SwarmUI-compatible result from TTS audio</summary>
    public async Task<Image[]> CreateTTSResultAsync(TTSResponse ttsResult, T2IParamInput input, string originalText)
    {
        try
        {
            // Save audio file to disk
            string audioFilePath = await SaveAudioFileAsync(ttsResult, originalText);

            // Log metadata for the generated audio
            LogAudioMetadata(ttsResult, audioFilePath, originalText);

            // TTS produces audio, not images. Audio is saved to disk and returned via API.
            // TODO: Return proper media output once SwarmUI supports non-image results from Generate()
            return [];
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error creating TTS result: {ex.Message}");
            throw;
        }
    }

    /// <summary>Save TTS audio data to file system</summary>
    public async Task<string> SaveAudioFileAsync(TTSResponse ttsResult, string originalText)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safeText = string.Join("", originalText.Take(50).Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))).Trim().Replace(" ", "_");
            string filename = $"tts_{timestamp}_{safeText}.wav";
            string outputDir = Path.Combine(ServiceConfiguration.ExtensionDirectory, "outputs", "audio");
            string outputPath = Path.Combine(outputDir, filename);

            Directory.CreateDirectory(outputDir);

            // Convert base64 audio back to bytes and save
            byte[] audioBytes = Convert.FromBase64String(ttsResult.AudioData);
            await File.WriteAllBytesAsync(outputPath, audioBytes);

            // Also save metadata
            string metadataPath = Path.ChangeExtension(outputPath, ".json");
            object metadata = new
            {
                timestamp = DateTime.UtcNow,
                original_text = originalText,
                voice = ttsResult.Voice,
                language = ttsResult.Language,
                volume = ttsResult.Volume,
                duration = ttsResult.Duration,
                processing_time = ttsResult.ProcessingTime,
                audio_file = filename,
                metadata = ttsResult.Metadata
            };

            string metadataJson = Newtonsoft.Json.JsonConvert.SerializeObject(metadata, Newtonsoft.Json.Formatting.Indented);
            await File.WriteAllTextAsync(metadataPath, metadataJson);

            Logs.Info($"[TTSBackend] Audio saved to: {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Failed to save audio file: {ex.Message}");
            throw;
        }
    }

    /// <summary>Log audio metadata for the TTS result. Audio is saved to disk separately.</summary>
    private static void LogAudioMetadata(TTSResponse ttsResult, string audioFilePath, string originalText)
    {
        Logs.Info($"[TTSBackend] TTS Generated Audio - Text: {originalText}, Voice: {ttsResult.Voice}, " +
                  $"Language: {ttsResult.Language}, Duration: {ttsResult.Duration:F2}s, " +
                  $"Volume: {ttsResult.Volume:P0}, File: {Path.GetFileName(audioFilePath)}");
    }

    /// <summary>Map model name to voice identifier</summary>
    public string GetVoiceFromModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return ServiceConfiguration.DefaultVoice;

        string modelLower = modelName.ToLowerInvariant();

        // Map common voice model names
        if (modelLower.Contains("default")) return "default";
        if (modelLower.Contains("expressive")) return "expressive";
        if (modelLower.Contains("calm")) return "calm";
        if (modelLower.Contains("dramatic")) return "dramatic";
        if (modelLower.Contains("male")) return "male";
        if (modelLower.Contains("female")) return "female";
        if (modelLower.Contains("neural")) return "neural";

        // If model name doesn't contain recognized voice, return it as-is
        return modelName;
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
            Logs.Warning($"[TTSBackend] Failed to free memory: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shutdown the TTS backend</summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info("[TTSBackend] Shutting down TTS backend");

            // Stop the TTS backend service
            await StopAsync();

            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error during shutdown: {ex.Message}");
        }
    }

    #region Service Management Methods

    /// <summary>Ensures TTS dependencies are available.</summary>
    /// <returns>True if dependencies are ready</returns>
    public async Task<bool> EnsureDependenciesAsync()
    {
        try
        {
            // Detect Python environment
            PythonEnvironmentInfo pythonInfo = _dependencyInstaller.DetectPythonEnvironment();
            if (pythonInfo?.IsValid != true)
            {
                Logs.Error("[TTSBackend] Python environment not detected");
                return false;
            }
            // Check if dependencies are already installed
            bool dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.TTS);
            if (!dependenciesInstalled)
            {
                Logs.Info("[TTSBackend] TTS dependencies not installed. Attempting to install them now...");
                try
                {
                    // Attempt to install the missing dependencies
                    bool installSuccess = await _dependencyInstaller.InstallDependenciesAsync(pythonInfo, ServiceConfiguration.BackendType.TTS);
                    if (!installSuccess)
                    {
                        Logs.Warning("[TTSBackend] TTS dependencies installation failed - voice synthesis will not be available");
                        Logs.Info("[TTSBackend] Install TTS dependencies using the SwarmUI extension manager or manually install Chatterbox TTS");
                        return false;
                    }
                    
                    // Verify that installation was successful - force a refresh of the cached package list
                    dependenciesInstalled = await _dependencyInstaller.CheckDependenciesInstalledAsync(pythonInfo, ServiceConfiguration.BackendType.TTS, forceRefresh: true);
                    if (!dependenciesInstalled)
                    {
                        Logs.Warning("[TTSBackend] TTS dependencies verification failed after installation - voice synthesis may not work correctly");
                        return false;
                    }
                    
                    Logs.Info("[TTSBackend] Successfully installed TTS dependencies");
                }
                catch (Exception ex)
                {
                    Logs.Error($"[TTSBackend] Error installing TTS dependencies: {ex.Message}");
                    Logs.Info("[TTSBackend] Install TTS dependencies using the SwarmUI extension manager or manually install Chatterbox TTS");
                    return false;
                }
            }
            Logs.Info("[TTSBackend] TTS dependencies verified");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error checking TTS dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>Perform a test TTS generation after initialization to verify the service is working.</summary>
    /// <returns>Result of the TTS test</returns>
    public async Task<bool> PerformPostInitTTSTestAsync()
    {
        try
        {
            string testPhrase = "This is a test of the TTS service. If you are hearing this everything is working!";
            Logs.Info($"[TTSBackend] Running post-initialization TTS test with phrase: '{testPhrase}'");
            
            // Create a progress tracker for this operation
            var tracker = ProgressTracking.GetJobTracker("post_init_test");
            tracker.UpdateProgress(0, $"Starting TTS test generation");
            
            // Create TTS request
            TTSRequest request = new()
            {
                Text = testPhrase,
                Voice = "default", // Use default voice
                Language = "en",
                Volume = 1.0f,
                Options = new TTSOptions
                {
                    Speed = 1.0f,
                    Pitch = 1.0f
                }
            };
            
            tracker.UpdateProgress(20, $"TTS request created, sending to voice processor");
            Logs.Debug($"[TTSBackend] Sending test TTS request to voice processor: {testPhrase}");
            
            // Call TTS service
            JObject response = await PythonVoiceProcessor.Instance.ProcessTTSAsync(request);
            tracker.UpdateProgress(60, $"TTS processing complete, checking response");
            
            if (response["success"]?.Value<bool>() != true)
            {
                string error = response["error"]?.ToString() ?? "Unknown error";
                Logs.Error($"[TTSBackend] Post-initialization TTS test failed: {error}");
                tracker.SetError($"TTS test failed: {error}");
                return false;
            }
            
            // Extract audio data from response
            string audioBase64 = response["audio_data"]?.Value<string>();
            int audioLength = 0;
            if (!string.IsNullOrEmpty(audioBase64))
            {
                byte[] audioData = Convert.FromBase64String(audioBase64);
                audioLength = audioData.Length;
                tracker.UpdateProgress(80, $"TTS audio generated: {audioLength} bytes");
                
                // Save audio to temp file for playback
                string tempDirectory = Path.Combine(Path.GetTempPath(), "SwarmUI-TTS-Tests");
                Directory.CreateDirectory(tempDirectory);
                string tempAudioFile = Path.Combine(tempDirectory, "tts_test.wav");
                
                try
                {
                    // Write audio data to file
                    await File.WriteAllBytesAsync(tempAudioFile, audioData);
                    Logs.Info($"[TTSBackend] Test audio saved to: {tempAudioFile}");
                    tracker.UpdateProgress(90, $"Audio saved to file, attempting playback");
                    
                    // Play the audio file
                    await PlayAudioFileAsync(tempAudioFile, tracker);
                }
                catch (Exception ex)
                {
                    // Don't fail the test if just the audio playback fails
                    Logs.Warning($"[TTSBackend] Unable to play test audio file: {ex.Message}");
                    Logs.Debug($"[TTSBackend] Audio playback exception: {ex}");
                }
            }
            
            Logs.Info($"[TTSBackend] Post-initialization TTS test successful! Generated {audioLength} bytes of audio data");
            Logs.Info($"[TTSBackend] TTS service is ready for use");
            tracker.SetComplete($"TTS test successful: {audioLength} bytes generated");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Post-initialization TTS test failed with exception: {ex.Message}");
            Logs.Debug($"[TTSBackend] Exception details: {ex}");
            ProgressTracking.GetJobTracker("post_init_test")
                .SetError($"TTS test failed with exception: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>Play an audio file using the system's default audio player</summary>
    /// <param name="audioFilePath">Path to the audio file to play</param>
    /// <param name="tracker">Optional progress tracker to update</param>
    /// <returns>Result of the playback attempt</returns>
    private async Task<bool> PlayAudioFileAsync(string audioFilePath, ProgressTracker tracker = null)
    {
        try
        {
            Logs.Info($"[TTSBackend] Playing audio file: {audioFilePath}");
            tracker?.UpdateProgress(95, "Playing audio file");

            // No direct SoundPlayer available, use Windows command to play the wav file
            ProcessStartInfo processStartInfo = new()
            {
                FileName = "powershell",
                Arguments = $"-c (New-Object Media.SoundPlayer '{audioFilePath}').PlaySync()",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            // Start the process to play the audio
            using Process process = Process.Start(processStartInfo);
            // Don't wait for completion - non-blocking
            // Log the playback
            Logs.Info($"[TTSBackend] Audio playback started for: {audioFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error playing audio file: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>Initialize the Python voice processor for TTS.</summary>
    /// <returns>True if initialization succeeded</returns>
    public async Task<bool> InitializePythonProcessorAsync()
    {
        try
        {
            // Initialize the processor
            bool initialized = await PythonVoiceProcessor.Instance.InitializeAsync();
            if (!initialized)
            {
                Logs.Error("[TTSBackend] Failed to initialize Python voice processor");
                return false;
            }
            // Initialize voice services with TTS-specific config
            JObject config = new()
            {
                ["tts_engine"] = "chatterbox"
            };
            JObject result = await PythonVoiceProcessor.Instance.InitializeVoiceServicesAsync(config);
            if (result["success"]?.Value<bool>() != true)
            {
                string error = result["error"]?.ToString() ?? "Unknown initialization error";
                Logs.Error($"[TTSBackend] Voice services initialization failed: {error}");
                return false;
            }
            // Check if TTS is available
            bool ttsAvailable = await PythonVoiceProcessor.Instance.IsTTSAvailableAsync();
            if (!ttsAvailable)
            {
                Logs.Warning("[TTSBackend] TTS service not available in Python processor");
                return false;
            }
            Logs.Info("[TTSBackend] Python voice processor initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error initializing Python processor: {ex.Message}");
            return false;
        }
    }

    #endregion
}
