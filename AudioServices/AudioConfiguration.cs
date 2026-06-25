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

    /// <summary>Root directory for audio model storage, centralized under Models/audio/.</summary>
    public static string ModelRoot { get; set; } = "Models/audio";

    /// <summary>Path for a specific model category (e.g. tts, stt, music).</summary>
    public static string GetModelPath(string category) => Path.Combine(Path.GetFullPath(ModelRoot), category);

    #endregion

    #region Runtime Settings

    /// <summary>Retained as inert metadata for the engine list UI; Docker-based Linux-only engines are
    /// no longer used now that inference runs in-process on the C# engine.</summary>
    public static bool UseDocker { get; set; } = false;

    #endregion
}
