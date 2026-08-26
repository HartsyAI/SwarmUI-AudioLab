using SwarmUI.Core;
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

    /// <summary>Folder name under each Swarm model root that holds audio weights. Shared with the "Audio"
    /// T2IModelHandler registration so the scanned folders and the install target can't drift apart.</summary>
    public const string ModelRootFolderName = "audio";

    /// <summary>Root directory for audio model storage. Set in AudioLab.OnPreInit to
    /// "{Swarm ModelRoot}/audio"; the literal below is only a pre-init fallback.</summary>
    public static string ModelRoot { get; set; } = "Models/audio";

    /// <summary>Path for a specific model category (e.g. tts, stt, music).</summary>
    public static string GetModelPath(string category) => Path.Combine(Path.GetFullPath(ModelRoot), category);

    /// <summary>Category folders created under <see cref="ModelRoot"/>.</summary>
    private static readonly string[] Categories = ["tts", "stt", "music", "clone", "fx", ".cache"];

    /// <summary>Points <see cref="ModelRoot"/> at "{Swarm ModelRoot}/audio" and makes sure the category
    /// folders exist. There is deliberately no AudioLab-specific path setting: audio weights follow Swarm's
    /// own Server Configuration -> Paths -> ModelRoot, so the two can't drift apart. Re-run on backend init so
    /// a restart picks up a changed server setting.</summary>
    public static void SyncModelRootFromServer()
    {
        ModelRoot = Path.Combine(Program.ServerSettings.Paths.ActualModelRoot, ModelRootFolderName);
        string full = Path.GetFullPath(ModelRoot);
        foreach (string sub in Categories)
        {
            Directory.CreateDirectory(Path.Combine(full, sub));
        }
    }

    #endregion
}
