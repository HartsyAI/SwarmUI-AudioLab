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

/// <summary>Text-to-Speech backend for SwarmUI integration.
/// Processes text input through TTS service and returns audio results.</summary>
public class TTSBackend : VoiceAssistantBackends
{
    /// <summary>Backend type identifier</summary>
    public override string BackendType => "TTS";

    /// <summary>Supported features for TTS processing</summary>
    public override IEnumerable<string> SupportedFeatures => new[]
    {
        "text_to_speech",
        "voice_cloning",
        "multi_language",
        "emotional_synthesis",
        "volume_control",
        "speed_control"
    };

    /// <summary>Initialize the TTS backend</summary>
    public override async Task Init()
    {
        try
        {
            Logs.Info("[TTSBackend] Initializing Text-to-Speech backend");

            // Ensure Python backend service is running
            await EnsureBackendServiceAsync();

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

    /// <summary>Load TTS model/voice configuration</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Info($"[TTSBackend] Loading TTS model: {model.Name}");

            // Map model name to voice configuration
            string voice = GetVoiceFromModel(model.Name);
            string language = GetLanguageFromModel(model.Name);

            // Validate model availability with Python backend
            var isAvailable = await ValidateVoiceAsync(voice, language);
            if (!isAvailable)
            {
                Logs.Error($"[TTSBackend] Voice not available: {model.Name}");
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
            var options = GetTTSOptions(input);

            // Process through TTS service
            var ttsResult = await ProcessTTSAsync(text, voice, language, volume, options);

            // Convert TTS result to SwarmUI output format
            var result = await CreateTTSResultAsync(ttsResult, input, text);

            Logs.Info($"[TTSBackend] TTS processing completed: {text.Length} chars -> {ttsResult.AudioData?.Length ?? 0} bytes");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] TTS generation failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Process text through TTS service</summary>
    private async Task<TTSResult> ProcessTTSAsync(string text, string voice, string language, float volume, TTSOptions options)
    {
        try
        {
            // Prepare request payload
            var requestPayload = new
            {
                text = text,
                voice = voice,
                language = language,
                volume = volume,
                options = new
                {
                    speed = options.Speed,
                    pitch = options.Pitch,
                    format = options.Format,
                    custom = options.CustomOptions
                }
            };

            // Call Python TTS service
            string requestJson = Newtonsoft.Json.JsonConvert.SerializeObject(requestPayload);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync($"{ServiceConfiguration.BackendUrl}/tts/synthesize", content);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            var responseData = JObject.Parse(responseJson);

            // Parse TTS response
            var result = new TTSResult
            {
                Success = responseData["success"]?.Value<bool>() ?? false,
                AudioData = !string.IsNullOrEmpty(responseData["audio_data"]?.ToString())
                    ? Convert.FromBase64String(responseData["audio_data"].ToString())
                    : null,
                Text = responseData["text"]?.ToString() ?? text,
                Voice = responseData["voice"]?.ToString() ?? voice,
                Language = responseData["language"]?.ToString() ?? language,
                Volume = responseData["volume"]?.Value<float>() ?? volume,
                Duration = responseData["duration"]?.Value<double>() ?? 0.0,
                ProcessingTime = responseData["processing_time"]?.Value<double>() ?? 0.0,
                Metadata = responseData["metadata"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
            };

            if (!result.Success || result.AudioData == null)
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
    private string GetTTSLanguage(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.TTSLanguage, out string language)
            ? language
            : ServiceConfiguration.DefaultLanguage;
    }

    /// <summary>Get TTS volume from input parameters</summary>
    private float GetTTSVolume(T2IParamInput input)
    {
        return input.TryGet(VoiceParameters.TTSVolume, out float volume)
            ? Math.Clamp(volume, 0.0f, 1.0f)
            : ServiceConfiguration.DefaultVolume;
    }

    /// <summary>Get TTS processing options from input parameters</summary>
    private TTSOptions GetTTSOptions(T2IParamInput input)
    {
        return new TTSOptions
        {
            Speed = input.TryGet(VoiceParameters.TTSSpeed, out float speed) ? speed : 1.0f,
            Pitch = input.TryGet(VoiceParameters.TTSPitch, out float pitch) ? pitch : 1.0f,
            Format = input.TryGet(VoiceParameters.TTSFormat, out string format) ? format : "wav"
        };
    }

    /// <summary>Create SwarmUI-compatible result from TTS audio</summary>
    private async Task<Image[]> CreateTTSResultAsync(TTSResult ttsResult, T2IParamInput input, string originalText)
    {
        try
        {
            // Save audio file to disk
            var audioFilePath = await SaveAudioFileAsync(ttsResult, originalText);

            // Create audio metadata image for SwarmUI display
            var metadataImage = await CreateAudioMetadataImageAsync(ttsResult, audioFilePath, originalText);

            // Create a proper audio file reference that SwarmUI can handle
            var audioImage = await CreateAudioImageAsync(ttsResult, audioFilePath);

            return [audioImage, metadataImage];
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error creating TTS result: {ex.Message}");
            throw;
        }
    }

    /// <summary>Save TTS audio data to file system</summary>
    private async Task<string> SaveAudioFileAsync(TTSResult ttsResult, string originalText) // TODO: This should call an imternal method
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeText = string.Join("", originalText.Take(50).Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))).Trim().Replace(" ", "_");
            var filename = $"tts_{timestamp}_{safeText}.wav";
            var outputDir = Path.Combine(ServiceConfiguration.ExtensionDirectory, "outputs", "audio");
            var outputPath = Path.Combine(outputDir, filename);

            Directory.CreateDirectory(outputDir);

            await File.WriteAllBytesAsync(outputPath, ttsResult.AudioData);

            // Also save metadata
            var metadataPath = Path.ChangeExtension(outputPath, ".json");
            var metadata = new
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

    /// <summary>Create an image containing audio metadata for display</summary>
    private static async Task<Image> CreateAudioMetadataImageAsync(TTSResult ttsResult, string audioFilePath, string originalText)
    {
        try
        {
            var metadataContent = $"🎵 Text-to-Speech Generated Audio\n\n" +
                                $"Text: {originalText}\n" +
                                $"Voice: {ttsResult.Voice}\n" +
                                $"Language: {ttsResult.Language}\n" +
                                $"Duration: {ttsResult.Duration:F2} seconds\n" +
                                $"Volume: {ttsResult.Volume:P0}\n" +
                                $"Processing Time: {ttsResult.ProcessingTime:F3}s\n" +
                                $"Audio File: {Path.GetFileName(audioFilePath)}";

            var metadataBytes = Encoding.UTF8.GetBytes(metadataContent);
            var metadata = new Dictionary<string, object>
            {
                ["type"] = "tts_metadata",
                ["original_text"] = originalText,
                ["voice"] = ttsResult.Voice,
                ["language"] = ttsResult.Language,
                ["duration"] = ttsResult.Duration,
                ["volume"] = ttsResult.Volume,
                ["processing_time"] = ttsResult.ProcessingTime,
                ["audio_file"] = audioFilePath,
                ["backend"] = "TTS"
            };

            return new Image(metadataBytes, "tts_metadata.txt", "text/plain", metadata);
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error creating metadata image: {ex.Message}");
            throw;
        }
    }

    /// <summary>Create an image that represents the audio file for SwarmUI</summary>
    private static async Task<Image> CreateAudioImageAsync(TTSResult ttsResult, string audioFilePath)
    {
        try
        {
            // Create a simple waveform visualization or audio icon
            // For now, embed the audio data directly in the image metadata
            var metadata = new Dictionary<string, object>
            {
                ["type"] = "audio_file",
                ["audio_data"] = Convert.ToBase64String(ttsResult.AudioData),
                ["audio_path"] = audioFilePath,
                ["mime_type"] = "audio/wav",
                ["duration"] = ttsResult.Duration,
                ["voice"] = ttsResult.Voice,
                ["language"] = ttsResult.Language,
                ["original_text"] = ttsResult.Text,
                ["backend"] = "TTS"
            };

            // Use the actual audio bytes, SwarmUI will handle it through the metadata
            return new Image(ttsResult.AudioData, Path.GetFileName(audioFilePath), "audio/wav", metadata);
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error creating audio image: {ex.Message}");
            throw;
        }
    }

    /// <summary>Ensure the Python backend service is running and healthy</summary>
    private static async Task EnsureBackendServiceAsync()
    {
        try
        {
            if (!PythonBackendService.Instance.IsBackendRunning)
            {
                Logs.Info("[TTSBackend] Starting Python backend service");
                var startResult = await PythonBackendService.Instance.StartAsync();
                if (!startResult.Success)
                {
                    throw new InvalidOperationException($"Failed to start Python backend: {startResult.Message}");
                }
            }

            // Verify TTS service is available
            var healthInfo = await PythonBackendService.Instance.GetHealthAsync();
            if (!healthInfo.IsHealthy || !healthInfo.Services.GetValueOrDefault("tts", false))
            {
                throw new InvalidOperationException("TTS service is not available in Python backend");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Backend service check failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Validate that the specified voice is available</summary>
    private static async Task<bool> ValidateVoiceAsync(string voice, string language)
    {
        try
        {
            var response = await HttpClient.GetAsync($"{ServiceConfiguration.BackendUrl}/status");
            if (!response.IsSuccessStatusCode)
                return false;

            var statusJson = await response.Content.ReadAsStringAsync();
            var status = JObject.Parse(statusJson);

            return status["services"]?["tts"]?["available"]?.Value<bool>() ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Map model name to voice identifier</summary>
    private string GetVoiceFromModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return ServiceConfiguration.DefaultVoice;

        var modelLower = modelName.ToLowerInvariant();

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

    /// <summary>Map model name to language code</summary>
    private static string GetLanguageFromModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return ServiceConfiguration.DefaultLanguage;

        string modelLower = modelName.ToLowerInvariant();

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

    /// <summary>Free memory and resources</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            // Signal Python backend to free TTS model memory if needed
            await HttpClient.PostAsync($"{ServiceConfiguration.BackendUrl}/tts/free_memory", null);
            return true;
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
            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[TTSBackend] Error during shutdown: {ex.Message}");
        }
    }
}

/// <summary>TTS processing result</summary>
public class TTSResult
{
    public bool Success { get; set; }
    public byte[] AudioData { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Voice { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public float Volume { get; set; }
    public double Duration { get; set; }
    public double ProcessingTime { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>TTS processing options</summary>
public class TTSOptions
{
    public float Speed { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public string Format { get; set; } = "wav";
    public Dictionary<string, object> CustomOptions { get; set; } = new();
}
