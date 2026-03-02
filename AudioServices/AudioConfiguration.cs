using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Configuration settings for the AudioLab extension.
/// Replaces ServiceConfiguration — removes hardcoded ports and BackendType enum
/// in favor of provider-based routing through DynamicAudioBackend.</summary>
public static class AudioConfiguration
{
    // Process Configuration
    public static readonly TimeSpan ProcessStartupTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);
    public static readonly int MaxHealthCheckAttempts = 30;

    // Installation Configuration
    public static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PackageInstallTimeout = TimeSpan.FromMinutes(30);
    public static readonly int MaxInstallationRetries = 3;

    // API Configuration
    public static readonly TimeSpan ApiCallTimeout = TimeSpan.FromSeconds(45);
    public static readonly string UserAgent = "SwarmUI-AudioLab/3.0";

    // Audio Configuration
    public static readonly int MaxAudioSizeMB = 50;
    public static readonly int MaxTextLength = 1000;
    public static readonly float DefaultVolume = 0.8f;
    public static readonly string DefaultLanguage = "en-US";
    public static readonly string DefaultVoice = "default";

    // Supported Languages
    public static readonly string[] SupportedLanguages =
    [
        "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT",
        "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
    ];

    // Paths
    public static string ExtensionDirectory { get; set; } = "";
    public static string PythonBackendDirectory => Path.Combine(ExtensionDirectory, "python_backend");

    // Model Storage — centralized under Models/audio/ instead of ~/.cache/huggingface/
    public static string ModelRoot { get; set; } = "Models/audio";

    /// <summary>Path for HuggingFace model cache (redirected from ~/.cache/huggingface/).</summary>
    public static string GetHuggingFaceCachePath() => Path.Combine(Path.GetFullPath(ModelRoot), ".cache");

    /// <summary>Path for a specific model category (e.g. tts, stt, music).</summary>
    public static string GetModelPath(string category) => Path.Combine(Path.GetFullPath(ModelRoot), category);

    /// <summary>Root directory for per-group Python virtual environments.
    /// Delegates to VenvManager.VenvRoot which uses a short path on Windows.</summary>
    public static string VenvRoot => VenvManager.VenvRoot;

    // Docker — optional for Linux-only engines
    public static bool UseDocker { get; set; } = false;

    // Request timeout — configurable from backend settings
    public static int TimeoutSeconds { get; set; } = 300;

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
