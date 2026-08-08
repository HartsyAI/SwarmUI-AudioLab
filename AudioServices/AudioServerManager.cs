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
/// everything else is delegated to <b>HartsyInference.Engine</b> through <see cref="AudioEngineBridge"/>, which
/// owns the model lifecycle, the compute device, and the generation itself. A provider the Engine can't service
/// yet returns a clear error.</para>
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
    /// <param name="user">The requesting user, whose stored API key is used for cloud providers.
    /// Null falls back to the local/admin user, which is only correct for single-user installs.</param>
    public async Task<JObject> ProcessAsync(AudioProviderDefinition provider, Dictionary<string, object> args, User user, CancellationToken cancelToken = default)
    {
        // Cloud API providers are handled entirely in C# by their dedicated handler.
        if (provider.IsApiProvider)
        {
            return await ProcessViaApiAsync(provider, args, user, cancelToken);
        }

        // Everything local is delegated to the Engine's typed audio services (it auto-downloads each
        // provider's weights into its own model cache on first use).
        if (AudioEngineBridge.IsProviderSupported(provider.Id))
        {
            if (!AudioEngineBridge.EngineReady())
            {
                return CreateErrorResponse($"{provider.Name} needs a compute backend, but none could be initialized. Check the SwarmUI logs for the audio engine startup error.");
            }
            return await AudioEngineBridge.ProcessAsync(provider.Id, args, cancelToken);
        }

        // Not an API provider and not yet wired into the in-process engine — name the specific blocker.
        return CreateErrorResponse(AudioUnsupportedReasons.Message(provider.Id, provider.Name));
    }

    /// <summary>Routes an API provider request to its C# handler.</summary>
    private async Task<JObject> ProcessViaApiAsync(AudioProviderDefinition provider, Dictionary<string, object> args, User user, CancellationToken cancelToken)
    {
        IApiEngineHandler handler = ApiHandlerRegistry.GetHandler(provider.Id);
        if (handler == null)
        {
            // Deliberately-unimplemented cloud providers (AWS Transcribe) have a specific documented reason.
            return CreateErrorResponse(AudioUnsupportedReasons.Message(provider.Id, provider.Name));
        }
        string apiKey = GetProviderApiKey(provider.ApiKeySettingsId, user);
        if (string.IsNullOrEmpty(apiKey))
        {
            return CreateErrorResponse($"{provider.Name} requires an API key. Set your '{provider.ApiKeySettingsId}' key in Server > User Settings > API Keys.");
        }
        try
        {
            return await handler.ProcessAsync(args, apiKey, cancelToken);
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            // Must carry `cancelled` like the engine path's AudioIo.Cancelled(), or callers treat a
            // user interrupt as a generation failure.
            return AudioIo.Cancelled();
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] API handler error for '{provider.Id}': {ex.Message}");
            return CreateErrorResponse($"API processing error: {ex.Message}");
        }
    }

    /// <summary>Retrieves a provider-specific API key from user settings.
    /// Called per-request so key changes take effect immediately.</summary>
    private static string GetProviderApiKey(string settingsId, User user)
    {
        if (string.IsNullOrEmpty(settingsId))
        {
            return "";
        }
        try
        {
            // Only fall back to the local/admin user when the caller genuinely has no session —
            // reading their key for every request leaked one user's keys to all others.
            user ??= Program.Sessions?.GetUser(SessionHandler.LocalUserID);
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
