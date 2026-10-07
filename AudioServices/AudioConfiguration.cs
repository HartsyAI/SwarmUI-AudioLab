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

    /// <summary>Piper's own sensible default voice, for a request that names none. Mirrors the Engine's
    /// <c>PiperModel.DefaultVoice</c> ("en_US-lessac-medium") -- kept as a literal here (not read off the
    /// Engine) because this id is a stable, documented part of the <c>rhasspy/piper-voices</c> repo, the same
    /// way <see cref="DefaultLanguage"/> is a literal rather than something probed.
    ///
    /// <para>Piper needs this, and the generic <see cref="DefaultVoice"/> placeholder is not enough, because
    /// Piper's weights ARE the voice -- there is no model-level default download the way Kokoro or VibeVoice
    /// have one. Left as "default"/empty/null, the request reaches the Engine with no real voice named; the
    /// Engine's own selector then falls back to its bare catalog token ("piper") instead of a real voice name,
    /// and "piper" is not a file in <c>rhasspy/piper-voices</c> -- it 404s on <c>piper.onnx</c>. See
    /// <see cref="ResolveVoice"/>.</para></summary>
    public const string DefaultPiperVoice = "en_US-lessac-medium";

    /// <summary>Resolves the voice value to actually send to <paramref name="providerId"/>'s Engine request,
    /// substituting <see cref="DefaultPiperVoice"/> when the provider is Piper and the caller named no real
    /// voice (null, empty, or the generic <see cref="DefaultVoice"/> placeholder). Every other provider's
    /// placeholder is returned unchanged -- the zero-shot providers (VibeVoice, Dia, F5, ...) pick their own
    /// speaker from it and must keep seeing the placeholder, not a Piper voice name.
    ///
    /// <para>The one place this substitution happens, so every caller that can receive an unparameterized TTS
    /// request (<c>AudioLabAPI.ProcessTTS</c>/<c>AudioLabSpeakRaw</c>, <c>SpeakStreamRoute</c>,
    /// <c>VoiceTurnOrchestrator</c>) agrees on the same default voice instead of four copies of the same
    /// literal.</para></summary>
    public static string ResolveVoice(string requestedVoice, string providerId)
    {
        bool named = !string.IsNullOrWhiteSpace(requestedVoice) && !requestedVoice.Equals(DefaultVoice, StringComparison.OrdinalIgnoreCase);
        if (named)
        {
            return requestedVoice;
        }
        return providerId is not null && providerId.Equals("piper_tts", StringComparison.OrdinalIgnoreCase)
            ? DefaultPiperVoice
            : DefaultVoice;
    }

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
