using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>Direct Python integration for voice processing. Replaces HTTP-based PythonBackendClient with direct function calls for better performance and simpler deployment.</summary>
public class PythonVoiceProcessor
{
    private static readonly Lazy<PythonVoiceProcessor> InstanceLazy = new(() => new PythonVoiceProcessor());
    public static PythonVoiceProcessor Instance => InstanceLazy.Value;

    private readonly object _lock = new();
    private string _pythonPath;
    private string _scriptPath;
    private bool _isInitialized = false;
    private bool _sttInitialized = false;
    private bool _ttsInitialized = false;

    private PythonVoiceProcessor()
    {
        Logs.Debug("[VoiceAssistant] PythonVoiceProcessor instance created");
    }

    /// <summary>Initialize the Python voice processor with environment detection.</summary>
    public async Task<bool> InitializeAsync()
    {
        lock (_lock)
        {
            if (_isInitialized)
                return true;
            try
            {
                // Detect Python environment using SwarmUI's established pattern
                _pythonPath = GetSwarmUIPythonPath();
                if (string.IsNullOrEmpty(_pythonPath))
                {
                    Logs.Error("[VoiceAssistant] Python environment not detected");
                    return false;
                }
                _scriptPath = Path.Combine(ServiceConfiguration.ExtensionDirectory, "python_backend", "voice_processor.py");
                if (!File.Exists(_scriptPath))
                {
                    Logs.Error($"[VoiceAssistant] Voice processor script not found: {_scriptPath}");
                    return false;
                }
                _isInitialized = true;
                Logs.Info("[VoiceAssistant] PythonVoiceProcessor initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Failed to initialize PythonVoiceProcessor: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Initialize voice services in Python with configuration.</summary>
    public async Task<JObject> InitializeVoiceServicesAsync(JObject config = null)
    {
        try
        {
            if (!_isInitialized && !await InitializeAsync())
            {
                return CreateErrorResponse("PythonVoiceProcessor not initialized");
            }
            string configJson = config?.ToString() ?? "{}";
            string[] args = ["init", configJson];
            string result = await RunPythonScriptAsync(args);
            JObject response = JObject.Parse(result);
            if (response["success"]?.Value<bool>() == true)
            {
                JArray sttEngines = response["stt_engines"] as JArray;
                JArray ttsEngines = response["tts_engines"] as JArray;
                _sttInitialized = sttEngines?.Count > 0;
                _ttsInitialized = ttsEngines?.Count > 0;
                Logs.Info($"[VoiceAssistant] Voice services initialized - STT: {_sttInitialized}, TTS: {_ttsInitialized}");
            }
            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Voice services initialization failed: {ex.Message}");
            return CreateErrorResponse($"Initialization failed: {ex.Message}");
        }
    }

    /// <summary>Process STT request using direct Python function call.</summary>
    public async Task<JObject> ProcessSTTAsync(STTRequest request)
    {
        try
        {
            if (!_sttInitialized)
            {
                // Try to auto-initialize
                JObject initResult = await InitializeVoiceServicesAsync();
                if (initResult["success"]?.Value<bool>() != true || !_sttInitialized)
                {
                    return CreateErrorResponse("STT service not available");
                }
            }
            string optionsJson = request.Options?.CustomOptions?.Count > 0 ?
                Newtonsoft.Json.JsonConvert.SerializeObject(request.Options) : "{}";
            string[] args = ["stt", request.AudioData, request.Language, optionsJson];
            string result = await RunPythonScriptAsync(args);
            return JObject.Parse(result);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] STT processing failed: {ex.Message}");
            return CreateErrorResponse($"STT processing failed: {ex.Message}");
        }
    }

    /// <summary>Process TTS request using direct Python function call.</summary>
    public async Task<JObject> ProcessTTSAsync(TTSRequest request)
    {
        try
        {
            if (!_ttsInitialized)
            {
                // Try to auto-initialize
                JObject initResult = await InitializeVoiceServicesAsync();
                if (initResult["success"]?.Value<bool>() != true || !_ttsInitialized)
                {
                    return CreateErrorResponse("TTS service not available");
                }
            }
            // Corrected the condition to check if Options is not null and has CustomOptions with a count greater than 0
            string optionsJson = request.Options?.CustomOptions?.Count > 0 ?
                Newtonsoft.Json.JsonConvert.SerializeObject(request.Options) : "{}";
            string[] args = [
                "tts",
                request.Text,
                request.Voice,
                request.Language,
                request.Volume.ToString("F2"),
                optionsJson
            ];
            string result = await RunPythonScriptAsync(args);
            return JObject.Parse(result);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] TTS processing failed: {ex.Message}");
            return CreateErrorResponse($"TTS processing failed: {ex.Message}");
        }
    }

    /// <summary>Get voice services status.</summary>
    public async Task<JObject> GetVoiceStatusAsync()
    {
        try
        {
            if (!_isInitialized)
            {
                return CreateErrorResponse("Python processor not initialized");
            }
            string[] args = ["status"];
            string result = await RunPythonScriptAsync(args);
            return JObject.Parse(result);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Status check failed: {ex.Message}");
            return CreateErrorResponse($"Status check failed: {ex.Message}");
        }
    }

    /// <summary>Check if STT service is available.</summary>
    public async Task<bool> IsSTTAvailableAsync()
    {
        try
        {
            JObject status = await GetVoiceStatusAsync();
            return status["success"]?.Value<bool>() == true &&
                   status["stt_available"]?.Value<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if TTS service is available.</summary>
    public async Task<bool> IsTTSAvailableAsync()
    {
        try
        {
            JObject status = await GetVoiceStatusAsync();
            return status["success"]?.Value<bool>() == true &&
                   status["tts_available"]?.Value<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Process a modular workflow by chaining STT, TTS, and other operations.</summary>
    /// <param name="request">Workflow request containing steps and input data</param>
    /// <param name="primaryBackend">Primary backend type (determines initialization priority)</param>
    /// <returns>Workflow processing response</returns>
    public async Task<JObject> CallWorkflowServiceAsync(WorkflowRequest request, ServiceConfiguration.BackendType primaryBackend)
    {
        DateTime startTime = DateTime.UtcNow;
        List<string> executedSteps = [];
        Dictionary<string, object> results = [];
        object currentData = request.InputData;
        string currentDataType = request.InputType;
        try
        {
            // Ensure voice services are initialized
            if ((!_sttInitialized || !_ttsInitialized))
            {
                JObject initResult = await InitializeVoiceServicesAsync();
                if (initResult["success"]?.Value<bool>() != true)
                {
                    return CreateErrorResponse($"Voice services initialization failed: {initResult["error"]}");
                }
            }
            // Process each enabled step in order
            foreach (WorkflowStep step in request.Steps.Where(s => s.Enabled).OrderBy(s => s.Order))
            {
                try
                {
                    (currentData, currentDataType, object stepResult) = await ProcessWorkflowStep(step, currentData, currentDataType);
                    results[step.Type] = stepResult;
                    executedSteps.Add(step.Type);
                }
                catch (Exception stepEx)
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["error"] = $"Workflow step '{step.Type}' failed: {stepEx.Message}",
                        ["failed_step"] = step.Type,
                        ["executed_steps"] = JArray.FromObject(executedSteps),
                        ["workflow_results"] = JObject.FromObject(results),
                        ["total_processing_time"] = (DateTime.UtcNow - startTime).TotalSeconds
                    };
                }
            }
            return new JObject
            {
                ["success"] = true,
                ["message"] = "Workflow completed successfully",
                ["workflow_results"] = JObject.FromObject(results),
                ["executed_steps"] = JArray.FromObject(executedSteps),
                ["total_processing_time"] = (DateTime.UtcNow - startTime).TotalSeconds,
                ["final_output"] = JToken.FromObject(currentData),
                ["final_output_type"] = currentDataType
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Workflow processing failed: {ex.Message}");
        }
    }

    /// <summary>Process a single workflow step and return updated data.</summary>
    private async Task<(object newData, string newType, object stepResult)> ProcessWorkflowStep(WorkflowStep step, object inputData, string inputType)
    {
        switch (step.Type.ToLowerInvariant())
        {
            case "stt":
                if (inputType != "audio")
                    throw new InvalidOperationException($"STT step requires audio input, got: {inputType}");
                STTRequest sttRequest = new()
                {
                    AudioData = inputData.ToString(),
                    Language = step.Config["language"]?.ToString() ?? "en-US",
                    Options = new STTOptions
                    {
                        ReturnConfidence = step.Config["return_confidence"]?.Value<bool>() ?? true,
                        ReturnAlternatives = step.Config["return_alternatives"]?.Value<bool>() ?? false,
                        ModelPreference = step.Config["model_preference"]?.ToString() ?? "accuracy"
                    }
                };
                JObject sttResult = await ProcessSTTAsync(sttRequest);
                if (sttResult["success"]?.Value<bool>() != true)
                    throw new InvalidOperationException($"STT processing failed: {sttResult["error"]}");
                string transcription = sttResult["transcription"]?.ToString() ?? "";
                return (transcription, "text", sttResult);
            case "tts":
                if (inputType != "text")
                    throw new InvalidOperationException($"TTS step requires text input, got: {inputType}");
                TTSRequest ttsRequest = new()
                {
                    Text = inputData.ToString(),
                    Voice = step.Config["voice"]?.ToString() ?? "default",
                    Language = step.Config["language"]?.ToString() ?? "en-US",
                    Volume = step.Config["volume"]?.Value<float>() ?? 0.8f,
                    Options = new TTSOptions
                    {
                        Speed = step.Config["speed"]?.Value<float>() ?? 1.0f,
                        Pitch = step.Config["pitch"]?.Value<float>() ?? 1.0f,
                        Format = step.Config["format"]?.ToString() ?? "wav"
                    }
                };
                JObject ttsResult = await ProcessTTSAsync(ttsRequest);
                if (ttsResult["success"]?.Value<bool>() != true)
                    throw new InvalidOperationException($"TTS processing failed: {ttsResult["error"]}");
                string audioData = ttsResult["audio_data"]?.ToString() ?? "";
                return (audioData, "audio", ttsResult);
            case "custom":
                // Simple passthrough or basic text transformations
                string operation = step.Config["operation"]?.ToString() ?? "passthrough";
                if (operation == "passthrough")
                {
                    return (inputData, inputType, new { operation = "passthrough", data = inputData });
                }
                else if (operation == "uppercase" && inputType == "text")
                {
                    string upperText = inputData.ToString().ToUpperInvariant();
                    return (upperText, "text", new { operation = "uppercase", result = upperText });
                }
                else if (operation == "lowercase" && inputType == "text")
                {
                    string lowerText = inputData.ToString().ToLowerInvariant();
                    return (lowerText, "text", new { operation = "lowercase", result = lowerText });
                }
                break;
            default:
                throw new ArgumentException($"Unknown workflow step type: {step.Type}");
        }
        return (inputData, inputType, new { step = step.Type, status = "completed" });
    }

    /// <summary>Cleanup voice services.</summary>
    public async Task<JObject> CleanupAsync()
    {
        try
        {
            if (!_isInitialized)
            {
                return CreateErrorResponse("Not initialized");
            }

            string[] args = ["cleanup"];
            string result = await RunPythonScriptAsync(args);

            _sttInitialized = false;
            _ttsInitialized = false;

            return JObject.Parse(result);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Cleanup failed: {ex.Message}");
            return CreateErrorResponse($"Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>Run Python script with arguments and return JSON output.</summary>
    private async Task<string> RunPythonScriptAsync(string[] args, int timeoutMs = 30000)
    {
        return await Task.Run(() =>
        {
            try
            {
                string arguments = $"\"{_scriptPath}\" " + string.Join(" ", args.Select(arg => $"\"{EscapeArgument(arg)}\""));

                ProcessStartInfo startInfo = new()
                {
                    FileName = _pythonPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_scriptPath)
                };

                // Setup Python environment using SwarmUI's pattern
                ConfigurePythonEnvironment(startInfo);

                using Process process = new() { StartInfo = startInfo };
                StringBuilder output = new();
                StringBuilder error = new();

                process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        output.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        error.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutMs))
                {
                    process.Kill();
                    throw new TimeoutException($"Python script timed out after {timeoutMs}ms");
                }

                if (process.ExitCode != 0)
                {
                    string errorOutput = error.ToString();
                    Logs.Error($"[VoiceAssistant] Python script failed (exit code {process.ExitCode}): {errorOutput}");
                    throw new Exception($"Python script failed: {errorOutput}");
                }

                string result = output.ToString().Trim();
                if (string.IsNullOrEmpty(result))
                {
                    throw new Exception("Python script returned empty output");
                }

                return result;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Python script execution error: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>Configure Python environment using SwarmUI's established pattern.</summary>
    private void ConfigurePythonEnvironment(ProcessStartInfo startInfo)
    {
        try
        {
            // Use SwarmUI's Python environment setup
            if (_pythonPath.Contains("python_embeded"))
            {
                string embedPath = Path.GetFullPath("./dlbackend/comfy/python_embeded");
                startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(embedPath);
                startInfo.WorkingDirectory = Path.GetFullPath("./dlbackend/comfy/");
            }
            else if (_pythonPath.Contains("venv"))
            {
                string venvPath = Path.GetFullPath("./dlbackend/ComfyUI/venv/bin");
                startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(venvPath);
            }

            PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VoiceAssistant] Error configuring Python environment: {ex.Message}");
        }
    }

    /// <summary>Get SwarmUI Python path using established detection logic.</summary>
    private static string GetSwarmUIPythonPath()
    {
        try
        {
            if (File.Exists("./dlbackend/comfy/python_embeded/python.exe"))
            {
                return Path.GetFullPath("./dlbackend/comfy/python_embeded/python.exe");
            }
            else if (File.Exists("./dlbackend/ComfyUI/venv/bin/python"))
            {
                return Path.GetFullPath("./dlbackend/ComfyUI/venv/bin/python");
            }
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error detecting Python path: {ex.Message}");
            return null;
        }
    }

    /// <summary>Escape command line argument.</summary>
    private static string EscapeArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
            return "";

        // Replace quotes and escape special characters
        return argument.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    /// <summary>Create standardized error response.</summary>
    private static JObject CreateErrorResponse(string message)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
    }

    /// <summary>Get processor status for diagnostics.</summary>
    public JObject GetProcessorStatus()
    {
        return new JObject
        {
            ["initialized"] = _isInitialized,
            ["stt_initialized"] = _sttInitialized,
            ["tts_initialized"] = _ttsInitialized,
            ["python_path"] = _pythonPath ?? "",
            ["script_path"] = _scriptPath ?? "",
            ["script_exists"] = !string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath)
        };
    }
}
