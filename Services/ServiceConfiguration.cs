using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

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
    /// <summary>
    /// Timeout for package installations. Increased to 30 minutes to accommodate slow package builds like halo, PyTorch, etc.
    /// Some packages require significant build time, especially when compiling wheels.
    /// </summary>
    public static readonly TimeSpan PackageInstallTimeout = TimeSpan.FromMinutes(30);
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
        // API Server Requirements
        "fastapi>=0.104.0",
        "uvicorn[standard]>=0.24.0",
        "python-multipart>=0.0.6",
        "pydantic>=2.5.0",
        "httpx>=0.25.0",
        "websockets==15.0.1",
        "websocket-client==1.8.0",
        
        // Core Audio/ML Dependencies
        "numpy>=1.26.0",
        "scipy==1.15.2",
        // PyTorch dependencies - using version 2.6.0 which is required by chatterbox-tts
        // and has CUDA support for ComfyUI compatibility
        "torch==2.6.0+cu126",  // CUDA 12.6 compatible version
        "torchvision==0.21.0+cu126",  // Matching version with CUDA support
        "torchaudio==2.6.0+cu126",  // Matching version with CUDA support
        "soundfile==0.13.1",
        "librosa==0.11.0",
        
        // RealtimeSTT Dependencies
        "PyAudio==0.2.14",
        "faster-whisper==1.1.1",
        "pvporcupine==1.9.5",
        "webrtcvad-wheels==2.0.14",
        "openwakeword>=0.4.0",
        "halo==0.0.31",
        "log_symbols>=0.0.14", // Dependency of halo
        "spinners>=0.0.24",    // Dependency of halo
        "termcolor>=1.1.0",    // Dependency of halo
        "colorama>=0.3.9",     // Dependency of halo
        
        // Chatterbox TTS Dependencies
        "s3tokenizer",
        "transformers==4.46.3",
        "diffusers==0.29.0",
        "resemble-perth==1.0.1",
        "conformer==0.3.2",
        "safetensors==0.5.3"
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