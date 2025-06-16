using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>Configuration settings for the Voice Assistant extension with separate STT and TTS backends.</summary>
public static class ServiceConfiguration
{
    // STT Backend Configuration
    public static readonly string STTBackendHost = "localhost";
    public static readonly int STTBackendPort = 7831;
    public static readonly string STTBackendUrl = $"http://{STTBackendHost}:{STTBackendPort}";

    // TTS Backend Configuration  
    public static readonly string TTSBackendHost = "localhost";
    public static readonly int TTSBackendPort = 7832;
    public static readonly string TTSBackendUrl = $"http://{TTSBackendHost}:{TTSBackendPort}";

    // Process Configuration
    public static readonly TimeSpan ProcessStartupTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);
    public static readonly int MaxHealthCheckAttempts = 30;

    // Installation Configuration
    public static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);
    /// <summary>Timeout for package installations. Increased to 30 minutes to accommodate slow package builds like halo, PyTorch, etc. Some packages require significant build time, especially when compiling wheels.</summary>
    public static readonly TimeSpan PackageInstallTimeout = TimeSpan.FromMinutes(30);
    public static readonly int MaxInstallationRetries = 3;

    // API Configuration
    public static readonly TimeSpan ApiCallTimeout = TimeSpan.FromSeconds(45);
    public static readonly string UserAgent = "SwarmUI-VoiceAssistant/2.0";

    // Audio Configuration
    public static readonly int MaxAudioSizeMB = 50;
    public static readonly int MaxTextLength = 1000;
    public static readonly float DefaultVolume = 0.8f;
    public static readonly string DefaultLanguage = "en-US";
    public static readonly string DefaultVoice = "default";

    // Backend Types
    public enum BackendType
    {
        STT,
        TTS
    }

    // STT Dependencies
    public static readonly string[] STTPackages =
    [
        // Core API Server Requirements
        "fastapi>=0.104.0",
        "uvicorn[standard]>=0.24.0",
        "python-multipart>=0.0.6",
        "pydantic>=2.5.0",
        "httpx>=0.25.0",
        "websockets==15.0.1",
        
        // Core Audio/ML Dependencies
        "numpy>=1.26.0",
        "scipy==1.15.2",
        "soundfile==0.13.1",
        "librosa==0.11.0",
        
        // PyTorch dependencies (required by RealtimeSTT)
        "torch==2.6.0+cu126",
        "torchaudio==2.6.0+cu126",
        
        // STT Engine Dependencies
        "RealtimeSTT",
        "PyAudio==0.2.14",
        "faster-whisper==1.1.1",
        "pvporcupine==1.9.5",
        "webrtcvad-wheels==2.0.14",
        "openwakeword>=0.4.0",
        "halo==0.0.31",
        "log_symbols>=0.0.14",
        "spinners>=0.0.24",
        "termcolor>=1.1.0",
        "colorama>=0.3.9"
    ];

    // TTS Dependencies
    public static readonly string[] TTSPackages =
    [
        // Core API Server Requirements
        "fastapi>=0.104.0",
        "uvicorn[standard]>=0.24.0",
        "python-multipart>=0.0.6",
        "pydantic>=2.5.0",
        "httpx>=0.25.0",
        "websockets==15.0.1",
        
        // Core Audio/ML Dependencies
        "numpy>=1.26.0",
        "scipy==1.15.2",
        "soundfile==0.13.1",
        "librosa==0.11.0",
        
        // PyTorch dependencies (required by Chatterbox TTS)
        "torch==2.6.0+cu126",
        "torchvision==0.21.0+cu126",
        "torchaudio==2.6.0+cu126",
        
        // TTS Engine Dependencies
        "git+https://github.com/JarodMica/chatterbox.git",
        "s3tokenizer",
        "transformers==4.46.3",
        "diffusers==0.29.0",
        "resemble-perth==1.0.1",
        "conformer==0.3.2",
        "safetensors==0.5.3"
    ];

    // Supported Languages
    public static readonly string[] SupportedLanguages =
    [
        "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT",
        "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
    ];

    // Available Voices
    public static readonly string[] AvailableVoices =
    [
        "default", "expressive", "calm", "dramatic", "male", "female", "neural"
    ];

    // Backend Script Names
    public static readonly string STTBackendScript = "stt_server.py";
    public static readonly string TTSBackendScript = "tts_server.py";

    // Paths
    public static string ExtensionDirectory { get; set; } = "";
    public static string STTPythonBackendScript => Path.Combine(ExtensionDirectory, "python_backend", STTBackendScript);
    public static string TTSPythonBackendScript => Path.Combine(ExtensionDirectory, "python_backend", TTSBackendScript);

    /// <summary>Gets the backend configuration for the specified backend type.</summary>
    /// <param name="backendType">The type of backend to get configuration for</param>
    /// <returns>Backend configuration containing host, port, and script path</returns>
    public static BackendConfiguration GetBackendConfiguration(BackendType backendType)
    {
        return backendType switch
        {
            BackendType.STT => new BackendConfiguration
            {
                Host = STTBackendHost,
                Port = STTBackendPort,
                Url = STTBackendUrl,
                ScriptPath = STTPythonBackendScript,
                Dependencies = STTPackages
            },
            BackendType.TTS => new BackendConfiguration
            {
                Host = TTSBackendHost,
                Port = TTSBackendPort,
                Url = TTSBackendUrl,
                ScriptPath = TTSPythonBackendScript,
                Dependencies = TTSPackages
            },
            _ => throw new ArgumentException($"Unknown backend type: {backendType}")
        };
    }

    /// <summary>Validates the current configuration and logs any issues.</summary>
    /// <returns>True if configuration is valid, false otherwise</returns>
    public static bool ValidateConfiguration()
    {
        bool isValid = true;

        if (string.IsNullOrEmpty(ExtensionDirectory))
        {
            Logs.Error("[VoiceAssistant] Extension directory not set");
            isValid = false;
        }

        if (!File.Exists(STTPythonBackendScript))
        {
            Logs.Error($"[VoiceAssistant] STT backend script not found: {STTPythonBackendScript}");
            isValid = false;
        }

        if (!File.Exists(TTSPythonBackendScript))
        {
            Logs.Error($"[VoiceAssistant] TTS backend script not found: {TTSPythonBackendScript}");
            isValid = false;
        }

        Logs.Debug($"[VoiceAssistant] Configuration validation: {(isValid ? "PASSED" : "FAILED")}");
        return isValid;
    }

    /// <summary>Gets the backend endpoint URL for a specific backend type and path.</summary>
    /// <param name="backendType">The backend type</param>
    /// <param name="path">The endpoint path</param>
    /// <returns>Complete endpoint URL</returns>
    public static string GetBackendEndpoint(BackendType backendType, string path)
    {
        BackendConfiguration config = GetBackendConfiguration(backendType);
        return $"{config.Url}{(path.StartsWith("/") ? path : "/" + path)}";
    }

    /// <summary>Gets all configured backend types.</summary>
    /// <returns>Array of all backend types</returns>
    public static BackendType[] GetAllBackendTypes()
    {
        return [BackendType.STT, BackendType.TTS];
    }
}

/// <summary>Configuration for a specific backend type.</summary>
public class BackendConfiguration
{
    /// <summary>Backend host address</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Backend port number</summary>
    public int Port { get; set; }

    /// <summary>Backend base URL</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Path to the Python script for this backend</summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>Dependencies required for this backend</summary>
    public string[] Dependencies { get; set; } = [];
}
