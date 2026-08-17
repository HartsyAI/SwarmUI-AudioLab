using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using Hartsy.Extensions.AudioLab.AudioServices;
using HartsyInference.Audio.Io;
using HartsyInference.Core.Backends;
using HartsyInference.Cpu;
using HartsyInference.Engine.Audio.Wake;
using HartsyInference.Engine.Audio.Wake.Speakers;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>Permissions for the wake-word endpoints.</summary>
public static class WakeWordPermissions
{
    /// <summary>Start/stop the listener, edit settings, train words, enroll speakers.</summary>
    public static readonly PermInfo PermManage = Permissions.Register(new("audio_wake_manage", "Manage Wake Words",
        "Allows starting and stopping the wake-word listener, training wake words, and enrolling speakers.",
        PermissionDefault.POWERUSERS, AudioLabPermissions.AudioLabPermGroup));

    /// <summary>Read status and subscribe to the detection stream. Lower bar than managing, because this is the
    /// permission another extension needs in order to react to wake events.</summary>
    public static readonly PermInfo PermListen = Permissions.Register(new("audio_wake_listen", "Listen to Wake Events",
        "Allows reading wake-word status and subscribing to the live detection stream.",
        PermissionDefault.USER, AudioLabPermissions.AudioLabPermGroup));
}

/// <summary>The SwarmUI API for wake-word detection.
///
/// <para>The point of this surface is that other extensions consume it. <c>AudioLabWakeEvents</c> is a
/// WebSocket that streams detections — word, score, route, transcript and speaker — as they happen, so
/// something like LLMAssistant can react to speech without reimplementing any audio handling.
/// <c>AudioLabWakeRecentDetections</c> is the same data for consumers that cannot hold a socket open.</para></summary>
[API.APIClass("Wake-word detection: listener control, word training, speaker enrollment, and a live detection stream")]
public static class WakeWordEndpoints
{
    /// <summary>How long the event socket waits before sending a keepalive. Comfortably under
    /// <see cref="API.WebsocketTimeout"/> (2 minutes), so an idle feed is never mistaken for a dead one.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Largest base64 enrollment clip accepted, per utterance.</summary>
    private const int MaxClipBase64 = 16 * 1024 * 1024;

    /// <summary>Registers every wake-word route.</summary>
    public static void Register()
    {
        API.RegisterAPICall(AudioLabWakeStatus, false, WakeWordPermissions.PermListen);
        API.RegisterAPICall(AudioLabWakeEvents, false, WakeWordPermissions.PermListen);
        API.RegisterAPICall(AudioLabWakeRecentDetections, false, WakeWordPermissions.PermListen);
        API.RegisterAPICall(AudioLabWakeListWords, false, WakeWordPermissions.PermListen);
        API.RegisterAPICall(AudioLabWakeListSpeakers, false, WakeWordPermissions.PermListen);
        API.RegisterAPICall(AudioLabWakeGetSettings, false, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeSaveSettings, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeStart, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeStop, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeConfigureWord, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeTrainWord, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeEnrollSpeaker, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeRemoveSpeaker, true, WakeWordPermissions.PermManage);
    }

    /// <summary>Listener state: whether it is bound, on what port, which satellites are connected, and which
    /// words are loaded.</summary>
    public static async Task<JObject> AudioLabWakeStatus(Session session)
    {
        await Task.CompletedTask;
        return new JObject
        {
            ["success"] = true,
            ["running"] = WakeWordService.Running,
            ["port"] = WakeWordService.Port,
            ["devices"] = new JArray(WakeWordService.Devices.ToArray()),
            ["words"] = new JArray(WakeWordService.Words.ToArray()),
            ["model_root"] = WakeWordService.ModelRoot(),
        };
    }

    /// <summary>Streams detections for as long as the socket is open.
    ///
    /// <para>The handler must loop: SwarmUI's API layer closes the socket the moment the handler returns, so
    /// returning early would turn a subscription into a single message.</para></summary>
    public static async Task<JObject> AudioLabWakeEvents(Session session, WebSocket ws)
    {
        (Guid id, ChannelReader<JObject> reader) = WakeWordService.Subscribe();
        try
        {
            await ws.SendJson(new JObject
            {
                ["subscribed"] = true,
                ["running"] = WakeWordService.Running,
                ["port"] = WakeWordService.Port,
            }, API.WebsocketTimeout);

            while (!Program.GlobalProgramCancel.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                JObject detection;
                try
                {
                    using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel);
                    wait.CancelAfter(KeepAliveInterval);
                    detection = await reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    if (Program.GlobalProgramCancel.IsCancellationRequested)
                    {
                        break;
                    }
                    await ws.SendJson(new JObject { ["keepalive"] = true }, API.WebsocketTimeout);
                    continue;
                }
                await ws.SendJson(new JObject { ["detection"] = detection }, API.WebsocketTimeout);
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab][Wake] Event socket closed: {ex.Message}");
        }
        finally
        {
            WakeWordService.Unsubscribe(id);
        }
        return null;
    }

    /// <summary>Detections buffered since the listener started — the polling equivalent of the event socket.</summary>
    public static async Task<JObject> AudioLabWakeRecentDetections(Session session)
    {
        await Task.CompletedTask;
        return new JObject { ["success"] = true, ["detections"] = WakeWordService.RecentDetections() };
    }

    /// <summary>Loaded wake words with their per-word settings.</summary>
    public static async Task<JObject> AudioLabWakeListWords(Session session)
    {
        await Task.CompletedTask;
        IReadOnlyDictionary<string, WakeWordConfig> settings = WakeWordService.WordSettings;
        JArray words = [];
        foreach (string word in WakeWordService.Words)
        {
            settings.TryGetValue(word, out WakeWordConfig config);
            words.Add(new JObject
            {
                ["name"] = word,
                ["threshold"] = config?.Threshold ?? 0.5f,
                ["smoothing_window"] = config?.SmoothingWindow ?? 3,
                ["refractory_seconds"] = config?.RefractorySeconds ?? 2.0,
                ["route"] = config?.Route,
                ["required_speaker"] = config?.RequiredSpeaker,
            });
        }
        return new JObject { ["success"] = true, ["words"] = words };
    }

    /// <summary>Current shared settings.</summary>
    public static async Task<JObject> AudioLabWakeGetSettings(Session session)
    {
        await Task.CompletedTask;
        return new JObject { ["success"] = true, ["settings"] = JObject.FromObject(WakeWordService.GetSettings()) };
    }

    /// <summary>Saves shared settings. When the listener is running it is restarted, because a bound socket
    /// cannot adopt a new port or model root.</summary>
    public static async Task<JObject> AudioLabWakeSaveSettings(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            JObject incoming = rawInput["settings"] as JObject
                ?? throw new InvalidOperationException("A 'settings' object is required.");
            WakeWordSettings settings = incoming.ToObject<WakeWordSettings>()
                ?? throw new InvalidOperationException("Settings could not be parsed.");
            if (settings.Port is < 1 or > 65535)
            {
                return new JObject { ["success"] = false, ["error"] = $"Port {settings.Port} is out of range." };
            }
            WakeWordService.SaveSettings(settings);
            string error = WakeWordService.ApplySettings();
            return new JObject { ["success"] = error is null, ["error"] = error, ["running"] = WakeWordService.Running };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Saving settings failed: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Starts the listener.</summary>
    public static async Task<JObject> AudioLabWakeStart(Session session)
    {
        await Task.CompletedTask;
        string error = WakeWordService.Start();
        return new JObject
        {
            ["success"] = error is null,
            ["error"] = error,
            ["running"] = WakeWordService.Running,
            ["port"] = WakeWordService.Port,
        };
    }

    /// <summary>Stops the listener and releases the port.</summary>
    public static async Task<JObject> AudioLabWakeStop(Session session)
    {
        await Task.CompletedTask;
        WakeWordService.Stop();
        return new JObject { ["success"] = true, ["running"] = WakeWordService.Running };
    }

    /// <summary>Sets one word's threshold, smoothing, refractory period, route tag and speaker restriction.</summary>
    public static async Task<JObject> AudioLabWakeConfigureWord(Session session, string word, float threshold = 0.5f,
        int smoothing_window = 3, double refractory_seconds = 2.0, string route = null, string required_speaker = null)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(word))
        {
            return new JObject { ["success"] = false, ["error"] = "word is required" };
        }
        if (!WakeWordService.Running)
        {
            return new JObject { ["success"] = false, ["error"] = "Start the wake listener before configuring words." };
        }
        try
        {
            WakeWordService.ConfigureWord(word, new WakeWordConfig
            {
                Threshold = threshold,
                SmoothingWindow = smoothing_window,
                RefractorySeconds = refractory_seconds,
                Route = string.IsNullOrWhiteSpace(route) ? null : route,
                RequiredSpeaker = string.IsNullOrWhiteSpace(required_speaker) ? null : required_speaker,
            });
            return new JObject { ["success"] = true };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Configuring '{word}' failed: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Trains a new wake word from its text, streaming progress. Follows the same WebSocket-parameter
    /// pattern as <c>AudioLabInstallEngine</c>.</summary>
    public static async Task<JObject> AudioLabWakeTrainWord(Session session, WebSocket ws, string phrase,
        string voices = null, string negative_phrases = null, string negative_audio = null, int epochs = 120)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            await ws.SendJson(new JObject { ["error"] = "phrase is required" }, API.WebsocketTimeout);
            return null;
        }
        try
        {
            WakeTrainingOptions options = new()
            {
                Phrase = phrase,
                Epochs = epochs,
                NegativeAudioDirectory = string.IsNullOrWhiteSpace(negative_audio) ? null : negative_audio,
                NegativePhrases = Split(negative_phrases),
            };
            List<string> voiceList = Split(voices);
            if (voiceList.Count > 0)
            {
                options = options with { Voices = voiceList };
            }

            WakeTrainingJob job = new(AudioEngineBridge.Engine, WakeWordService.ModelRoot());
            // A WebSocket throws on concurrent SendAsync, and progress reports arrive from thread-pool threads,
            // so two overlapping reports would kill the stream mid-training. Serialize every send through one
            // semaphore rather than blocking the training loop on the browser.
            using SemaphoreSlim sendLock = new(1, 1);
            async Task SendAsync(JObject payload)
            {
                await sendLock.WaitAsync().ConfigureAwait(false);
                try { await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false); }
                catch (Exception ex) { Logs.Debug($"[AudioLab][Wake] Training progress send failed: {ex.Message}"); }
                finally { sendLock.Release(); }
            }
            Progress<string> progress = new(message => _ = SendAsync(new JObject { ["status"] = message }));
            WakeTrainingResult result = await job.RunAsync(options, progress, Program.GlobalProgramCancel);

            // Persist the measured threshold so the word deploys with its own number rather than a global default.
            WakeWordService.ConfigureWord(result.Name, new WakeWordConfig { Threshold = result.SuggestedThreshold });

            await SendAsync(new JObject
            {
                ["success"] = true,
                ["name"] = result.Name,
                ["head_path"] = result.HeadPath,
                ["recall"] = result.Recall,
                ["false_accept_rate"] = result.FalseAcceptRate,
                ["false_accepts_per_hour"] = result.FalseAcceptsPerHour,
                ["suggested_threshold"] = result.SuggestedThreshold,
                ["positive_windows"] = result.PositiveWindows,
                ["negative_windows"] = result.NegativeWindows,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Training '{phrase}' failed: {ex.ReadableString()}");
            await ws.SendJson(new JObject { ["error"] = ex.Message }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Enrolled household speakers.</summary>
    public static async Task<JObject> AudioLabWakeListSpeakers(Session session)
    {
        await Task.CompletedTask;
        try
        {
            SpeakerProfileStore store = new();
            JArray speakers = [];
            foreach (SpeakerProfile profile in store.List())
            {
                speakers.Add(new JObject
                {
                    ["name"] = profile.Name,
                    ["utterances"] = profile.UtteranceCount,
                    ["phrase"] = profile.Phrase,
                    ["text_dependent"] = profile.IsTextDependent,
                    ["enrolled_at"] = profile.EnrolledUtc.ToString("O"),
                });
            }
            return new JObject { ["success"] = true, ["speakers"] = speakers, ["available"] = SpeakerVerifier.IsAvailable };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Listing speakers failed: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Enrolls a speaker from recorded utterances (base64 WAV).
    ///
    /// <para>Enroll on repetitions of the wake phrase itself when you can: a wake word is about a second long,
    /// and text-independent verification degrades badly at that length, so matching content between enrollment
    /// and use is what makes short-utterance identification workable.</para></summary>
    public static async Task<JObject> AudioLabWakeEnrollSpeaker(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string name = rawInput["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return new JObject { ["success"] = false, ["error"] = "name is required" };
            }
            if (rawInput["clips"] is not JArray clips || clips.Count == 0)
            {
                return new JObject { ["success"] = false, ["error"] = "clips (array of base64 WAV) is required" };
            }
            if (!SpeakerVerifier.IsAvailable)
            {
                return new JObject { ["success"] = false, ["error"] = "Speaker identification is unavailable: the CAM++ weights were not found." };
            }

            List<float[]> utterances = [];
            foreach (JToken clip in clips)
            {
                string base64 = clip.ToString();
                if (base64.Length > MaxClipBase64)
                {
                    return new JObject { ["success"] = false, ["error"] = "An enrollment clip exceeded the size limit." };
                }
                utterances.Add(DecodeMono16k(Convert.FromBase64String(base64)));
            }

            using IBackend backend = new CpuBackend();
            using SpeakerVerifier verifier = SpeakerVerifier.Load();
            SpeakerProfile profile = verifier.EnrollFromAudio(backend, name, utterances, rawInput["phrase"]?.ToString());
            Logs.Info($"[AudioLab][Wake] Enrolled speaker '{profile.Name}' from {profile.UtteranceCount} utterance(s).");
            return new JObject { ["success"] = true, ["name"] = profile.Name, ["utterances"] = profile.UtteranceCount };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Enrolling a speaker failed: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Removes an enrolled speaker.</summary>
    public static async Task<JObject> AudioLabWakeRemoveSpeaker(Session session, string name)
    {
        await Task.CompletedTask;
        try
        {
            SpeakerProfileStore store = new();
            return new JObject { ["success"] = store.Remove(name) };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Removing speaker '{name}' failed: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Decodes a WAV to the 16 kHz mono float the speaker encoder expects.</summary>
    private static float[] DecodeMono16k(byte[] wav)
    {
        using MemoryStream stream = new(wav);
        WavFile.DecodedAudio decoded = WavFile.Read(stream);
        float[] mono = decoded.ToMono();
        return decoded.SampleRate == 16000 ? mono : Resampler.Create(decoded.SampleRate, 16000).Resample(mono);
    }

    private static List<string> Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
