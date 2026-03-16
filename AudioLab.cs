using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioBackends;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab;

/// <summary>SwarmUI AudioLab Extension - Main Entry Point.
/// Provides modular audio processing (TTS, STT, music gen, voice cloning, etc.)
/// through a provider-based architecture integrated into SwarmUI's Generate tab.</summary>
public class AudioLab : Extension
{
    /// <summary>Current extension version.</summary>
    public static new readonly string Version = "4.0.0";

    /// <summary>Pre-initialization — registers providers and web assets before SwarmUI core is ready.</summary>
    public override void OnPreInit()
    {
        try
        {
            // Set extension directory for Python path resolution
            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            AudioConfiguration.ExtensionDirectory = Path.GetFullPath(Path.Combine(projectRoot, "Extensions", "SwarmUI-AudioLab"));
            Logs.Info($"[AudioLab] Extension directory: {AudioConfiguration.ExtensionDirectory}");

            // Ensure centralized model storage directories exist
            string audioModelRoot = Path.GetFullPath(AudioConfiguration.ModelRoot);
            foreach (string sub in new[] { "tts", "stt", "music", "clone", "fx", ".cache" })
            {
                Directory.CreateDirectory(Path.Combine(audioModelRoot, sub));
            }
            Logs.Info($"[AudioLab] Audio model root: {audioModelRoot}");

            // Register all built-in audio providers
            AudioProviderDefinitions.RegisterAll();
            Logs.Info($"[AudioLab] Registered {AudioProviderDefinitions.All.Count} audio providers");

            // Register web assets — libraries first, then DAW modules, then integration
            ScriptFiles.Add("Assets/lib/wavesurfer.min.js");
            ScriptFiles.Add("Assets/lib/wavesurfer-record.min.js");
            ScriptFiles.Add("Assets/lib/wavesurfer-regions.min.js");
            ScriptFiles.Add("Assets/lib/wavesurfer-timeline.min.js");
            ScriptFiles.Add("Assets/lib/wavesurfer-minimap.min.js");
            ScriptFiles.Add("Assets/lib/crunker.min.js");
            ScriptFiles.Add("Assets/audio-player.js");
            ScriptFiles.Add("Assets/audio-api.js");
            ScriptFiles.Add("Assets/audio-core.js");
            ScriptFiles.Add("Assets/audio-daw-timeline.js");
            ScriptFiles.Add("Assets/audio-daw-track.js");
            ScriptFiles.Add("Assets/audio-daw-mixer.js");
            ScriptFiles.Add("Assets/audio-daw.js");
            ScriptFiles.Add("Assets/audio-editor.js");
            ScriptFiles.Add("Assets/audio-integration.js");
            StyleSheetFiles.Add("Assets/audio-lab.css");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Critical error during pre-initialization: {ex.Message}");
        }
    }

    /// <summary>Main initialization — registers backend, T2I params, feature flags, and API endpoints.</summary>
    public override async void OnInit()
    {
        // Register T2I parameters for audio workflows (TTS, STT, Music, Clone, FX, SFX)
        AudioLabParams.RegisterAll();
        Logs.Info("[AudioLab] Registered audio T2I parameters");

        // Register feature flags so SwarmUI knows these are extension-managed
        RegisterFeatureFlags();
        Logs.Info("[AudioLab] Registered feature flags");

        // Register ONE unified backend
        Program.Backends.RegisterBackendType<DynamicAudioBackend>(
            "audio-backend", "Audio Backend",
            "Dynamic audio backend supporting TTS, STT, music generation, and more.", true);

        // Register API endpoints
        AudioLabAPI.Register();
        VideoAudioEndpoints.Register();
    }

    /// <summary>Registers all feature flags that should be disregarded for audio backends.
    /// Mirrors the pattern from SwarmUI-API-Backends RegisterFeatureFlags().</summary>
    private static void RegisterFeatureFlags()
    {
        // Category-level flags (one per AudioCategory)
        string[] categoryFlags = ["audiolab_tts", "audiolab_stt", "audiolab_audiogen", "audiolab_clone", "audiolab_audioproc"];

        // Per-provider flags from each provider's FeatureFlags list
        string[] providerFlags = AudioProviderRegistry.All
            .SelectMany(p => p.FeatureFlags).Distinct().ToArray();

        // Image-only features incompatible with audio models
        string[] incompatibleFlags = [
            "sampling", "zero_negative", "refiners", "controlnet", "variation_seed",
            "video", "autowebui", "comfyui", "frameinterps", "ipadapter", "sdxl",
            "dynamic_thresholding", "cascade", "sd3", "flux-dev", "seamless",
            "freeu", "teacache", "text2video", "yolov8", "aitemplate", "sdcpp"
        ];

        foreach (string flag in categoryFlags) T2IEngine.DisregardedFeatureFlags.Add(flag);
        foreach (string flag in providerFlags) T2IEngine.DisregardedFeatureFlags.Add(flag);
        foreach (string flag in incompatibleFlags) T2IEngine.DisregardedFeatureFlags.Add(flag);
    }

    /// <summary>Creates a standardized error response for API endpoints.</summary>
    public static JObject CreateErrorResponse(string message, string errorCode = null, Exception exception = null)
    {
        JObject response = new()
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(errorCode))
        {
            response["error_code"] = errorCode;
        }
        if (exception != null)
        {
            Logs.Error($"[AudioLab] Exception details: {exception}");
            response["error_type"] = exception.GetType().Name;
        }
        return response;
    }

    /// <summary>Creates a standardized success response for API endpoints.</summary>
    public static JObject CreateSuccessResponse(object data = null, string message = null)
    {
        JObject response = new()
        {
            ["success"] = true,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (!string.IsNullOrEmpty(message))
        {
            response["message"] = message;
        }
        if (data != null)
        {
            if (data is JObject jObject)
            {
                foreach (JProperty property in jObject.Properties())
                {
                    response[property.Name] = property.Value;
                }
            }
            else
            {
                response["data"] = JToken.FromObject(data);
            }
        }
        return response;
    }
}
