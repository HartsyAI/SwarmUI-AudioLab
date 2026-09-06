using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using HartsyInference.Engine.Audio.Wake;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>AudioLab's always-on wake-word listener: satellites hold a TCP connection open and stream microphone
/// audio, the engine scores it, and detections fan out to the browser and to any other extension that subscribes.
///
/// <para>This is the first background service in AudioLab — everything else here is request/response — so it
/// follows SwarmUI core's convention rather than inventing one: work is owned by the extension lifecycle, every
/// wait observes <see cref="Program.GlobalProgramCancel"/>, and failures are logged rather than thrown into a
/// thread nobody is awaiting.</para>
///
/// <para>It is deliberately NOT a SwarmUI backend. <c>AbstractT2IBackend</c> is request/response and is
/// restartable from the Backends admin panel, so an admin restarting a backend would silently kill the
/// microphone. The engine-side listener also holds its own private CPU backend and its own ~3 MB models, so it
/// neither takes the shared generation lock nor competes for VRAM with audio generation.</para></summary>
public static class WakeWordService
{
    /// <summary>Generic-data bucket and key the settings live under, alongside AudioLab's other shared state.</summary>
    public const string SettingsDataName = "audiolab";
    /// <summary>Key within that bucket.</summary>
    public const string SettingsKey = "wakeword";

    /// <summary>Detections retained for pollers that cannot hold a socket open.</summary>
    private const int RecentCapacity = 100;

    private static readonly object _lock = new();
    private static readonly ConcurrentDictionary<Guid, Channel<JObject>> _subscribers = new();
    private static readonly ConcurrentQueue<JObject> _recent = new();
    private static WakeService _service;
    private static int _starting;

    /// <summary>Raised for every detection, for in-process consumers. Other extensions (LLMAssistant) can hook
    /// this directly instead of opening a WebSocket back to the same process.</summary>
    public static event Action<JObject> Detected;

    /// <summary>Whether the listener is currently bound and scoring.</summary>
    public static bool Running => _service is not null;

    /// <summary>The bound TCP port, or 0 when stopped.</summary>
    public static int Port => _service?.Port ?? 0;

    /// <summary>Device ids with a session, connected or not — a session outlives its connection so a reconnecting
    /// satellite keeps its configuration.</summary>
    public static IReadOnlyCollection<string> Devices => _service?.Devices ?? [];

    /// <summary>Wake words currently loaded.</summary>
    public static IReadOnlyCollection<string> Words => _service?.Words ?? [];

    /// <summary>Whether the shared backbone is on disk. The listener fails closed without it, so the UI needs
    /// to tell "not installed yet" apart from a real fault.</summary>
    public static bool BackboneInstalled
    {
        get
        {
            string dir = Path.Combine(ModelRoot(), "backbone");
            return File.Exists(Path.Combine(dir, "melspectrogram.onnx"))
                && File.Exists(Path.Combine(dir, "embedding_model.onnx"));
        }
    }

    /// <summary>Wake-word heads present on disk, whether or not the listener is running. <see cref="Words"/>
    /// only reports loaded heads, which is empty while stopped — and "no words" is exactly what a user needs to
    /// see before starting.</summary>
    public static IReadOnlyCollection<string> InstalledHeads
    {
        get
        {
            string dir = Path.Combine(ModelRoot(), "heads");
            if (!Directory.Exists(dir)) return [];
            return [.. Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct()
                .OrderBy(n => n)];
        }
    }

    /// <summary>Stock wake words offered for one-click install.</summary>
    public static IReadOnlyCollection<string> AvailableStockHeads => AudioWeightsRegistry.ModelsFor("wake_stock_heads");

    /// <summary>Whether the RNNoise weights are present, independent of whether suppression is switched on. The
    /// weights are a conversion of upstream's PyTorch checkpoint rather than a downloadable artifact, so this is
    /// how the UI tells "not enabled" apart from "enabled but the file was never produced" — the second silently
    /// runs unsuppressed.</summary>
    public static bool DenoiserAvailable =>
        File.Exists(Path.Combine(ModelRoot(), "denoise", "rnnoise.safetensors"));

    /// <summary>Per-word settings as the engine has them persisted.</summary>
    public static IReadOnlyDictionary<string, WakeWordConfig> WordSettings =>
        _service?.WordSettings ?? new Dictionary<string, WakeWordConfig>();

    /// <summary>Starts the listener if it is not already running. Safe to call concurrently; returns the error
    /// text on failure rather than throwing, because every caller is an API route that must answer the client.</summary>
    public static string Start()
    {
        if (Interlocked.Exchange(ref _starting, 1) != 0)
        {
            return "Wake listener is already starting.";
        }
        try
        {
            lock (_lock)
            {
                if (_service is not null)
                {
                    return null;
                }
                WakeWordSettings settings = GetSettings();
                WakeService service = new(AudioEngineBridge.Engine, new WakeServiceOptions
                {
                    Port = settings.Port,
                    BindAddress = settings.BindAddress,
                    ModelRoot = string.IsNullOrWhiteSpace(settings.ModelRoot) ? null : settings.ModelRoot,
                    TranscribeOnDetection = settings.TranscribeOnDetection,
            UseEndOfSpeech = settings.UseEndOfSpeech,
            EndOfSpeechSilenceMs = settings.EndOfSpeechSilenceMs,
            UtteranceSeconds = settings.UtteranceSeconds,
                    TranscribeModel = settings.TranscribeModel,
                    IdentifySpeakers = settings.IdentifySpeakers,
                    Webhooks = settings.Webhooks ?? [],
                    EnableTcpListener = settings.EnableTcpListener,
                    AuthToken = string.IsNullOrWhiteSpace(settings.AuthToken) ? null : settings.AuthToken,
                    NoiseSuppression = settings.NoiseSuppression,
                });
                service.Detected += OnDetected;
                service.Start();
                _service = service;
                Logs.Init($"[AudioLab][Wake] Listener started on port {service.Port} with {service.Words.Count} word(s).");
                return null;
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Failed to start the listener: {ex.ReadableString()}");
            return ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
        }
    }

    /// <summary>Stops the listener and releases the port. Idempotent.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            if (_service is null)
            {
                return;
            }
            try
            {
                _service.Detected -= OnDetected;
                _service.Dispose();
                Logs.Info("[AudioLab][Wake] Listener stopped.");
            }
            catch (Exception ex)
            {
                Logs.Error($"[AudioLab][Wake] Error stopping the listener: {ex.ReadableString()}");
            }
            finally
            {
                _service = null;
            }
        }
    }

    /// <summary>Applies the persisted settings by restarting the listener when it is running. A port or model-root
    /// change cannot be applied to a bound socket, so a restart is the honest way to make a saved setting real.</summary>
    public static string ApplySettings()
    {
        if (!Running)
        {
            return null;
        }
        Stop();
        return Start();
    }

    private static void OnDetected(WakeEvent evt)
    {
        JObject payload = ToJson(evt);
        _recent.Enqueue(payload);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _))
        {
        }
        foreach (Channel<JObject> channel in _subscribers.Values)
        {
            // Bounded + DropOldest: a browser tab that stopped reading must never stall the detection path.
            channel.Writer.TryWrite(payload);
        }
        try
        {
            Detected?.Invoke(payload);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] A detection subscriber threw: {ex.ReadableString()}");
        }
    }

    /// <summary>Serializes a detection into the shape both the WebSocket feed and the polling route return, so a
    /// consumer written against one works against the other unchanged.</summary>
    public static JObject ToJson(WakeEvent evt) => new()
    {
        ["device_id"] = evt.DeviceId,
        ["word"] = evt.Word,
        ["score"] = evt.Score,
        ["route"] = evt.Route,
        ["transcript"] = evt.Transcript,
        ["speaker"] = evt.Speaker,
        ["detected_at"] = evt.DetectedAtUtc.ToString("O"),
    };

    /// <summary>Detections since the service started, oldest first.</summary>
    public static JArray RecentDetections() => [.. _recent];

    /// <summary>Opens a detection stream. Dispose the returned token to unsubscribe — a subscriber that is never
    /// released leaks a channel for the process lifetime.</summary>
    public static (Guid Id, ChannelReader<JObject> Reader) Subscribe()
    {
        Guid id = Guid.NewGuid();
        Channel<JObject> channel = Channel.CreateBounded<JObject>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    /// <summary>Releases a subscription taken from <see cref="Subscribe"/>.</summary>
    public static void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out Channel<JObject> channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Hands a caller-supplied connection to the wake service — used by the WebSocket ingest route so a
    /// satellite can reach the listener through an HTTPS tunnel that cannot carry raw TCP.</summary>
    public static Task ServeConnectionAsync(Stream stream, string remoteLabel, CancellationToken cancel)
    {
        WakeService service = _service ?? throw new InvalidOperationException("The wake listener is not running.");
        return service.ServeConnectionAsync(stream, remoteLabel, cancel);
    }

    /// <summary>Persists a word's threshold, route and speaker restriction, and rolls it out to live sessions.</summary>
    public static void ConfigureWord(string name, WakeWordConfig config) => _service?.ConfigureWord(name, config);

    /// <summary>Downloads the openWakeWord backbone (melspectrogram.onnx + embedding_model.onnx) into
    /// <c>{ModelRoot}/backbone/</c> if not already present and hash-valid — reuses AudioWeights'
    /// atomic-download-plus-sha256-verify machinery (AudioLabInstallEngine's checkpoint path), just pointed
    /// at the wake directory convention instead of a provider's. A fresh install has neither file, and
    /// <see cref="Start"/> fails closed rather than fetching them itself (see <c>WakeModelSet.Load</c>) —
    /// this is that missing install step, callable the same way everything else here is: over the API
    /// (<c>AudioLabWakeInstallBackbone</c>), not by hand-placing files.</summary>
    public static async Task<bool> InstallBackboneAsync(Func<string, Task> onProgress, CancellationToken cancel = default)
    {
        AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor("wake", "backbone");
        if (specs.Length == 0)
        {
            return false;
        }
        string dir = Path.Combine(ModelRoot(), "backbone");
        Directory.CreateDirectory(dir);
        foreach (AudioWeightsRegistry.DownloadSpec spec in specs)
        {
            cancel.ThrowIfCancellationRequested();
            await AudioWeights.EnsureWeightAsync(spec, dir, onProgress, cancel);
        }
        return true;
    }

    /// <summary>Downloads the RNNoise denoiser from <paramref name="url"/> into <c>{ModelRoot}/denoise/</c>.
    ///
    /// <para>Takes a URL rather than reading <see cref="AudioWeightsRegistry"/> like the backbone and heads do,
    /// because there is no canonical download: the weights are a conversion of upstream's PyTorch checkpoint
    /// (see <c>tools/convert_pth_to_safetensors.py</c> in the engine repo), so whoever hosts the converted file
    /// decides where it lives. Configuring it as a setting also means swapping in a re-quantized build later is
    /// a text edit rather than an extension release.</para>
    ///
    /// <para>Reuses the same atomic download-and-verify path as every other weight. With no published hash to
    /// pin, an interrupted download is caught by the size floor rather than silently accepted.</para></summary>
    /// <summary>Downloads Silero VAD into <c>{ModelRoot}/vad/</c>, the model that lets an utterance end when the
    /// speaker stops instead of after a fixed three seconds.
    ///
    /// <para>Unlike the denoiser this takes no URL: the file has a canonical home (silero-vad's own repository,
    /// MIT) and the engine reads that ONNX directly, so there is nothing to convert and nothing for anyone to
    /// host. Same atomic download-and-verify path as the backbone, against a pinned sha256.</para></summary>
    public static async Task<bool> InstallVadAsync(Func<string, Task> onProgress, CancellationToken cancel = default)
    {
        AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor("wake", "vad");
        if (specs.Length == 0)
        {
            return false;
        }
        string dir = Path.Combine(ModelRoot(), "vad");
        Directory.CreateDirectory(dir);
        foreach (AudioWeightsRegistry.DownloadSpec spec in specs)
        {
            cancel.ThrowIfCancellationRequested();
            await AudioWeights.EnsureWeightAsync(spec, dir, onProgress, cancel);
        }
        return true;
    }

    /// <summary>Whether the end-of-speech model is on disk.</summary>
    public static bool VadInstalled =>
        File.Exists(Path.Combine(ModelRoot(), "vad", "silero_vad.onnx"))
        || File.Exists(Path.Combine(ModelRoot(), "vad", "silero_vad_16k.safetensors"));

    public static async Task<bool> InstallDenoiserAsync(string url, Func<string, Task> onProgress, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        AudioWeightsRegistry.DownloadSpec spec = new(Url: url.Trim(), FileName: "rnnoise.safetensors", Sha256: "");
        string dir = Path.Combine(ModelRoot(), "denoise");
        Directory.CreateDirectory(dir);
        await AudioWeights.EnsureWeightAsync(spec, dir, onProgress, cancel);
        return true;
    }

    /// <summary>Downloads one of openWakeWord's pretrained stock heads into <c>{ModelRoot}/heads/</c> — the
    /// quickest way to get the listener past its fail-closed "no heads loaded" check for testing, before a
    /// real word is trained via <c>AudioLabWakeTrainWord</c>. Returns false for a word not in
    /// AudioWeightsRegistry's "wake_stock_heads" set (i.e. not actually offered/verified) rather than
    /// guessing at a URL.</summary>
    public static async Task<bool> InstallStockHeadAsync(string word, Func<string, Task> onProgress, CancellationToken cancel = default)
    {
        AudioWeightsRegistry.DownloadSpec[] specs = AudioWeightsRegistry.SpecsFor("wake_stock_heads", word);
        if (specs.Length == 0)
        {
            return false;
        }
        string dir = Path.Combine(ModelRoot(), "heads");
        Directory.CreateDirectory(dir);
        foreach (AudioWeightsRegistry.DownloadSpec spec in specs)
        {
            cancel.ThrowIfCancellationRequested();
            await AudioWeights.EnsureWeightAsync(spec, dir, onProgress, cancel);
        }
        return true;
    }

    /// <summary>The wake model directory, whether or not the listener is running — the training and speaker routes
    /// need it while stopped.</summary>
    public static string ModelRoot()
    {
        WakeWordSettings settings = GetSettings();
        if (!string.IsNullOrWhiteSpace(settings.ModelRoot))
        {
            return settings.ModelRoot;
        }
        return Path.Combine(AudioConfiguration.ModelRoot, "wake");
    }

    /// <summary>Reads the shared settings. There is no core mechanism for extension settings in the SwarmUI
    /// settings UI, so this uses the generic data store on the shared pseudo-user, as LLMAssistant does.</summary>
    public static WakeWordSettings GetSettings()
    {
        try
        {
            string raw = Program.Sessions.GenericSharedUser.GetGenericData(SettingsDataName, SettingsKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return JObject.Parse(raw).ToObject<WakeWordSettings>() ?? new WakeWordSettings();
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Could not read settings, using defaults: {ex.Message}");
        }
        return new WakeWordSettings();
    }

    /// <summary>Persists the shared settings.</summary>
    public static void SaveSettings(WakeWordSettings settings)
    {
        Program.Sessions.GenericSharedUser.SaveGenericData(SettingsDataName, SettingsKey, JObject.FromObject(settings).ToString());
    }
}

/// <summary>Shared wake-word settings. Off by default: a SwarmUI install with no voice satellite should never
/// bind a port or hold a detection thread.</summary>
public class WakeWordSettings
{
    /// <summary>Whether the listener starts with SwarmUI.</summary>
    public bool Enabled { get; set; }

    /// <summary>TCP port satellites connect to.</summary>
    public int Port { get; set; } = 10800;

    /// <summary>Interface to bind; satellites connect from the LAN.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Wake assets directory; empty uses the engine default under the audio model root.</summary>
    public string ModelRoot { get; set; } = "";

    /// <summary>Whether a detection also transcribes the command that follows it.</summary>
    public bool TranscribeOnDetection { get; set; } = true;

    /// <summary>Model id used for that transcription.</summary>
    public string TranscribeModel { get; set; } = "whisper";

    /// <summary>Whether to identify the speaker and enforce per-word speaker restrictions.</summary>
    public bool IdentifySpeakers { get; set; } = true;

    /// <summary>URLs that receive a JSON POST per detection, for consumers outside this process.</summary>
    public List<string> Webhooks { get; set; } = [];

    /// <summary>Whether to bind the raw TCP port. Turn it off for a tunnel-only deployment and nothing listens
    /// on the LAN; satellites then connect over the WebSocket ingest route instead.</summary>
    public bool EnableTcpListener { get; set; } = true;

    /// <summary>Shared secret a satellite must send in its hello frame. Empty disables the check, which is fine
    /// on a trusted LAN. **Set it before exposing this to the internet** — without it, anyone who reaches the
    /// endpoint can stream audio in and receive every detection, including transcripts of what was said.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>Whether to run RNNoise over satellite audio before scoring it, so the wake model hears speech
    /// rather than the room. Requires the denoiser weights (install them from the Wake Word tab); without them
    /// the listener logs and runs unsuppressed. Costs real compute per connected satellite, so it is opt-in.
    ///
    /// <para>Only the wake scoring sees the cleaned audio — transcription and speaker identification still get
    /// the raw microphone feed.</para></summary>
    public bool NoiseSuppression { get; set; }

    /// <summary>Where to download the RNNoise denoiser from. Empty until you host one — the weights are a
    /// conversion of upstream's PyTorch checkpoint, not a file with a canonical home, so there is no sensible
    /// default to ship. Kept as a setting rather than baked into the weights registry so a re-quantized or
    /// self-hosted build can be swapped in without an extension release.</summary>
    public string DenoiserUrl { get; set; } = "";

    /// <summary>Whether to end an utterance when the speaker stops rather than after a fixed wait.
    ///
    /// <para>Off, transcription starts a fixed three seconds after the word fires — which truncates anyone
    /// whose question runs past that, and makes everyone else wait the full three seconds for a two-word
    /// command. Needs the VAD installed (<c>AudioLabWakeInstallVad</c>); without it the engine logs and falls
    /// back to the fixed wait rather than refusing to listen.</para></summary>
    public bool UseEndOfSpeech { get; set; } = true;

    /// <summary>Silence, in milliseconds, that ends an utterance. 500 ms is about the shortest a person can
    /// pause mid-sentence without it reading as the end of one.</summary>
    public int EndOfSpeechSilenceMs { get; set; } = 500;

    /// <summary>Longest utterance captured around a detection, and the cap on how long end-of-speech waits.</summary>
    public double UtteranceSeconds { get; set; } = 8.0;
}
