using Hartsy.Extensions.VoiceAssistant.Services;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.VoiceAssistant.SwarmBackends;

/// <summary>Base class for Voice Assistant backends. Provides common functionality for STT and TTS backends with direct Python integration.</summary>
public abstract class VoiceAssistantBackends : AbstractT2IBackend
{
    /// <summary>Collection of supported features for this backend</summary>
    protected readonly HashSet<string> SupportedFeatureSet = [];

    /// <summary>The backend type this instance manages</summary>
    protected abstract ServiceConfiguration.BackendType BackendType { get; }

    /// <summary>Gets the supported features for this backend (implemented by derived classes)</summary>
    public abstract override IEnumerable<string> SupportedFeatures { get; }

    /// <summary>Initialize the voice backend (implemented by derived classes)</summary>
    public abstract override Task Init();

    /// <summary>Load a model for voice processing</summary>
    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        try
        {
            Logs.Verbose($"[VoiceAssistant] {GetType().Name} - Loading model: {model.Name}");

            // Validate model compatibility
            if (!IsModelCompatible(model))
            {
                Logs.Warning($"[VoiceAssistant] {GetType().Name} - Model not compatible: {model.Name}");
                return false;
            }
            CurrentModelName = model.Name;
            Logs.Verbose($"[VoiceAssistant] {GetType().Name} - Successfully loaded model: {model.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] {GetType().Name} - Error loading model: {ex.Message}");
            return false;
        }
    }

    /// <summary>Generate voice processing results (implemented by derived classes)</summary>
    public abstract override Task<Image[]> Generate(T2IParamInput input);

    /// <summary>Check if this backend can handle the specified model</summary>
    protected virtual bool IsModelCompatible(T2IModel model)
    {
        if (model == null) return false;

        string modelName = model.Name?.ToLowerInvariant();
        ServiceConfiguration.BackendType backendType = BackendType;

        return backendType switch
        {
            ServiceConfiguration.BackendType.STT => IsSTTModel(modelName),
            ServiceConfiguration.BackendType.TTS => IsTTSModel(modelName),
            _ => IsVoiceModel(modelName)
        };
    }

    /// <summary>Check if model name indicates an STT model</summary>
    protected virtual bool IsSTTModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        return modelName.Contains("whisper") ||
               modelName.Contains("stt") ||
               modelName.Contains("speech") ||
               modelName.Contains("transcrib") ||
               modelName.Contains("wav2vec") ||
               modelName.Contains("deepspeech");
    }

    /// <summary>Check if model name indicates a TTS model</summary>
    protected virtual bool IsTTSModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        return modelName.Contains("tts") ||
               modelName.Contains("voice") ||
               modelName.Contains("speak") ||
               modelName.Contains("synthesis") ||
               modelName.Contains("chatterbox") ||
               modelName.Contains("bark") ||
               modelName.Contains("tortoise");
    }

    /// <summary>Check if model name indicates any voice model</summary>
    protected virtual bool IsVoiceModel(string modelName)
    {
        return IsSTTModel(modelName) || IsTTSModel(modelName);
    }

    /// <summary>Free memory and resources</summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] {GetType().Name} - Freeing memory (systemRam: {systemRam})");

            // Cleanup Python resources
            JObject result = await PythonVoiceProcessor.Instance.CleanupAsync();
            bool success = result["success"]?.Value<bool>() ?? false;

            if (!success)
            {
                Logs.Warning($"[VoiceAssistant] {GetType().Name} - Python cleanup returned error: {result["error"]}");
            }

            return success;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VoiceAssistant] {GetType().Name} - Failed to free memory: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shutdown the backend</summary>
    public override async Task Shutdown()
    {
        try
        {
            Logs.Info($"[VoiceAssistant] {GetType().Name} - Shutting down backend");
            Status = BackendStatus.DISABLED;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] {GetType().Name} - Error during shutdown: {ex.Message}");
        }
    }

    /// <summary>Get current backend status information</summary>
    public virtual async Task<Dictionary<string, object>> GetBackendStatusAsync()
    {
        try
        {
            JObject pythonStatus = await PythonVoiceProcessor.Instance.GetVoiceStatusAsync();

            return new Dictionary<string, object>
            {
                { "backend_type", BackendType.ToString() },
                { "is_running", await IsBackendRunningAsync() },
                { "current_model", CurrentModelName ?? "None" },
                { "supported_features", SupportedFeatures.ToList() },
                { "status", Status.ToString() },
                { "python_status", pythonStatus }
            };
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VoiceAssistant] {GetType().Name} - Error getting backend status: {ex.Message}");
            return new Dictionary<string, object>
            {
                { "backend_type", BackendType.ToString() },
                { "is_running", false },
                { "current_model", CurrentModelName ?? "None" },
                { "supported_features", SupportedFeatures.ToList() },
                { "status", Status.ToString() },
                { "error", ex.Message }
            };
        }
    }

    /// <summary>Check if the backend is currently running</summary>
    protected async Task<bool> IsBackendRunningAsync()
    {
        try
        {
            JObject status = await PythonVoiceProcessor.Instance.GetVoiceStatusAsync();

            string serviceKey = BackendType == ServiceConfiguration.BackendType.STT ? "stt_available" : "tts_available";

            return status["success"]?.Value<bool>() == true &&
                   status[serviceKey]?.Value<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ensure the Python voice processor is ready for this backend type</summary>
    protected async Task EnsureBackendServiceHealthyAsync()
    {
        try
        {
            // Check if processor is initialized
            JObject processorStatus = PythonVoiceProcessor.Instance.GetProcessorStatus();

            if (processorStatus["initialized"]?.Value<bool>() != true)
            {
                throw new InvalidOperationException($"{BackendType} backend processor not initialized");
            }

            // Check if specific service is available
            JObject voiceStatus = await PythonVoiceProcessor.Instance.GetVoiceStatusAsync();

            if (voiceStatus["success"]?.Value<bool>() != true)
            {
                string error = voiceStatus["error"]?.ToString() ?? "Unknown error";
                throw new InvalidOperationException($"{BackendType} backend check failed: {error}");
            }

            string serviceKey = BackendType == ServiceConfiguration.BackendType.STT ? "stt_available" : "tts_available";

            if (voiceStatus[serviceKey]?.Value<bool>() != true)
            {
                throw new InvalidOperationException($"{BackendType} service is not available in Python backend");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] {GetType().Name} - Backend service check failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Check if dependencies are installed for this backend type</summary>
    protected async Task<bool> CheckDependenciesAsync()
    {
        try
        {
            DependencyInstaller installer = new();
            PythonEnvironmentInfo pythonInfo = installer.DetectPythonEnvironment();

            if (pythonInfo?.IsValid != true)
            {
                Logs.Warning($"[VoiceAssistant] {GetType().Name} - Python environment not detected");
                return false;
            }

            bool dependenciesInstalled = await installer.CheckDependenciesInstalledAsync(pythonInfo, BackendType);
            return dependenciesInstalled;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] {GetType().Name} - Error checking dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>Get dependency status for this backend type</summary>
    protected async Task<JObject> GetDependencyStatusAsync()
    {
        try
        {
            DependencyInstaller installer = new();
            PythonEnvironmentInfo pythonInfo = installer.DetectPythonEnvironment();

            if (pythonInfo?.IsValid != true)
            {
                return new JObject
                {
                    ["backend_type"] = BackendType.ToString().ToLowerInvariant(),
                    ["python_environment"] = "not_detected",
                    ["dependencies_installed"] = false
                };
            }

            JObject dependencyStatus = await installer.GetDetailedInstallationStatusAsync(pythonInfo, BackendType);
            dependencyStatus["backend_type"] = BackendType.ToString().ToLowerInvariant();
            dependencyStatus["python_environment"] = "detected";

            return dependencyStatus;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] {GetType().Name} - Error getting dependency status: {ex.Message}");
            return new JObject
            {
                ["backend_type"] = BackendType.ToString().ToLowerInvariant(),
                ["error"] = ex.Message,
                ["dependencies_installed"] = false
            };
        }
    }
}
