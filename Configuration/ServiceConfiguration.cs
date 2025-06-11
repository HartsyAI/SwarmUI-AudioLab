using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Configuration;

/// <summary>
/// Configuration settings for the Voice Assistant extension.
/// Centralizes all configuration values and provides validation.
/// </summary>
public static class ServiceConfiguration
{
    // Backend Configuration
    public static readonly string BackendHost = "localhost";
    public static readonly int BackendPort = 7831;
    public static readonly string BackendUrl = $"http://{BackendHost}:{BackendPort}";

    // Process Configuration
    public static readonly TimeSpan ProcessStartupTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);
    public static readonly int MaxHealthCheckAttempts = 30;

    // Installation Configuration
    public static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PackageInstallTimeout = TimeSpan.FromMinutes(10);
    public static readonly int MaxInstallationRetries = 3;

    // API Configuration
    public static readonly TimeSpan ApiCallTimeout = TimeSpan.FromSeconds(45);
    public static readonly string UserAgent = "SwarmUI-VoiceAssistant/1.0";

    // Audio Configuration
    public static readonly int MaxAudioSizeMB = 50;
    public static readonly int MaxTextLength = 1000;
    public static readonly float DefaultVolume = 0.8f;
    public static readonly string DefaultLanguage = "en-US";
    public static readonly string DefaultVoice = "default";

    // Required Dependencies
    public static readonly string[] CorePackages =
    {
        "fastapi>=0.104.0",
        "uvicorn[standard]>=0.24.0",
        "python-multipart>=0.0.6",
        "pydantic>=2.5.0",
        "numpy>=1.24.0",
        "scipy>=1.10.0",
        "torchaudio>=2.0.0",
        "httpx>=0.25.0"
    };

    public static readonly string PrimarySTTEngine = "RealtimeSTT";
    public static readonly string PrimaryTTSEngine = "git+https://github.com/resemble-ai/chatterbox.git";

    // Paths
    public static string ExtensionDirectory { get; set; } = "";
    public static string PythonBackendScript => Path.Combine(ExtensionDirectory, "python_backend", "voice_server.py");

    // Supported Languages
    public static readonly string[] SupportedLanguages =
    {
        "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT",
        "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
    };

    // Available Voices
    public static readonly string[] AvailableVoices =
    {
        "default", "expressive", "calm", "dramatic", "male", "female", "neural"
    };

    /// <summary>
    /// Validates the current configuration and logs any issues.
    /// </summary>
    public static bool ValidateConfiguration()
    {
        bool isValid = true;

        if (string.IsNullOrEmpty(ExtensionDirectory))
        {
            Logs.Error("[VoiceAssistant] Extension directory not set");
            isValid = false;
        }

        if (!File.Exists(PythonBackendScript))
        {
            Logs.Error($"[VoiceAssistant] Python backend script not found: {PythonBackendScript}");
            isValid = false;
        }

        Logs.Debug($"[VoiceAssistant] Configuration validation: {(isValid ? "PASSED" : "FAILED")}");
        return isValid;
    }

    /// <summary>
    /// Gets the backend endpoint URL for a specific path.
    /// </summary>
    public static string GetBackendEndpoint(string path)
    {
        return $"{BackendUrl}{(path.StartsWith("/") ? path : "/" + path)}";
    }
}