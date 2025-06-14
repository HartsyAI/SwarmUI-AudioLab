using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Net.Http;

namespace Hartsy.Extensions.VoiceAssistant.SwarmBackends;

/// <summary>Base class for Voice Assistant backends.
/// Provides common functionality for STT, TTS, and STS backends.</summary>
public abstract class VoiceAssistantBackends : AbstractT2IBackend
{
    /// <summary>Shared HttpClient for all Voice Assistant API requests</summary>
    protected static readonly HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Collection of supported features for this backend</summary>
    protected readonly HashSet<string> SupportedFeatureSet = new();

    /// <summary>Gets the supported features for this backend (implemented by derived classes)</summary>
    public abstract override IEnumerable<string> SupportedFeatures { get; }

    /// <summary>Backend type identifier (implemented by derived classes)</summary>
    public abstract string BackendType { get; }

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

        var modelName = model.Name?.ToLowerInvariant();
        var backendType = BackendType.ToLowerInvariant();

        return backendType switch
        {
            "stt" => IsSTTModel(modelName),
            "tts" => IsTTSModel(modelName),
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

            // Signal Python backend to free memory if needed
            var endpoint = BackendType.ToLowerInvariant();
            await HttpClient.PostAsync($"{Services.ServiceConfiguration.BackendUrl}/{endpoint}/free_memory", null);

            return true;
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
    public virtual async Task<Dictionary<string, object>> GetBackendStatusAsync() //TODO: This needs to call internal ProgressTracking
    {
        return new Dictionary<string, object>
        {
            { "backend_type", BackendType },
            { "is_running", Services.PythonBackendService.Instance.IsBackendRunning },
            { "current_model", CurrentModelName ?? "None" },
            { "supported_features", SupportedFeatures.ToList() },
            { "status", Status.ToString() }
        };
    }

    /// <summary>Test connection to the Python backend service</summary>
    protected async Task<bool> TestBackendConnectionAsync()
    {
        try
        {
            var response = await HttpClient.GetAsync($"{Services.ServiceConfiguration.BackendUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ensure the Python backend service is running and healthy</summary>
    protected async Task EnsureBackendServiceHealthyAsync() // TODO: This needs to call internal ProgressTracking
    {
        try
        {
            if (!Services.PythonBackendService.Instance.IsBackendRunning)
            {
                Logs.Info($"[VoiceAssistant] {GetType().Name} - Starting Python backend service");
                var startResult = await Services.PythonBackendService.Instance.StartAsync();
                if (!startResult.Success)
                {
                    throw new InvalidOperationException($"Failed to start Python backend: {startResult.Message}");
                }
            }

            // Verify service is healthy
            var healthInfo = await Services.PythonBackendService.Instance.GetHealthAsync();
            if (!healthInfo.IsHealthy)
            {
                throw new InvalidOperationException($"Python backend is not healthy: {healthInfo.ErrorMessage}");
            }

            // Verify specific service is available
            var serviceName = BackendType.ToLowerInvariant();
            if (!healthInfo.Services.GetValueOrDefault(serviceName, false))
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
}
