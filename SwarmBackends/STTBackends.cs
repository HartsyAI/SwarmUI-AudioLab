using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Hartsy.Extensions.VoiceAssistant.SwarmBackends;

/// <summary>Speech-to-Text backend for SwarmUI integration.
/// Processes audio input through STT service and returns transcription results.</summary>
public class STTBackend : VoiceAssistantBackends
{
    /// <summary>Backend type identifier</summary>
    public override string BackendType => "STT";

    /// <summary>Supported features for STT processing</summary>
    public override IEnumerable<string> SupportedFeatures =>
    [
        "transcription",
        "language_detection",
        "confidence_scoring",
        "real_time_processing"
    ];

    /// <summary>Initialize the STT backend</summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[STTBackend] Initializing Speech-to-Text backend");

            // Ensure Python backend service is running
            await EnsureBackendServiceAsync();

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

    /// <summary>Load STT model - maps to language/engine configuration</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[STTBackend] Loading STT model: {model.Name}");

            // Map model name to STT configuration
            string language = GetLanguageFromModel(model.Name);
            string engine = GetEngineFromModel(model.Name);

            // Validate model availability with Python backend
            var isAvailable = await ValidateModelAsync(engine, language);
            if (!isAvailable)
            {
                Logs.Error($"[STTBackend] Model not available: {model.Name}");
                return false;
            }

            CurrentModelName = model.Name;
            Logs.Info($"[STTBackend] Successfully loaded STT model: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error loading model {model.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Process audio input and generate transcription</summary>
    public override async Task<Image[]> Generate(T2IParamInput input)
    {
        try
        {
            Logs.Info("[STTBackend] Processing STT generation request");

            // Extract audio data from input - this is where audio would be provided
            var audioData = ExtractAudioFromInput(input);
            if (audioData == null || audioData.Length == 0)
            {
                throw new ArgumentException("No audio data provided for STT processing");
            }

            // Get STT parameters
            string language = GetSTTLanguage(input);
            var options = GetSTTOptions(input);

            // Process through STT service
            var transcriptionResult = await ProcessSTTAsync(audioData, language, options);

            // Convert transcription result to SwarmUI output format
            var result = await CreateSTTResultAsync(transcriptionResult, input);

            Logs.Info($"[STTBackend] STT processing completed: '{transcriptionResult.Transcription}'");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] STT generation failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Process audio through STT service</summary>
    private async Task<STTResult> ProcessSTTAsync(byte[] audioData, string language, STTOptions options)
    {
        try
        {
            // Convert audio to base64 for Python backend
            string audioBase64 = Convert.ToBase64String(audioData);

            // Prepare request payload
            var requestPayload = new
            {
                audio_data = audioBase64,
                language = language,
                options = new
                {
                    return_confidence = options.ReturnConfidence,
                    return_alternatives = options.ReturnAlternatives,
                    model_preference = options.ModelPreference,
                    custom = options.CustomOptions
                }
            };

            // Call Python STT service
            string requestJson = Newtonsoft.Json.JsonConvert.SerializeObject(requestPayload);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync($"{ServiceConfiguration.BackendUrl}/stt/transcribe", content);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            var responseData = JObject.Parse(responseJson);

            // Parse STT response
            return new STTResult
            {
                Success = responseData["success"]?.Value<bool>() ?? false,
                Transcription = responseData["transcription"]?.ToString() ?? string.Empty,
                Confidence = responseData["confidence"]?.Value<float>() ?? 0.0f,
                Language = responseData["language"]?.ToString() ?? language,
                ProcessingTime = responseData["processing_time"]?.Value<double>() ?? 0.0,
                Alternatives = responseData["alternatives"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                Metadata = responseData["metadata"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] STT processing error: {ex.Message}");
            throw new InvalidOperationException($"STT processing failed: {ex.Message}", ex);
        }
    }

    /// <summary>Extract audio data from SwarmUI input parameters</summary>
    private byte[] ExtractAudioFromInput(T2IParamInput input)
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
            var audioFile = GetLatestUserAudioFile();
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
    private string GetSTTLanguage(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.STTLanguage, out string language)
            ? language
            : ServiceConfiguration.DefaultLanguage;
    }

    /// <summary>Get STT processing options from input parameters</summary>
    private STTOptions GetSTTOptions(T2IParamInput input)
    {
        return new STTOptions
        {
            ReturnConfidence = input.TryGet(VoiceParameters.STTReturnConfidence, out bool confidence) && confidence,
            ReturnAlternatives = input.TryGet(VoiceParameters.STTReturnAlternatives, out bool alternatives) && alternatives,
            ModelPreference = input.TryGet(VoiceParameters.STTModelPreference, out string preference) ? preference : "accuracy"
        };
    }

    /// <summary>Create SwarmUI-compatible result from STT transcription</summary>
    private async Task<Image[]> CreateSTTResultAsync(STTResult sttResult, T2IParamInput input)
    {
        try
        {
            // Create a text image with the transcription result
            var resultImage = await CreateTranscriptionImageAsync(sttResult);

            // Also save transcription as text file
            await SaveTranscriptionAsync(sttResult, input);

            return new[] { resultImage };
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error creating STT result: {ex.Message}");
            throw;
        }
    }

    /// <summary>Create an image containing the transcription text for display</summary>
    private async Task<Image> CreateTranscriptionImageAsync(STTResult sttResult)
    {
        // Create a simple text image showing the transcription
        var textContent = $"Transcription ({sttResult.Confidence:P1} confidence):\n\n{sttResult.Transcription}";

        if (sttResult.Alternatives?.Length > 0)
        {
            textContent += "\n\nAlternatives:\n" + string.Join("\n", sttResult.Alternatives);
        }

        // Create a simple text-based image (you could enhance this with proper text rendering)
        var textBytes = Encoding.UTF8.GetBytes(textContent);
        var metadata = new Dictionary<string, object>
        {
            ["transcription"] = sttResult.Transcription,
            ["confidence"] = sttResult.Confidence,
            ["language"] = sttResult.Language,
            ["processing_time"] = sttResult.ProcessingTime,
            ["backend"] = "STT"
        };

        return new Image(textBytes, "transcription.txt", "text/plain", metadata);
    }

    /// <summary>Save transcription result to file system</summary>
    private async Task SaveTranscriptionAsync(STTResult sttResult, T2IParamInput input)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var filename = $"transcription_{timestamp}.json";
            var outputPath = Path.Combine(ServiceConfiguration.ExtensionDirectory, "outputs", filename);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var outputData = new
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

    /// <summary>Ensure the Python backend service is running and healthy</summary>
    private async Task EnsureBackendServiceAsync()
    {
        try
        {
            if (!PythonBackendService.Instance.IsBackendRunning)
            {
                Logs.Info("[STTBackend] Starting Python backend service");
                var startResult = await PythonBackendService.Instance.StartAsync();
                if (!startResult.Success)
                {
                    throw new InvalidOperationException($"Failed to start Python backend: {startResult.Message}");
                }
            }

            // Verify STT service is available
            var healthInfo = await PythonBackendService.Instance.GetHealthAsync();
            if (!healthInfo.IsHealthy || !healthInfo.Services.GetValueOrDefault("stt", false))
            {
                throw new InvalidOperationException("STT service is not available in Python backend");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Backend service check failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Validate that the specified model is available</summary>
    private async Task<bool> ValidateModelAsync(string engine, string language)
    {
        try
        {
            var response = await HttpClient.GetAsync($"{ServiceConfiguration.BackendUrl}/status");
            if (!response.IsSuccessStatusCode)
                return false;

            var statusJson = await response.Content.ReadAsStringAsync();
            var status = JObject.Parse(statusJson);

            return status["services"]?["stt"]?["available"]?.Value<bool>() ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Map model name to language code</summary>
    private string GetLanguageFromModel(string modelName)
    {
        // Extract language from model name (e.g., "whisper-large-en" -> "en-US")
        var modelLower = modelName.ToLowerInvariant();

        if (modelLower.Contains("-en")) return "en-US";
        if (modelLower.Contains("-es")) return "es-ES";
        if (modelLower.Contains("-fr")) return "fr-FR";
        if (modelLower.Contains("-de")) return "de-DE";
        if (modelLower.Contains("-it")) return "it-IT";
        if (modelLower.Contains("-pt")) return "pt-BR";
        if (modelLower.Contains("-ru")) return "ru-RU";
        if (modelLower.Contains("-ja")) return "ja-JP";
        if (modelLower.Contains("-ko")) return "ko-KR";
        if (modelLower.Contains("-zh")) return "zh-CN";

        return ServiceConfiguration.DefaultLanguage;
    }

    /// <summary>Map model name to STT engine</summary>
    private string GetEngineFromModel(string modelName)
    {
        var modelLower = modelName.ToLowerInvariant();

        if (modelLower.Contains("whisper")) return "whisper";
        if (modelLower.Contains("wav2vec")) return "wav2vec2";
        if (modelLower.Contains("deepspeech")) return "deepspeech";

        return "whisper"; // Default to whisper
    }

    /// <summary>Extract audio data from image metadata (if stored there)</summary>
    private byte[] ExtractAudioFromImage(Image image)
    {
        try
        {
            if (image.Metadata?.TryGetValue("audio_data", out object audioData) == true)
            {
                if (audioData is string base64Audio)
                    return Convert.FromBase64String(base64Audio);
                if (audioData is byte[] audioBytes)
                    return audioBytes;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[STTBackend] Failed to extract audio from image: {ex.Message}");
            return null;
        }
    }

    /// <summary>Get the latest audio file from user data directory</summary>
    private string GetLatestUserAudioFile()
    {
        try
        {
            var audioDir = Path.Combine(ServiceConfiguration.ExtensionDirectory, "audio_input");
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
            // Signal Python backend to free STT model memory if needed
            await HttpClient.PostAsync($"{ServiceConfiguration.BackendUrl}/stt/free_memory", null);
            return true;
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
            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[STTBackend] Error during shutdown: {ex.Message}");
        }
    }
}

/// <summary>STT processing result</summary>
public class STTResult
{
    public bool Success { get; set; }
    public string Transcription { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public string Language { get; set; } = string.Empty;
    public double ProcessingTime { get; set; }
    public string[] Alternatives { get; set; } = Array.Empty<string>();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>STT processing options</summary>
public class STTOptions
{
    public bool ReturnConfidence { get; set; } = true;
    public bool ReturnAlternatives { get; set; } = false;
    public string ModelPreference { get; set; } = "accuracy";
    public Dictionary<string, object> CustomOptions { get; set; } = new();
}
