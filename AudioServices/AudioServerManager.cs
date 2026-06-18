using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Routes audio requests to a backend — pure C#, no Python.
///
/// <para>Historically this spawned per-group Python HTTP servers; that machinery is gone. Requests now go
/// one of two ways: cloud <b>API providers</b> (ElevenLabs, OpenAI, …) run through their C# <see cref="IApiEngineHandler"/>;
/// everything else runs <b>in-process on the HartsyInference C# engine</b> via <see cref="HartsyEngineBridge"/>
/// (hosted by the sibling backend extension). A provider the engine can't service yet returns a clear error
/// rather than falling back to Python.</para>
///
/// <para>The class name is retained because many call sites use <c>AudioServerManager.Instance.ProcessAsync</c>;
/// it is no longer a "server manager" in any literal sense.</para></summary>
public class AudioServerManager
{
    private static readonly Lazy<AudioServerManager> InstanceLazy = new(() => new AudioServerManager());
    public static AudioServerManager Instance => InstanceLazy.Value;

    private AudioServerManager()
    {
        Logs.Debug("[AudioLab] AudioServerManager (C# router) created");
    }

    /// <summary>Routes a processing request to the appropriate C# backend.</summary>
    public async Task<JObject> ProcessAsync(AudioProviderDefinition provider, Dictionary<string, object> args, CancellationToken cancelToken = default)
    {
        // Cloud API providers are handled entirely in C# by their dedicated handler.
        if (provider.IsApiProvider)
        {
            return await ProcessViaApiAsync(provider, args, cancelToken);
        }

        // In-process C# inference via the HartsyInference engine (sibling backend extension).
        if (HartsyEngineBridge.Available && HartsyEngineBridge.EngineReady() && HartsyEngineBridge.IsProviderSupported(provider.Id))
        {
            if (HartsyEngineBridge.ProviderManagesOwnWeights(provider.Id))
            {
                // STT and friends: the engine downloads/manages their weights itself.
                return await HartsyEngineBridge.ProcessAsync(provider.Id, args, cancelToken);
            }
            string checkpointPath = AudioWeights.ResolveCheckpoint(provider, args);
            if (checkpointPath is not null)
            {
                Dictionary<string, object> engineArgs = new(args) { ["checkpoint_path"] = checkpointPath };
                return await HartsyEngineBridge.ProcessAsync(provider.Id, engineArgs, cancelToken);
            }
            return CreateErrorResponse($"{provider.Name} weights are not installed yet. Install the model from the AudioLab model browser first.");
        }

        // No Python fallback exists anymore.
        if (!HartsyEngineBridge.Available)
        {
            return CreateErrorResponse($"{provider.Name} runs on the HartsyInference C# engine, which isn't available. Install the 'HartsyInference (Pure C# Inference)' backend extension.");
        }
        if (!HartsyEngineBridge.EngineReady())
        {
            return CreateErrorResponse($"{provider.Name} needs the HartsyInference engine running. Add the 'HartsyInference (Pure C# Inference)' backend in Server > Backends.");
        }
        return CreateErrorResponse($"{provider.Name} is not yet supported by the in-process C# engine. Support is being added engine-side; this provider will light up automatically once it lands.");
    }

    /// <summary>Routes an API provider request to its C# handler.</summary>
    private async Task<JObject> ProcessViaApiAsync(AudioProviderDefinition provider, Dictionary<string, object> args, CancellationToken cancelToken)
    {
        IApiEngineHandler handler = ApiHandlerRegistry.GetHandler(provider.Id);
        if (handler == null)
        {
            return CreateErrorResponse($"No API handler found for provider '{provider.Id}'.");
        }
        string apiKey = GetProviderApiKey(provider.ApiKeySettingsId);
        if (string.IsNullOrEmpty(apiKey))
        {
            return CreateErrorResponse($"{provider.Name} requires an API key. Set your '{provider.ApiKeySettingsId}' key in Server > User Settings > API Keys.");
        }
        try
        {
            return await handler.ProcessAsync(args, apiKey, cancelToken);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResponse("Request cancelled by user.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] API handler error for '{provider.Id}': {ex.Message}");
            return CreateErrorResponse($"API processing error: {ex.Message}");
        }
    }

    /// <summary>Retrieves a provider-specific API key from user settings.
    /// Called per-request so key changes take effect immediately.</summary>
    private static string GetProviderApiKey(string settingsId)
    {
        if (string.IsNullOrEmpty(settingsId))
        {
            return "";
        }
        try
        {
            User user = Program.Sessions?.GetUser(SessionHandler.LocalUserID);
            return user?.GetGenericData(settingsId, "key") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Creates a standardized JSON error response.</summary>
    private static JObject CreateErrorResponse(string message)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
    }
}
