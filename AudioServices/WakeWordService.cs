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
                    TranscribeModel = settings.TranscribeModel,
                    IdentifySpeakers = settings.IdentifySpeakers,
                    Webhooks = settings.Webhooks ?? [],
                    EnableTcpListener = settings.EnableTcpListener,
                    AuthToken = string.IsNullOrWhiteSpace(settings.AuthToken) ? null : settings.AuthToken,
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
}
