using System.Reflection;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// AudioLab's reflection bridge to the in-process C# inference engine hosted by the sibling
/// SwarmUI-HartsyInference-Backend extension. This is the replacement transport for the old Python
/// HTTP servers (<see cref="AudioServerManager"/>).
///
/// <para><b>Why reflection instead of a project/assembly reference:</b> SwarmUI loads each extension
/// into its own non-collectible <c>AssemblyLoadContext</c> (core <c>ExtensionsManager</c>). A direct
/// reference to the backend extension's assembly would not resolve from AudioLab's context (it is
/// neither a host/Default assembly nor present in AudioLab's own folder), and forcing it would load an
/// identity-divergent second copy whose statics differ from the live ones. Instead we find the backend
/// extension's <c>HartsyAudioService</c> type at runtime via <see cref="AppDomain.GetAssemblies"/>
/// (which spans every load context in the process) and invoke its static methods. This is safe because
/// the service's signatures use only Default-context types (string, IReadOnlyDictionary, Action,
/// CancellationToken, Task, and Newtonsoft's JObject — a shared host dependency), so type identity is
/// consistent on both sides of the boundary.</para>
///
/// All members are resilient to the backend extension being absent or an older/newer contract: when the
/// type or a method can't be resolved, <see cref="Available"/> is false and callers fall back to their
/// existing path (during the migration, the Python servers; afterward, a clean "engine unavailable"
/// error).
/// </summary>
public static class HartsyEngineBridge
{
    /// <summary>Fully-qualified name of the boundary type exposed by the backend extension.</summary>
    private const string ServiceTypeName = "Hartsy.Extensions.HartsyInferenceBackend.Audio.HartsyAudioService";

    /// <summary>Minimum boundary contract version AudioLab knows how to talk to.</summary>
    private const int MinSupportedContractVersion = 1;

    private static readonly object _lock = new();
    private static bool _resolved;
    private static Type _serviceType;
    private static int _contractVersion;
    private static MethodInfo _isProviderSupported;
    private static MethodInfo _processAsync;
    private static MethodInfo _ensureWeightsAsync;
    private static MethodInfo _unload;
    // Optional (contract v2+) — tolerated absent so an older backend still works for the v1 surface.
    private static MethodInfo _engineReady;
    private static MethodInfo _providerManagesOwnWeights;

    /// <summary>Locates the backend extension's audio service across all load contexts and caches its
    /// reflection handles. Idempotent and thread-safe; a failed resolve is cached as "unavailable".</summary>
    private static void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }
        lock (_lock)
        {
            if (_resolved)
            {
                return;
            }
            try
            {
                Type type = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // Skip dynamic assemblies (no GetType backing) and keep the scan cheap.
                    if (asm.IsDynamic)
                    {
                        continue;
                    }
                    type = asm.GetType(ServiceTypeName, throwOnError: false);
                    if (type is not null)
                    {
                        break;
                    }
                }

                if (type is null)
                {
                    Logs.Debug("[AudioLab] HartsyInference engine boundary not found — backend extension absent or not yet loaded.");
                    return;
                }

                int version = (int)(type.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() ?? 0);
                if (version < MinSupportedContractVersion)
                {
                    Logs.Warning($"[AudioLab] HartsyInference audio boundary contract v{version} is older than the minimum v{MinSupportedContractVersion} AudioLab supports. Treating engine as unavailable.");
                    return;
                }

                MethodInfo isSupported = type.GetMethod("IsProviderSupported", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
                MethodInfo process = type.GetMethod("ProcessAsync", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IReadOnlyDictionary<string, object>), typeof(CancellationToken)]);
                MethodInfo ensure = type.GetMethod("EnsureWeightsAsync", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string), typeof(Action<string>), typeof(CancellationToken)]);
                MethodInfo unload = type.GetMethod("Unload", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);

                if (isSupported is null || process is null || ensure is null || unload is null)
                {
                    Logs.Warning("[AudioLab] HartsyInference audio boundary is missing expected methods — version mismatch? Treating engine as unavailable.");
                    return;
                }

                _serviceType = type;
                _contractVersion = version;
                _isProviderSupported = isSupported;
                _processAsync = process;
                _ensureWeightsAsync = ensure;
                _unload = unload;
                // Optional v2 methods — absence just means "treat as not-ready / not-self-managed".
                _engineReady = type.GetMethod("EngineReady", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                _providerManagesOwnWeights = type.GetMethod("ProviderManagesOwnWeights", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
                Logs.Info($"[AudioLab] Connected to HartsyInference in-process audio engine (boundary contract v{version}).");
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Failed to resolve HartsyInference audio boundary: {ex.Message}");
            }
            finally
            {
                _resolved = true;
            }
        }
    }

    /// <summary>Forces a fresh resolve on the next access. Call if the backend extension can load after
    /// AudioLab (load-order independence).</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _resolved = false;
            _serviceType = null;
            _isProviderSupported = _processAsync = _ensureWeightsAsync = _unload = null;
            _engineReady = _providerManagesOwnWeights = null;
        }
    }

    /// <summary>Whether a HartsyInference engine instance is up and ready to run audio. False if the
    /// boundary is unavailable or predates the v2 contract — callers then keep their existing path.</summary>
    public static bool EngineReady()
    {
        EnsureResolved();
        if (_engineReady is null)
        {
            return false;
        }
        try
        {
            return (bool)_engineReady.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] HartsyInference EngineReady() threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>Whether the engine downloads/manages this provider's weights itself (STT), so no local
    /// checkpoint is required to route to it. False (the safe default) if unknown or pre-v2.</summary>
    public static bool ProviderManagesOwnWeights(string providerId)
    {
        EnsureResolved();
        if (_providerManagesOwnWeights is null)
        {
            return false;
        }
        try
        {
            return (bool)_providerManagesOwnWeights.Invoke(null, [providerId]);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] HartsyInference ProviderManagesOwnWeights('{providerId}') threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>True if the in-process C# engine boundary is present and speaks a compatible contract.</summary>
    public static bool Available
    {
        get
        {
            EnsureResolved();
            return _serviceType is not null;
        }
    }

    /// <summary>The resolved boundary contract version, or 0 if unavailable.</summary>
    public static int ContractVersion
    {
        get
        {
            EnsureResolved();
            return _contractVersion;
        }
    }

    /// <summary>Whether the C# engine can service the given AudioLab provider id right now. False if the
    /// engine boundary is unavailable.</summary>
    public static bool IsProviderSupported(string providerId)
    {
        EnsureResolved();
        if (_isProviderSupported is null)
        {
            return false;
        }
        try
        {
            return (bool)_isProviderSupported.Invoke(null, [providerId]);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] HartsyInference IsProviderSupported('{providerId}') threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>Runs an audio request through the engine. Returns the same JObject shape AudioLab's
    /// generation path already parses. Throws if the boundary is unavailable (callers should gate on
    /// <see cref="Available"/> / <see cref="IsProviderSupported"/> first).</summary>
    public static Task<JObject> ProcessAsync(string providerId, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        EnsureResolved();
        if (_processAsync is null)
        {
            throw new InvalidOperationException("HartsyInference audio engine boundary is unavailable.");
        }
        return (Task<JObject>)_processAsync.Invoke(null, [providerId, args, cancel]);
    }

    /// <summary>Ensures model weights for a provider/model are present on disk (replaces the pip-install
    /// step). <paramref name="onProgress"/> receives human-readable status lines.</summary>
    public static Task<JObject> EnsureWeightsAsync(string providerId, string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        EnsureResolved();
        if (_ensureWeightsAsync is null)
        {
            throw new InvalidOperationException("HartsyInference audio engine boundary is unavailable.");
        }
        return (Task<JObject>)_ensureWeightsAsync.Invoke(null, [providerId, modelId, onProgress, cancel]);
    }

    /// <summary>Drops a provider/model's resident pipeline from the engine's shared cache to free VRAM.</summary>
    public static void Unload(string providerId, string modelId)
    {
        EnsureResolved();
        if (_unload is null)
        {
            return;
        }
        try
        {
            _unload.Invoke(null, [providerId, modelId]);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] HartsyInference Unload('{providerId}','{modelId}') threw: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
