using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Hartsy.Extensions.AudioLab.AudioBackends;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Hartsy.Extensions.AudioLab.Progress;
using Hartsy.Extensions.AudioLab.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Text;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>Permission definitions for the AudioLab API endpoints.</summary>
public static class AudioLabPermissions
{
    public static readonly PermInfoGroup AudioLabPermGroup = new("AudioLab", "Permissions related to AudioLab functionality for API calls and voice processing.");
    public static readonly PermInfo PermProcessAudio = Permissions.Register(new("audio_process", "Process Audio", "Allows processing of audio through any audio provider.", PermissionDefault.POWERUSERS, AudioLabPermGroup));
    public static readonly PermInfo PermManageBackends = Permissions.Register(new("audio_manage_backends", "Manage Audio Backends", "Allows managing audio backend providers.", PermissionDefault.POWERUSERS, AudioLabPermGroup));
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("audio_check_status", "Check Audio Status", "Allows checking the status of audio providers.", PermissionDefault.POWERUSERS, AudioLabPermGroup));
}

/// <summary>AudioLab API endpoints — provider-aware audio processing.</summary>
[API.APIClass("AudioLab API with provider-based audio processing")]
public static class AudioLabAPI
{
    /// <summary>Registers all API endpoints.</summary>
    public static void Register()
    {
        try
        {
            // Generic provider-based processing
            API.RegisterAPICall(ProcessAudio, false, AudioLabPermissions.PermProcessAudio);

            // Backward-compatible endpoints
            API.RegisterAPICall(ProcessSTT, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessTTS, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessWorkflow, false, AudioLabPermissions.PermProcessAudio);

            // Provider status and dependency management
            API.RegisterAPICall(GetAllProvidersStatus, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(InstallProviderDependencies, false, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(GetInstallationStatus, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, AudioLabPermissions.PermCheckStatus);

            // Engine management (install-driven registration)
            API.RegisterAPICall(AudioLabListEngines, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(AudioLabInstallEngine, true, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(AudioLabUninstallEngine, true, AudioLabPermissions.PermManageBackends);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    #region Generic Provider Processing

    /// <summary>Process audio through a specific provider. Takes provider_id and routes to the correct engine.</summary>
    public static async Task<JObject> ProcessAudio(Session session, JObject input)
    {
        try
        {
            string providerId = input["provider_id"]?.ToString();
            if (string.IsNullOrEmpty(providerId))
            {
                return AudioLab.CreateErrorResponse("provider_id is required", "missing_provider");
            }

            AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
            if (provider == null)
            {
                return AudioLab.CreateErrorResponse($"Unknown provider: {providerId}", "unknown_provider");
            }

            // Build args from input
            Dictionary<string, object> args = [];
            if (input["args"] is JObject argsObj)
            {
                foreach (JProperty prop in argsObj.Properties())
                {
                    args[prop.Name] = prop.Value?.ToObject<object>();
                }
            }

            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args);
            return result;
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Audio processing failed", "processing_error", ex);
        }
    }

    #endregion

    #region Backward-Compatible Endpoints

    /// <summary>Process Speech-to-Text.</summary>
    public static async Task<JObject> ProcessSTT(Session session, JObject input)
    {
        try
        {
            STTRequest request = ParseSTTRequest(input, session.ID);

            // Use provider_id from request if supplied, otherwise fall back to first available
            string requestedProvider = input["provider_id"]?.ToString();
            AudioProviderDefinition sttProvider = !string.IsNullOrEmpty(requestedProvider)
                ? AudioProviderRegistry.GetById(requestedProvider)
                : AudioProviderRegistry.GetByCategory(AudioCategory.STT).FirstOrDefault();

            if (sttProvider == null)
            {
                return AudioLab.CreateErrorResponse("No STT provider available", "no_provider");
            }

            Dictionary<string, object> args = new()
            {
                ["audio_data"] = request.AudioData,
                ["language"] = request.Language
            };

            JObject result = await AudioServerManager.Instance.ProcessAsync(sttProvider, args);

            if (result["success"]?.Value<bool>() == true)
            {
                STTResponse response = new()
                {
                    Success = true,
                    Transcription = result["text"]?.ToString() ?? "",
                    Confidence = result["confidence"]?.Value<float>() ?? 0f,
                    Language = request.Language,
                    ProcessingTime = result["processing_time"]?.Value<double>() ?? 0,
                    SessionId = session.ID
                };
                return response.ToJObject();
            }
            return result;
        }
        catch (ArgumentException ex)
        {
            return AudioLab.CreateErrorResponse("Invalid STT request parameters", "invalid_request", ex);
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("STT processing failed", "processing_error", ex);
        }
    }

    /// <summary>Process Text-to-Speech (backward compatible).</summary>
    public static async Task<JObject> ProcessTTS(Session session, JObject input)
    {
        try
        {
            TTSRequest request = ParseTTSRequest(input, session.ID);

            // Use provider_id from request if supplied, otherwise fall back to first available
            string requestedProvider = input["provider_id"]?.ToString();
            Logs.Debug($"[AudioLab] ProcessTTS: requested provider_id='{requestedProvider}'");
            AudioProviderDefinition ttsProvider = !string.IsNullOrEmpty(requestedProvider)
                ? AudioProviderRegistry.GetById(requestedProvider)
                : AudioProviderRegistry.GetByCategory(AudioCategory.TTS).FirstOrDefault();

            if (ttsProvider == null)
            {
                return AudioLab.CreateErrorResponse($"No TTS provider available (requested: '{requestedProvider}')", "no_provider");
            }
            Logs.Info($"[AudioLab] ProcessTTS: routing to provider '{ttsProvider.Id}' ({ttsProvider.Name}), module={ttsProvider.PythonModule}, class={ttsProvider.PythonEngineClass}");

            Dictionary<string, object> args = new()
            {
                ["text"] = request.Text,
                ["voice"] = request.Voice,
                ["language"] = request.Language,
                ["volume"] = request.Volume
            };

            JObject result = await AudioServerManager.Instance.ProcessAsync(ttsProvider, args);

            if (result["success"]?.Value<bool>() == true)
            {
                TTSResponse response = new()
                {
                    Success = true,
                    AudioData = result["audio_data"]?.ToString() ?? "",
                    Text = request.Text,
                    Voice = request.Voice,
                    Language = request.Language,
                    Volume = request.Volume,
                    Duration = result["duration"]?.Value<double>() ?? 0,
                    ProcessingTime = result["processing_time"]?.Value<double>() ?? 0,
                    SessionId = session.ID
                };
                return response.ToJObject();
            }
            return result;
        }
        catch (ArgumentException ex)
        {
            return AudioLab.CreateErrorResponse("Invalid TTS request parameters", "invalid_request", ex);
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("TTS processing failed", "processing_error", ex);
        }
    }

    /// <summary>Process modular workflow (backward compatible).</summary>
    public static async Task<JObject> ProcessWorkflow(Session session, JObject input)
    {
        try
        {
            WorkflowRequest request = ParseWorkflowRequest(input, session.ID);

            // Simple chained processing through providers
            object currentData = request.InputData;
            string currentDataType = request.InputType;
            List<string> executedSteps = [];
            Dictionary<string, object> results = [];
            DateTime startTime = DateTime.UtcNow;

            foreach (WorkflowStep step in request.Steps.Where(s => s.Enabled).OrderBy(s => s.Order))
            {
                AudioCategory category = step.Type.ToLowerInvariant() switch
                {
                    "stt" => AudioCategory.STT,
                    "tts" => AudioCategory.TTS,
                    _ => AudioCategory.TTS
                };

                AudioProviderDefinition provider = AudioProviderRegistry.GetByCategory(category)
                    .FirstOrDefault();

                if (provider == null) continue;

                Dictionary<string, object> args = [];
                if (category == AudioCategory.STT)
                {
                    args["audio_data"] = currentData?.ToString() ?? "";
                    args["language"] = step.Config?["language"]?.ToString() ?? "en-US";
                }
                else if (category == AudioCategory.TTS)
                {
                    args["text"] = currentData?.ToString() ?? "";
                    args["voice"] = step.Config?["voice"]?.ToString() ?? "default";
                    args["language"] = step.Config?["language"]?.ToString() ?? "en-US";
                    args["volume"] = step.Config?["volume"]?.Value<float>() ?? 0.8f;
                }

                JObject stepResult = await AudioServerManager.Instance.ProcessAsync(provider, args);
                results[step.Type] = stepResult;
                executedSteps.Add(step.Type);

                if (category == AudioCategory.STT)
                {
                    currentData = stepResult["text"]?.ToString() ?? "";
                    currentDataType = "text";
                }
                else if (category == AudioCategory.TTS)
                {
                    currentData = stepResult["audio_data"]?.ToString() ?? "";
                    currentDataType = "audio";
                }
            }

            return new JObject
            {
                ["success"] = true,
                ["message"] = "Workflow completed successfully",
                ["workflow_results"] = JObject.FromObject(results),
                ["executed_steps"] = JArray.FromObject(executedSteps),
                ["total_processing_time"] = (DateTime.UtcNow - startTime).TotalSeconds,
                ["session_id"] = session.ID
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Workflow processing failed", "processing_error", ex);
        }
    }

    #endregion

    #region Provider Status and Dependencies

    /// <summary>Get status of all registered providers.</summary>
    public static async Task<JObject> GetAllProvidersStatus(Session session, JObject input)
    {
        try
        {
            JArray providers = [];
            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
            {
                providers.Add(new JObject
                {
                    ["id"] = provider.Id,
                    ["name"] = provider.Name,
                    ["category"] = provider.Category.ToString(),
                    ["model_count"] = provider.Models.Count,
                    ["dependency_count"] = provider.Dependencies.Count
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["providers"] = providers,
                ["total_count"] = providers.Count
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Failed to get provider status", "status_error", ex);
        }
    }

    /// <summary>Install dependencies for a specific provider.</summary>
    public static async Task<JObject> InstallProviderDependencies(Session session, JObject input)
    {
        try
        {
            string providerId = input["provider_id"]?.ToString();
            if (string.IsNullOrEmpty(providerId))
            {
                return AudioLab.CreateErrorResponse("provider_id is required", "missing_provider");
            }

            AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
            if (provider == null)
            {
                return AudioLab.CreateErrorResponse($"Unknown provider: {providerId}", "unknown_provider");
            }

            AudioDependencyInstaller installer = new();
            PythonEnvironmentInfo pythonInfo = await installer.DetectPythonEnvironmentForGroupAsync(provider.EngineGroup);
            if (pythonInfo?.IsValid != true)
            {
                return AudioLab.CreateErrorResponse("Python environment not available. Install Python 3.10+ or ensure python/python3 is on your system PATH.", "python_not_found");
            }

            bool success = await installer.InstallProviderDependenciesAsync(pythonInfo, provider);
            return new JObject
            {
                ["success"] = success,
                ["provider_id"] = providerId,
                ["message"] = success ? $"Dependencies for {provider.Name} installed successfully" : $"Failed to install dependencies for {provider.Name}"
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Dependency installation failed", "install_error", ex);
        }
    }

    /// <summary>Get installation status for all providers (checks per-group venvs).</summary>
    public static async Task<JObject> GetInstallationStatus(Session session, JObject input)
    {
        try
        {
            AudioDependencyInstaller installer = new();

            // Check if base Python is available at all
            string basePython = VenvManager.GetBasePythonPath();
            bool pythonDetected = basePython != null;

            JObject providerStatuses = [];
            // Cache PythonEnvironmentInfo per group to avoid redundant lookups
            Dictionary<string, PythonEnvironmentInfo> groupEnvCache = [];

            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
            {
                if (provider.Dependencies.Count == 0)
                {
                    providerStatuses[provider.Id] = true;
                    continue;
                }

                string group = provider.EngineGroup;

                // Get or create the PythonEnvironmentInfo for this group
                if (!groupEnvCache.TryGetValue(group, out PythonEnvironmentInfo groupPython))
                {
                    string venvPython = VenvManager.GetVenvPythonPath(group);
                    if (System.IO.File.Exists(venvPython))
                    {
                        groupPython = new PythonEnvironmentInfo
                        {
                            PythonPath = venvPython,
                            OperatingSystem = Environment.OSVersion.ToString(),
                            IsEmbedded = false,
                            Version = "detected",
                        };
                    }
                    groupEnvCache[group] = groupPython; // may be null
                }

                if (groupPython?.IsValid != true)
                {
                    providerStatuses[provider.Id] = false;
                    continue;
                }

                bool installed = await installer.CheckProviderDependenciesAsync(groupPython, provider);
                providerStatuses[provider.Id] = installed;
            }

            return new JObject
            {
                ["success"] = true,
                ["python_detected"] = pythonDetected,
                ["base_python"] = basePython ?? "not found",
                ["providers"] = providerStatuses
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Failed to check installation status", "status_error", ex);
        }
    }

    /// <summary>Get real-time installation progress.</summary>
    public static async Task<JObject> GetInstallationProgress(Session session, JObject input)
    {
        try
        {
            ProgressTracker tracker = ProgressTracking.Installation;

            return new JObject
            {
                ["success"] = true,
                ["progress"] = tracker.Progress,
                ["current_step"] = tracker.CurrentStep,
                ["current_package"] = tracker.CurrentPackage,
                ["completed_packages"] = new JArray(tracker.CompletedPackages),
                ["is_complete"] = tracker.IsComplete,
                ["has_error"] = tracker.HasError,
                ["error_message"] = tracker.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Failed to get installation progress", "status_error", ex);
        }
    }

    #endregion

    #region Engine Management

    /// <summary>Lists all available audio engines with their install status, metadata, and dependencies.
    /// Uses AllBackends instead of RunningBackendsOfType so installed engines are visible
    /// even while the backend is still starting (LOADING state).</summary>
    public static async Task<JObject> AudioLabListEngines(Session session, JObject input)
    {
        try
        {
            DynamicAudioBackend backend = Program.Backends.AllBackends.Values
                .Select(b => b.AbstractBackend as DynamicAudioBackend)
                .FirstOrDefault(b => b is not null);
            IReadOnlySet<string> installedIds = backend?.GetInstalledEngineIds() ?? new HashSet<string>();
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool dockerEnabled = AudioConfiguration.UseDocker;

            JArray engines = [];
            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
            {
                // Platform compatibility check
                bool platformCompatible = true;
                string platformNote = "";
                if (provider.RequiresDocker && isWindows && !dockerEnabled)
                {
                    platformCompatible = false;
                    platformNote = "Requires Docker on Windows. Enable 'Use Docker' in backend settings.";
                }

                // Build dependencies list
                JArray deps = [];
                foreach (PackageDefinition dep in provider.Dependencies)
                {
                    deps.Add(new JObject
                    {
                        ["name"] = dep.Name,
                        ["category"] = dep.Category
                    });
                }

                // Build models list
                JArray models = [];
                foreach (AudioModelDefinition modelDef in provider.Models)
                {
                    models.Add(new JObject
                    {
                        ["id"] = modelDef.Id,
                        ["name"] = modelDef.Name,
                        ["description"] = modelDef.Description,
                        ["source_url"] = modelDef.SourceUrl,
                        ["license"] = modelDef.License,
                        ["estimated_size"] = modelDef.EstimatedSize,
                        ["estimated_vram"] = modelDef.EstimatedVram
                    });
                }

                engines.Add(new JObject
                {
                    ["id"] = provider.Id,
                    ["name"] = provider.Name,
                    ["category"] = provider.Category.ToString(),
                    ["engine_group"] = provider.EngineGroup,
                    ["requires_docker"] = provider.RequiresDocker,
                    ["platform_compatible"] = platformCompatible,
                    ["platform_note"] = platformNote,
                    ["installed"] = installedIds.Contains(provider.Id),
                    ["dependencies"] = deps,
                    ["models"] = models
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["engines"] = engines,
                ["backend_status"] = backend?.Status.ToString() ?? "NOT_FOUND"
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Failed to list engines", "list_error", ex);
        }
    }

    /// <summary>Installs an audio engine via WebSocket with streaming progress.
    /// Follows the DoModelDownloadWS pattern: WebSocket parameter auto-detected by API framework.</summary>
    public static async Task<JObject> AudioLabInstallEngine(Session session, WebSocket ws, string provider_id)
    {
        try
        {
            if (string.IsNullOrEmpty(provider_id))
            {
                await ws.SendJson(new JObject { ["error"] = "provider_id is required" }, API.WebsocketTimeout);
                return null;
            }

            AudioProviderDefinition provider = AudioProviderRegistry.GetById(provider_id);
            if (provider == null)
            {
                await ws.SendJson(new JObject { ["error"] = $"Unknown provider: {provider_id}" }, API.WebsocketTimeout);
                return null;
            }

            DynamicAudioBackend backend = Program.Backends.RunningBackendsOfType<DynamicAudioBackend>().FirstOrDefault();
            if (backend == null)
            {
                await ws.SendJson(new JObject { ["error"] = "Audio backend is not running. Add and enable the Audio Backend first." }, API.WebsocketTimeout);
                return null;
            }

            if (backend.GetInstalledEngineIds().Contains(provider_id))
            {
                await ws.SendJson(new JObject { ["success"] = true, ["message"] = $"{provider.Name} is already installed." }, API.WebsocketTimeout);
                return null;
            }

            bool success = await backend.InstallAndRegisterEngine(provider_id, msg =>
            {
                Logs.Info($"[AudioLab] Install progress: {msg}");
                ws.SendJson(new JObject { ["info"] = msg }, API.WebsocketTimeout).Wait();
            });

            if (success)
            {
                await ws.SendJson(new JObject { ["success"] = true, ["provider_id"] = provider_id, ["message"] = $"{provider.Name} installed successfully!" }, API.WebsocketTimeout);
            }
            else
            {
                await ws.SendJson(new JObject { ["error"] = $"Failed to install {provider.Name}. Check server logs for details." }, API.WebsocketTimeout);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Engine installation failed: {ex}");
            await ws.SendJson(new JObject { ["error"] = $"Engine installation failed: {ex.Message}" }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Uninstalls an audio engine: removes models from registry and persists the change.</summary>
    public static async Task<JObject> AudioLabUninstallEngine(Session session, string provider_id)
    {
        try
        {
            if (string.IsNullOrEmpty(provider_id))
            {
                return AudioLab.CreateErrorResponse("provider_id is required", "missing_provider");
            }

            AudioProviderDefinition provider = AudioProviderRegistry.GetById(provider_id);
            if (provider == null)
            {
                return AudioLab.CreateErrorResponse($"Unknown provider: {provider_id}", "unknown_provider");
            }

            DynamicAudioBackend backend = Program.Backends.RunningBackendsOfType<DynamicAudioBackend>().FirstOrDefault();
            if (backend == null)
            {
                return AudioLab.CreateErrorResponse("Audio backend is not running.", "no_backend");
            }

            if (!backend.GetInstalledEngineIds().Contains(provider_id))
            {
                return new JObject
                {
                    ["success"] = true,
                    ["message"] = $"{provider.Name} is not installed."
                };
            }

            backend.UnregisterEngine(provider_id);

            return new JObject
            {
                ["success"] = true,
                ["provider_id"] = provider_id,
                ["message"] = $"{provider.Name} removed successfully."
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse($"Engine uninstall failed: {ex.Message}", "uninstall_error", ex);
        }
    }

    #endregion

    #region Request Parsing

    private static STTRequest ParseSTTRequest(JObject input, string sessionId)
    {
        STTRequest request = new()
        {
            SessionId = sessionId,
            AudioData = input["audio_data"]?.ToString() ?? "",
            Language = input["language"]?.ToString() ?? AudioConfiguration.DefaultLanguage,
            Options = new STTOptions()
        };
        if (input["options"] is JObject opts)
        {
            request.Options.ReturnConfidence = opts["return_confidence"]?.Value<bool>() ?? true;
            request.Options.ReturnAlternatives = opts["return_alternatives"]?.Value<bool>() ?? false;
            request.Options.ModelPreference = opts["model_preference"]?.ToString() ?? "accuracy";
        }
        request.Validate();
        return request;
    }

    private static TTSRequest ParseTTSRequest(JObject input, string sessionId)
    {
        TTSRequest request = new()
        {
            SessionId = sessionId,
            Text = input["text"]?.ToString() ?? "",
            Voice = input["voice"]?.ToString() ?? AudioConfiguration.DefaultVoice,
            Language = input["language"]?.ToString() ?? AudioConfiguration.DefaultLanguage,
            Volume = input["volume"]?.Value<float>() ?? AudioConfiguration.DefaultVolume,
            Options = new TTSOptions()
        };
        if (input["options"] is JObject opts)
        {
            request.Options.Speed = opts["speed"]?.Value<float>() ?? 1.0f;
            request.Options.Pitch = opts["pitch"]?.Value<float>() ?? 1.0f;
            request.Options.Format = opts["format"]?.ToString() ?? "wav";
        }
        request.Validate();
        return request;
    }

    private static WorkflowRequest ParseWorkflowRequest(JObject input, string sessionId)
    {
        WorkflowRequest request = new()
        {
            SessionId = sessionId,
            WorkflowType = input["workflow_type"]?.ToString() ?? "custom",
            InputData = input["input_data"]?.ToString() ?? "",
            InputType = input["input_type"]?.ToString() ?? "text",
            Steps = []
        };
        if (input["steps"] is JArray stepsArray)
        {
            foreach (JToken stepToken in stepsArray)
            {
                if (stepToken is JObject stepObj)
                {
                    WorkflowStep step = new()
                    {
                        Type = stepObj["type"]?.ToString() ?? "unknown",
                        Enabled = stepObj["enabled"]?.Value<bool>() ?? true,
                        Order = stepObj["order"]?.Value<int>() ?? 0,
                        Config = stepObj["config"] as JObject ?? []
                    };
                    step.Validate();
                    request.Steps.Add(step);
                }
            }
        }
        request.Steps = [.. request.Steps.OrderBy(s => s.Order)];
        request.Validate();
        return request;
    }

    #endregion
}
