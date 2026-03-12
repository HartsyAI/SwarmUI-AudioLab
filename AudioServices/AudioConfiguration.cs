using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Configuration settings for the AudioLab extension.
/// Replaces ServiceConfiguration — removes hardcoded ports and BackendType enum
/// in favor of provider-based routing through DynamicAudioBackend.</summary>
public static class AudioConfiguration
{
    #region Process Configuration

    /// <summary>Maximum time to wait for a Python server process to start.</summary>
    public static readonly TimeSpan ProcessStartupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Maximum time to wait for a Python server process to shut down.</summary>
    public static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Maximum time to wait for a health check response.</summary>
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of health check attempts before declaring failure.</summary>
    public static readonly int MaxHealthCheckAttempts = 30;

    #endregion

    #region Installation Configuration

    /// <summary>Maximum time to wait for full dependency installation.</summary>
    public static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Maximum time to wait for a single pip package install.</summary>
    public static readonly TimeSpan PackageInstallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of retries for failed package installations.</summary>
    public static readonly int MaxInstallationRetries = 3;

    #endregion

    #region API Configuration

    /// <summary>Default timeout for API calls to Python servers.</summary>
    public static readonly TimeSpan ApiCallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>User-Agent header for outgoing HTTP requests.</summary>
    public static readonly string UserAgent = "SwarmUI-AudioLab/3.0";

    #endregion

    #region Audio Defaults

    /// <summary>Maximum audio file size in megabytes.</summary>
    public static readonly int MaxAudioSizeMB = 50;

    /// <summary>Maximum text length for TTS input.</summary>
    public static readonly int MaxTextLength = 1000;

    /// <summary>Default volume level for generated audio.</summary>
    public static readonly float DefaultVolume = 0.8f;

    /// <summary>Default language code for audio processing.</summary>
    public static readonly string DefaultLanguage = "en-US";

    /// <summary>Default voice identifier for TTS.</summary>
    public static readonly string DefaultVoice = "default";

    /// <summary>Supported language codes for audio processing.</summary>
    public static readonly string[] SupportedLanguages =
    [
        "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT",
        "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
    ];

    #endregion

    #region Paths

    /// <summary>Root directory of the AudioLab extension.</summary>
    public static string ExtensionDirectory { get; set; } = "";

    /// <summary>Directory containing the Python backend scripts.</summary>
    public static string PythonBackendDirectory => Path.Combine(ExtensionDirectory, "python_backend");

    /// <summary>Root directory for audio model storage, centralized under Models/audio/.</summary>
    public static string ModelRoot { get; set; } = "Models/audio";

    /// <summary>Path for HuggingFace model cache (redirected from ~/.cache/huggingface/).</summary>
    public static string GetHuggingFaceCachePath() => Path.Combine(Path.GetFullPath(ModelRoot), ".cache");

    /// <summary>Path for a specific model category (e.g. tts, stt, music).</summary>
    public static string GetModelPath(string category) => Path.Combine(Path.GetFullPath(ModelRoot), category);

    /// <summary>Root directory for per-group Python virtual environments.
    /// Delegates to VenvManager.VenvRoot which uses a short path on Windows.</summary>
    public static string VenvRoot => VenvManager.VenvRoot;

    #endregion

    #region Runtime Settings

    /// <summary>Whether to use Docker for Linux-only engines.</summary>
    public static bool UseDocker { get; set; } = false;

    /// <summary>Request timeout in seconds, configurable from backend settings.</summary>
    public static int TimeoutSeconds { get; set; } = 300;

    #endregion

    /// <summary>Validates the current configuration and logs any issues.</summary>
    public static bool ValidateConfiguration()
    {
        bool isValid = true;

        if (string.IsNullOrEmpty(ExtensionDirectory))
        {
            Logs.Error("[AudioLab] Extension directory not set");
            isValid = false;
        }

        string voiceProcessorScript = Path.Combine(PythonBackendDirectory, "voice_processor.py");
        if (!File.Exists(voiceProcessorScript))
        {
            Logs.Error($"[AudioLab] Voice processor script not found: {voiceProcessorScript}");
            isValid = false;
        }

        return isValid;
    }
}
