using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Hartsy.Extensions.AudioLab.AudioBackends;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
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
    /// <summary>Permission group for all AudioLab API operations.</summary>
    public static readonly PermInfoGroup AudioLabPermGroup = new("AudioLab", "Permissions related to AudioLab functionality for API calls and voice processing.");

    /// <summary>Permission for processing audio through any provider.</summary>
    public static readonly PermInfo PermProcessAudio = Permissions.Register(new("audio_process", "Process Audio", "Allows processing of audio through any audio provider.", PermissionDefault.POWERUSERS, AudioLabPermGroup));

    /// <summary>Permission for managing audio backend providers.</summary>
    public static readonly PermInfo PermManageBackends = Permissions.Register(new("audio_manage_backends", "Manage Audio Backends", "Allows managing audio backend providers.", PermissionDefault.POWERUSERS, AudioLabPermGroup));

    /// <summary>Permission for checking audio provider status.</summary>
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("audio_check_status", "Check Audio Status", "Allows checking the status of audio providers.", PermissionDefault.POWERUSERS, AudioLabPermGroup));

    /// <summary>Permission for saving/loading DAW projects (per-user data).</summary>
    public static readonly PermInfo PermDawProjects = Permissions.Register(new("audio_daw_projects", "AudioLab DAW Projects", "Allows saving and loading personal AudioLab DAW projects.", PermissionDefault.USER, AudioLabPermGroup));
}

/// <summary>AudioLab API endpoints — provider-aware audio processing.</summary>
[API.APIClass("AudioLab API with provider-based audio processing")]
public static class AudioLabAPI
{
    /// <summary>Cap on any single string arg to ProcessAudio: 64 MB of base64 (~48 MB decoded), well above any
    /// realistic reference clip while still rejecting a runaway payload.</summary>
    private const int MaxArgStringLength = 64 * 1024 * 1024;

    /// <summary>Registers all API endpoints.</summary>
    public static void Register()
    {
        try
        {
            API.RegisterAPICall(ProcessAudio, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessSTT, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessTTS, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(ProcessWorkflow, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(GetAllProvidersStatus, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(GetInstallationStatus, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(ConvertAudioFormat, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(AudioLabTimeStretch, false, AudioLabPermissions.PermProcessAudio);
            API.RegisterAPICall(AudioLabListEngines, false, AudioLabPermissions.PermCheckStatus);
            API.RegisterAPICall(AudioLabInstallEngine, true, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(AudioLabInstallAllModels, true, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(AudioLabUninstallEngine, true, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(AudioLabRemoveAllModels, true, AudioLabPermissions.PermManageBackends);
            API.RegisterAPICall(AudioLabSaveProject, true, AudioLabPermissions.PermDawProjects);
            API.RegisterAPICall(AudioLabLoadProject, false, AudioLabPermissions.PermDawProjects);
            API.RegisterAPICall(AudioLabListProjects, false, AudioLabPermissions.PermDawProjects);
            API.RegisterAPICall(AudioLabDeleteProject, true, AudioLabPermissions.PermDawProjects);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to register API endpoints: {ex.Message}");
            throw;
        }
    }

    /// <summary>Tempo-preserving time-stretch (and optional pitch shift) via ffmpeg atempo.
    /// rate = output tempo multiplier (2.0 = twice as fast / half as long). semitones shifts pitch.</summary>
    public static async Task<JObject> AudioLabTimeStretch(Session session, JObject input)
    {
        try
        {
            string audioData = input["audio_data"]?.ToString();
            double rate = input["rate"]?.Value<double>() ?? 1.0;
            double semitones = input["semitones"]?.Value<double>() ?? 0;
            if (string.IsNullOrEmpty(audioData))
            {
                return AudioLab.CreateErrorResponse("audio_data is required", "missing_audio");
            }
            if (rate < 0.25 || rate > 4.0)
            {
                return AudioLab.CreateErrorResponse("rate must be within 0.25-4.0", "bad_rate");
            }
            string ffmpeg = Utilities.FfmegLocation.Value;
            if (string.IsNullOrEmpty(ffmpeg))
            {
                return AudioLab.CreateErrorResponse("ffmpeg not found. Install ffmpeg and ensure it is in your PATH.", "ffmpeg_not_found");
            }
            byte[] audioBytes = Convert.FromBase64String(audioData);
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audiolab");
            System.IO.Directory.CreateDirectory(tempDir);
            string inputPath = System.IO.Path.Combine(tempDir, $"stretch_input_{Guid.NewGuid()}.wav");
            string outputPath = System.IO.Path.Combine(tempDir, $"stretch_output_{Guid.NewGuid()}.wav");
            try
            {
                await System.IO.File.WriteAllBytesAsync(inputPath, audioBytes);
                // Pitch shift = resample by 2^(semi/12) then counter-stretch; atempo legal range is
                // 0.5-2.0 so the total tempo factor is decomposed into a chain.
                double pitchFactor = Math.Pow(2, semitones / 12.0);
                double tempo = rate / pitchFactor;
                List<string> filters = [];
                if (Math.Abs(semitones) > 0.01)
                {
                    filters.Add($"asetrate=44100*{pitchFactor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)},aresample=44100");
                }
                double remaining = tempo;
                while (remaining < 0.5 || remaining > 2.0)
                {
                    double step = remaining < 0.5 ? 0.5 : 2.0;
                    filters.Add($"atempo={step.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}");
                    remaining /= step;
                }
                if (Math.Abs(remaining - 1.0) > 0.0001)
                {
                    filters.Add($"atempo={remaining.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}");
                }
                List<string> args = ["-i", inputPath, "-y"];
                if (filters.Count > 0)
                {
                    args.AddRange(["-filter:a", string.Join(",", filters)]);
                }
                args.AddRange(["-codec:a", "pcm_s16le", "-ar", "44100", outputPath]);
                string result = await Utilities.QuickRunProcess(ffmpeg, [.. args]);
                Logs.Debug($"[AudioLab] ffmpeg stretch output: {result}");
                if (!System.IO.File.Exists(outputPath))
                {
                    return AudioLab.CreateErrorResponse("Time-stretch failed - no output file produced", "stretch_failed");
                }
                byte[] outputBytes = await System.IO.File.ReadAllBytesAsync(outputPath);
                return AudioLab.CreateSuccessResponse(new JObject()
                {
                    ["audio_data"] = Convert.ToBase64String(outputBytes),
                    ["rate"] = rate,
                    ["semitones"] = semitones
                });
            }
            finally
            {
                try { System.IO.File.Delete(inputPath); } catch { }
                try { System.IO.File.Delete(outputPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] TimeStretch failed: {ex.Message}");
            return AudioLab.CreateErrorResponse($"Time-stretch failed: {ex.Message}", "stretch_failed");
        }
    }

    #region DAW Projects

    /// <summary>Per-user data store namespace for DAW projects.</summary>
    private const string DawProjectDataName = "audiolab_daw";

    /// <summary>Max serialized project size accepted (base64 audio inflates ~1.33x).</summary>
    private const int MaxProjectBytes = 64 * 1024 * 1024;

    /// <summary>Save a DAW project (arrangement JSON with embedded base64 clip audio) under the user's account.</summary>
    public static async Task<JObject> AudioLabSaveProject(Session session, JObject input)
    {
        try
        {
            await Task.CompletedTask;
            string name = input["name"]?.ToString()?.Trim();
            string json = input["project_json"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return AudioLab.CreateErrorResponse("Missing 'name' parameter", "missing_name");
            }
            if (string.IsNullOrEmpty(json))
            {
                return AudioLab.CreateErrorResponse("Missing 'project_json' parameter", "missing_project");
            }
            if (json.Length > MaxProjectBytes)
            {
                return AudioLab.CreateErrorResponse($"Project too large ({json.Length / (1024 * 1024)}MB, max {MaxProjectBytes / (1024 * 1024)}MB)", "project_too_large");
            }
            session.User.SaveGenericData(DawProjectDataName, name, json);
            return AudioLab.CreateSuccessResponse(new JObject() { ["name"] = name, ["size"] = json.Length });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] SaveProject failed: {ex.Message}");
            return AudioLab.CreateErrorResponse($"Save failed: {ex.Message}", "save_failed");
        }
    }

    /// <summary>Load a previously saved DAW project by name.</summary>
    public static async Task<JObject> AudioLabLoadProject(Session session, JObject input)
    {
        try
        {
            await Task.CompletedTask;
            string name = input["name"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return AudioLab.CreateErrorResponse("Missing 'name' parameter", "missing_name");
            }
            string json = session.User.GetGenericData(DawProjectDataName, name);
            if (json is null)
            {
                return AudioLab.CreateErrorResponse($"No project named '{name}'", "not_found");
            }
            return AudioLab.CreateSuccessResponse(new JObject() { ["name"] = name, ["project_json"] = json });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] LoadProject failed: {ex.Message}");
            return AudioLab.CreateErrorResponse($"Load failed: {ex.Message}", "load_failed");
        }
    }

    /// <summary>List the user's saved DAW project names.</summary>
    public static async Task<JObject> AudioLabListProjects(Session session, JObject input)
    {
        try
        {
            await Task.CompletedTask;
            List<string> names = session.User.ListAllGenericData(DawProjectDataName);
            return AudioLab.CreateSuccessResponse(new JObject() { ["projects"] = JArray.FromObject(names ?? []) });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] ListProjects failed: {ex.Message}");
            return AudioLab.CreateErrorResponse($"List failed: {ex.Message}", "list_failed");
        }
    }

    /// <summary>Delete a saved DAW project.</summary>
    public static async Task<JObject> AudioLabDeleteProject(Session session, JObject input)
    {
        try
        {
            await Task.CompletedTask;
            string name = input["name"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return AudioLab.CreateErrorResponse("Missing 'name' parameter", "missing_name");
            }
            session.User.DeleteGenericData(DawProjectDataName, name);
            return AudioLab.CreateSuccessResponse(new JObject() { ["name"] = name });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] DeleteProject failed: {ex.Message}");
            return AudioLab.CreateErrorResponse($"Delete failed: {ex.Message}", "delete_failed");
        }
    }

    #endregion

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

            if (input["args"] is not JObject argsObj || !argsObj.Properties().Any())
            {
                return AudioLab.CreateErrorResponse("args is required and must be a non-empty object", "missing_args");
            }
            Dictionary<string, object> args = [];
            foreach (JProperty prop in argsObj.Properties())
            {
                // This endpoint is a generic passthrough, so it can't validate per-provider shapes — but an
                // oversized base64 blob must be rejected before it reaches a provider or an upstream API.
                if (prop.Value?.Type == JTokenType.String)
                {
                    string value = prop.Value.ToString();
                    if (value.Length > MaxArgStringLength)
                    {
                        return AudioLab.CreateErrorResponse(
                            $"args.{prop.Name} is {value.Length / (1024 * 1024)} MB, over the {MaxArgStringLength / (1024 * 1024)} MB limit for a single argument.",
                            "arg_too_large");
                    }
                }
                args[prop.Name] = prop.Value?.ToObject<object>();
            }

            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, session?.User);
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

            long sttStart = Environment.TickCount64;
            JObject result = await AudioServerManager.Instance.ProcessAsync(sttProvider, args, session?.User);

            if (result["success"]?.Value<bool>() == true)
            {
                STTResponse response = new()
                {
                    Success = true,
                    Transcription = result["text"]?.ToString() ?? "",
                    Confidence = result["confidence"]?.Value<float>() ?? 0f,
                    Language = request.Language,
                    ProcessingTime = (Environment.TickCount64 - sttStart) / 1000.0,
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

            string requestedProvider = input["provider_id"]?.ToString();
            Logs.Debug($"[AudioLab] ProcessTTS: requested provider_id='{requestedProvider}'");
            AudioProviderDefinition ttsProvider = !string.IsNullOrEmpty(requestedProvider)
                ? AudioProviderRegistry.GetById(requestedProvider)
                : AudioProviderRegistry.GetByCategory(AudioCategory.TTS).FirstOrDefault();

            if (ttsProvider == null)
            {
                return AudioLab.CreateErrorResponse($"No TTS provider available (requested: '{requestedProvider}')", "no_provider");
            }
            Logs.Info($"[AudioLab] ProcessTTS: routing to provider '{ttsProvider.Id}' ({ttsProvider.Name}).");

            Dictionary<string, object> args = new()
            {
                ["text"] = request.Text,
                ["voice"] = request.Voice,
                ["language"] = request.Language,
                ["volume"] = request.Volume
            };
            // Forward the zero-shot voice reference so cloning providers (F5, VibeVoice, …) can actually run
            // via the API — the TtsHandler reads args["reference_audio"] (base64 WAV) + args["ref_text"].
            if (input["reference_audio"] is JToken refAudio && refAudio.Type != JTokenType.Null)
                args["reference_audio"] = refAudio.ToString();
            if (input["ref_text"] is JToken refText && refText.Type != JTokenType.Null)
                args["ref_text"] = refText.ToString();

            long ttsStart = Environment.TickCount64;
            JObject result = await AudioServerManager.Instance.ProcessAsync(ttsProvider, args, session?.User);

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
                    ProcessingTime = (Environment.TickCount64 - ttsStart) / 1000.0,
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
                // Unknown step types used to fall through to TTS, so an "llm" or misspelled step silently ran
                // as text-to-speech and the response still reported success.
                AudioCategory? mapped = step.Type.ToLowerInvariant() switch
                {
                    "stt" => AudioCategory.STT,
                    "tts" => AudioCategory.TTS,
                    _ => null
                };
                if (mapped is null)
                {
                    results[step.Type] = new JObject { ["success"] = false, ["error"] = $"Unsupported workflow step type '{step.Type}'. Supported: stt, tts." };
                    continue;
                }
                AudioCategory category = mapped.Value;

                AudioProviderDefinition provider = AudioProviderRegistry.GetByCategory(category)
                    .FirstOrDefault();

                if (provider == null)
                {
                    results[step.Type] = new JObject { ["success"] = false, ["error"] = $"No {category} provider is registered to run step '{step.Type}'." };
                    continue;
                }

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

                JObject stepResult = await AudioServerManager.Instance.ProcessAsync(provider, args, session?.User);
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

            // A step that never ran, or ran and failed, must not be reported as an unqualified success.
            List<string> failedSteps = [.. request.Steps.Where(s => s.Enabled).Select(s => s.Type)
                .Where(t => !executedSteps.Contains(t)
                    || (results.TryGetValue(t, out object r) && r is JObject ro && ro["success"]?.Value<bool>() == false))
                .Distinct()];
            return new JObject
            {
                ["success"] = failedSteps.Count == 0,
                ["message"] = failedSteps.Count == 0
                    ? "Workflow completed successfully"
                    : $"Workflow completed with {failedSteps.Count} failed step(s): {string.Join(", ", failedSteps)}",
                ["failed_steps"] = JArray.FromObject(failedSteps),
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
                    ["model_count"] = provider.Models.Count
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

    /// <summary>Get install status for all providers. "Installed" now means the provider's models are
    /// registered (engine-backed or API), reported from the running backend — no Python/venv involved.</summary>
    public static async Task<JObject> GetInstallationStatus(Session session, JObject input)
    {
        try
        {
            await Task.CompletedTask;
            DynamicAudioBackend backend = Program.Backends.AllBackends.Values
                .Select(b => b.AbstractBackend as DynamicAudioBackend)
                .FirstOrDefault(b => b is not null);
            IReadOnlySet<string> installedIds = backend?.GetInstalledEngineIds() ?? new HashSet<string>();
            bool engineAvailable = AudioEngineBridge.Available;

            JObject providerStatuses = [];
            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
            {
                providerStatuses[provider.Id] = installedIds.Contains(provider.Id);
            }

            return new JObject
            {
                ["success"] = true,
                ["engine_available"] = engineAvailable,
                ["engine_ready"] = engineAvailable && AudioEngineBridge.EngineReady(),
                ["providers"] = providerStatuses
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Failed to check installation status", "status_error", ex);
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

            JArray engines = [];
            foreach (AudioProviderDefinition provider in AudioProviderRegistry.All)
            {
                // Legacy Docker/Python engines aren't ported to the in-process C# engine, so they can't run in
                // this build — surface them as incompatible instead of installable.
                bool platformCompatible = !provider.RequiresDocker;
                string platformNote = provider.RequiresDocker
                    ? "Legacy Docker-based engine — not available in the in-process build."
                    : "";

                // Self-managed (HF auto-download) providers fetch weights on first use, so per-model
                // presence is meaningless — the UI shows "Downloads on first use" instead of install buttons.
                bool selfManaged = AudioEngineBridge.ProviderManagesOwnWeights(provider.Id);

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
                        ["estimated_vram"] = modelDef.EstimatedVram,
                        // Name in Swarm's model registry, for generating via the core T2I pipeline.
                        ["swarm_model"] = provider.GetFullModelName(modelDef.Id),
                        // Per-model: are this variant's weights on disk? (API/self-managed report true.)
                        ["installed"] = provider.IsApiProvider || AudioEngineBridge.WeightsPresent(provider.Id, modelDef.Id)
                    });
                }

                engines.Add(new JObject
                {
                    ["id"] = provider.Id,
                    ["name"] = provider.Name,
                    ["category"] = provider.Category.ToString(),
                    ["engine_group"] = provider.EngineGroup,
                    ["requires_docker"] = provider.RequiresDocker,
                    ["is_api_provider"] = provider.IsApiProvider,
                    ["api_key_settings_id"] = provider.ApiKeySettingsId,
                    ["platform_compatible"] = platformCompatible,
                    ["platform_note"] = platformNote,
                    ["installed"] = installedIds.Contains(provider.Id),
                    // Installed but weights absent on disk (e.g. user freed space) — UI shows a repair prompt.
                    // In-process C# engines have no Python dependencies; only the model weights download.
                    ["in_process"] = !provider.IsApiProvider,
                    // Weights fetched on first use — no explicit per-model install/remove for this engine.
                    ["self_managed"] = selfManaged,
                    // Capability flags (e.g. tts_voice_ref) so the UI only offers what the engine supports.
                    ["features"] = JArray.FromObject(provider.FeatureFlags),
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

    /// <summary>Installs an audio engine via WebSocket with streaming progress. Optional
    /// <paramref name="model_id"/> installs one specific model's weights (variants are distinct multi-GB
    /// checkpoints); without it the provider's default model set is fetched.
    /// Follows the DoModelDownloadWS pattern: WebSocket parameter auto-detected by API framework.</summary>
    public static async Task<JObject> AudioLabInstallEngine(Session session, WebSocket ws, string provider_id, string model_id = null)
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

            // A provider being installed doesn't mean THIS model's weights exist (per-model checkpoints) —
            // only short-circuit the no-model-id form.
            if (string.IsNullOrEmpty(model_id) && backend.GetInstalledEngineIds().Contains(provider_id))
            {
                await ws.SendJson(new JObject { ["success"] = true, ["message"] = $"{provider.Name} is already installed." }, API.WebsocketTimeout);
                return null;
            }

            bool success = await backend.InstallAndRegisterEngine(provider_id, async msg =>
            {
                Logs.Info($"[AudioLab] Install progress: {msg}");
                await ws.SendJson(new JObject { ["info"] = msg }, API.WebsocketTimeout);
            }, Program.GlobalProgramCancel, model_id);

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

    /// <summary>Uninstalls an audio engine: removes models from registry and persists the change. When
    /// <paramref name="delete_weights"/> is true, also deletes the provider's downloaded weights from disk
    /// (shared side-model caches are retained). When <paramref name="model_id"/> is given, only that one
    /// variant's weights are deleted — the engine stays installed and its other models are untouched.</summary>
    public static async Task<JObject> AudioLabUninstallEngine(Session session, string provider_id, bool delete_weights = false, string model_id = null)
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

            // Per-model remove: delete just this variant's weights, leave the engine installed.
            if (!string.IsNullOrEmpty(model_id))
            {
                AudioModelDefinition modelDef = provider.Models.FirstOrDefault(m => string.Equals(m.Id, model_id, StringComparison.OrdinalIgnoreCase));
                if (modelDef == null)
                {
                    return AudioLab.CreateErrorResponse($"Unknown model '{model_id}' for provider {provider.Name}", "unknown_model");
                }
                backend.DeleteModelWeights(provider_id, model_id);
                return new JObject
                {
                    ["success"] = true,
                    ["provider_id"] = provider_id,
                    ["model_id"] = model_id,
                    ["deleted_weights"] = true,
                    ["message"] = $"{modelDef.Name} weights removed."
                };
            }

            backend.UnregisterEngine(provider_id, delete_weights);

            return new JObject
            {
                ["success"] = true,
                ["provider_id"] = provider_id,
                ["deleted_weights"] = delete_weights,
                ["message"] = delete_weights
                    ? $"{provider.Name} removed and weights deleted."
                    : $"{provider.Name} removed successfully."
            };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse($"Engine uninstall failed: {ex.Message}", "uninstall_error", ex);
        }
    }

    /// <summary>Installs every not-yet-present model for a provider, streaming progress over the WebSocket.
    /// Skips models whose weights are already on disk. This is the server-side "Download All" — the UI and
    /// API testers call it instead of looping <see cref="AudioLabInstallEngine"/> per model.</summary>
    public static async Task<JObject> AudioLabInstallAllModels(Session session, WebSocket ws, string provider_id)
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
            List<AudioModelDefinition> pending = provider.Models
                .Where(m => !(provider.IsApiProvider || AudioEngineBridge.WeightsPresent(provider.Id, m.Id)))
                .ToList();
            if (pending.Count == 0)
            {
                await ws.SendJson(new JObject { ["success"] = true, ["provider_id"] = provider_id, ["installed"] = 0, ["total"] = 0, ["message"] = $"All {provider.Name} models are already installed." }, API.WebsocketTimeout);
                return null;
            }
            int ok = 0;
            foreach (AudioModelDefinition model in pending)
            {
                await ws.SendJson(new JObject { ["info"] = $"Installing {model.Name}…" }, API.WebsocketTimeout);
                bool success = await backend.InstallAndRegisterEngine(provider_id,
                    async msg => await ws.SendJson(new JObject { ["info"] = msg }, API.WebsocketTimeout),
                    Program.GlobalProgramCancel, model.Id);
                if (success)
                {
                    ok++;
                    await ws.SendJson(new JObject { ["info"] = $"{model.Name} installed.", ["model_id"] = model.Id, ["model_done"] = true }, API.WebsocketTimeout);
                }
                else
                {
                    await ws.SendJson(new JObject { ["info"] = $"Failed to install {model.Name}.", ["model_id"] = model.Id, ["model_failed"] = true }, API.WebsocketTimeout);
                }
            }
            await ws.SendJson(new JObject { ["success"] = true, ["provider_id"] = provider_id, ["installed"] = ok, ["total"] = pending.Count, ["message"] = $"Installed {ok}/{pending.Count} {provider.Name} models." }, API.WebsocketTimeout);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Install-all failed: {ex}");
            await ws.SendJson(new JObject { ["error"] = $"Install-all failed: {ex.Message}" }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Deletes the weights of every installed model for a provider (the engine stays installed).
    /// Server-side "Remove All". Fast (disk deletes only), so it's a plain call rather than a WebSocket.</summary>
    public static async Task<JObject> AudioLabRemoveAllModels(Session session, string provider_id)
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
            List<AudioModelDefinition> present = provider.Models
                .Where(m => !provider.IsApiProvider && AudioEngineBridge.WeightsPresent(provider.Id, m.Id))
                .ToList();
            if (present.Count == 0)
            {
                return new JObject { ["success"] = true, ["provider_id"] = provider_id, ["removed"] = 0, ["total"] = 0, ["message"] = $"No {provider.Name} models are installed." };
            }
            JArray removedIds = [];
            foreach (AudioModelDefinition model in present)
            {
                try { backend.DeleteModelWeights(provider_id, model.Id); removedIds.Add(model.Id); }
                catch (Exception ex) { Logs.Warning($"[AudioLab] Failed to remove {model.Name}: {ex.Message}"); }
            }
            return new JObject { ["success"] = true, ["provider_id"] = provider_id, ["removed"] = removedIds.Count, ["total"] = present.Count, ["removed_ids"] = removedIds, ["message"] = $"Removed {removedIds.Count}/{present.Count} {provider.Name} models." };
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse($"Remove-all failed: {ex.Message}", "remove_all_error", ex);
        }
    }

    #endregion

    #region Audio Format Conversion

    /// <summary>Convert audio between formats using FFMpegCore. Accepts WAV base64, returns converted base64.</summary>
    public static async Task<JObject> ConvertAudioFormat(Session session, JObject input)
    {
        try
        {
            string audioData = input["audio_data"]?.ToString();
            string targetFormat = input["format"]?.ToString()?.ToLowerInvariant() ?? "mp3";
            if (string.IsNullOrEmpty(audioData))
            {
                return AudioLab.CreateErrorResponse("audio_data is required", "missing_audio");
            }

            string[] supportedFormats = ["mp3", "ogg", "flac", "wav", "aac", "m4a"];
            if (!supportedFormats.Contains(targetFormat))
            {
                return AudioLab.CreateErrorResponse($"Unsupported format: {targetFormat}. Supported: {string.Join(", ", supportedFormats)}", "unsupported_format");
            }

            string ffmpeg = Utilities.FfmegLocation.Value;
            if (string.IsNullOrEmpty(ffmpeg))
            {
                return AudioLab.CreateErrorResponse("ffmpeg not found. Install ffmpeg and ensure it is in your PATH.", "ffmpeg_not_found");
            }

            // Decode input base64 to temp WAV file
            byte[] audioBytes = Convert.FromBase64String(audioData);
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audiolab");
            System.IO.Directory.CreateDirectory(tempDir);
            string inputPath = System.IO.Path.Combine(tempDir, $"convert_input_{Guid.NewGuid()}.wav");
            string outputPath = System.IO.Path.Combine(tempDir, $"convert_output_{Guid.NewGuid()}.{targetFormat}");

            try
            {
                await System.IO.File.WriteAllBytesAsync(inputPath, audioBytes);

                // Build ffmpeg args for format conversion
                List<string> args = ["-i", inputPath, "-y"];
                switch (targetFormat)
                {
                    case "mp3":
                        args.AddRange(["-codec:a", "libmp3lame", "-b:a", "192k"]);
                        break;
                    case "ogg":
                        args.AddRange(["-codec:a", "libvorbis", "-q:a", "6"]);
                        break;
                    case "flac":
                        args.AddRange(["-codec:a", "flac"]);
                        break;
                    case "aac":
                    case "m4a":
                        args.AddRange(["-codec:a", "aac", "-b:a", "192k"]);
                        break;
                    case "wav":
                        args.AddRange(["-codec:a", "pcm_s16le", "-ar", "44100", "-ac", "2"]);
                        break;
                }
                args.Add(outputPath);

                string result = await Utilities.QuickRunProcess(ffmpeg, [.. args]);
                Logs.Debug($"[AudioLab] ffmpeg convert output: {result}");

                if (!System.IO.File.Exists(outputPath))
                {
                    return AudioLab.CreateErrorResponse("Conversion failed - no output file produced", "conversion_failed");
                }

                byte[] outputBytes = await System.IO.File.ReadAllBytesAsync(outputPath);
                string outputBase64 = Convert.ToBase64String(outputBytes);

                string mimeType = targetFormat switch
                {
                    "mp3" => "audio/mpeg",
                    "ogg" => "audio/ogg",
                    "flac" => "audio/flac",
                    "aac" or "m4a" => "audio/mp4",
                    "wav" => "audio/wav",
                    _ => "audio/octet-stream"
                };

                return AudioLab.CreateSuccessResponse(new JObject
                {
                    ["audio_data"] = outputBase64,
                    ["format"] = targetFormat,
                    ["mime_type"] = mimeType,
                    ["size"] = outputBytes.Length
                });
            }
            finally
            {
                if (System.IO.File.Exists(inputPath)) System.IO.File.Delete(inputPath);
                if (System.IO.File.Exists(outputPath)) System.IO.File.Delete(outputPath);
            }
        }
        catch (FormatException)
        {
            return AudioLab.CreateErrorResponse("Invalid base64 audio data", "invalid_audio");
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Audio conversion failed", "conversion_error", ex);
        }
    }

    #endregion

    #region Request Parsing

    /// <summary>Parses and validates an STT request from raw JSON input.</summary>
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

    /// <summary>Parses and validates a TTS request from raw JSON input.</summary>
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

    /// <summary>Parses and validates a workflow request from raw JSON input.</summary>
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
