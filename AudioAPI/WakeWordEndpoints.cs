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
        API.RegisterAPICall(AudioLabWakeInstallBackbone, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeInstallStockHead, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeInstallDenoiser, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeInstallVad, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeStart, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeStop, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeConfigureWord, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeTrainWord, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeEnrollSpeaker, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeRemoveSpeaker, true, WakeWordPermissions.PermManage);
        API.RegisterAPICall(AudioLabWakeIngest, false, WakeWordPermissions.PermListen);
    }

    /// <summary>Accepts a satellite over a WebSocket instead of the raw TCP port.
    ///
    /// <para>This exists because an HTTPS reverse proxy or tunnel — Cloudflare's, for instance — cannot carry
    /// raw TCP, but does carry WebSockets. A satellite that can speak WSS therefore reaches the listener over
    /// the same hostname as the web UI, with TLS supplied by the proxy and no extra port exposed.</para>
    ///
    /// <para>The wire format inside the socket is byte-for-byte the protocol the TCP port speaks, so firmware
    /// only changes its transport, not its framing. Authentication is still the <c>hello</c> frame's token —
    /// SwarmUI's own session check gates the route, but the token is what identifies the device.</para></summary>
    public static async Task<JObject> AudioLabWakeIngest(Session session, WebSocket ws)
    {
        if (!WakeWordService.Running)
        {
            await ws.SendJson(new JObject { ["error"] = "The wake listener is not running." }, API.WebsocketTimeout);
            return null;
        }
        string remote = $"ws:{session.User?.UserID ?? "?"}";
        try
        {
            using WebSocketStream stream = new(ws);
            await WakeWordService.ServeConnectionAsync(stream, remote, Program.GlobalProgramCancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab][Wake] Ingest connection from {remote} ended: {ex.Message}");
        }
        return null;
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
            ["noise_suppression"] = WakeWordService.GetSettings().NoiseSuppression,
            ["denoiser_available"] = WakeWordService.DenoiserAvailable,
            ["vad_installed"] = WakeWordService.VadInstalled,
            ["backbone_installed"] = WakeWordService.BackboneInstalled,
            ["installed_heads"] = new JArray(WakeWordService.InstalledHeads.ToArray()),
            ["available_stock_heads"] = new JArray(WakeWordService.AvailableStockHeads.ToArray()),
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
            // Merged over what is stored rather than deserialized on its own: the UI posts the fields it has
            // inputs for, and anything it omits — ModelRoot and Webhooks today, plus every field added since
            // the page was written — would otherwise be silently reset to its default on every save.
            JObject merged = JObject.FromObject(WakeWordService.GetSettings());
            merged.Merge(incoming, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            WakeWordSettings settings = merged.ToObject<WakeWordSettings>()
                ?? throw new InvalidOperationException("Settings could not be parsed.");
            if (settings.Port is < 1 or > 65535)
            {
                return new JObject { ["success"] = false, ["error"] = $"Port {settings.Port} is out of range." };
            }
            // The engine keeps a fixed-size capture ring and silently clamps a longer request to it, so a
            // larger number here does not buy a longer question — it just stops meaning anything, and the
            // setting quietly lies about what the device will accept.
            double maxUtterance = WakeSession.CaptureCapacitySamples / 16_000.0;
            if (settings.UtteranceSeconds is < 1 || settings.UtteranceSeconds > maxUtterance)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Longest utterance must be between 1 and {maxUtterance:0.#} seconds; the engine "
                                + $"keeps only {maxUtterance:0.#} seconds of audio to transcribe from.",
                };
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

    /// <summary>Downloads the openWakeWord backbone (melspectrogram.onnx + embedding_model.onnx, ~2.4MB
    /// combined, sha256-pinned in AudioWeightsRegistry) into the wake model directory's backbone/ subfolder.
    /// A fresh install has neither file — <see cref="AudioLabWakeStart"/> fails closed until this has run
    /// once. Streams progress the same way <see cref="AudioLabWakeTrainWord"/> does.</summary>
    public static async Task<JObject> AudioLabWakeInstallBackbone(Session session, WebSocket ws)
    {
        try
        {
            async Task SendAsync(JObject payload) => await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false);
            bool installed = await WakeWordService.InstallBackboneAsync(
                msg => SendAsync(new JObject { ["status"] = msg }), Program.GlobalProgramCancel);
            await SendAsync(new JObject { ["success"] = installed });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Installing the backbone failed: {ex.ReadableString()}");
            await ws.SendJson(new JObject { ["error"] = ex.Message }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Downloads one of openWakeWord's pretrained stock heads (currently just "hey_jarvis" —
    /// see AudioWeightsRegistry's "wake_stock_heads" set) into the wake model directory's heads/ subfolder.
    /// Quickest path to a running listener for testing before a real word is trained via
    /// <see cref="AudioLabWakeTrainWord"/>. Streams progress the same way that does.</summary>
    public static async Task<JObject> AudioLabWakeInstallStockHead(Session session, WebSocket ws, string word = "hey_jarvis")
    {
        try
        {
            async Task SendAsync(JObject payload) => await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false);
            bool installed = await WakeWordService.InstallStockHeadAsync(word,
                msg => SendAsync(new JObject { ["status"] = msg }), Program.GlobalProgramCancel);
            if (!installed)
            {
                await SendAsync(new JObject { ["error"] = $"'{word}' is not a known/verified stock head." });
                return null;
            }
            await SendAsync(new JObject { ["success"] = true });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Installing stock head '{word}' failed: {ex.ReadableString()}");
            await ws.SendJson(new JObject { ["error"] = ex.Message }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Downloads the RNNoise denoiser from the configured <c>DenoiserUrl</c> into the wake model
    /// directory's <c>denoise/</c> subfolder. Streams progress the same way the backbone install does.
    ///
    /// <para>Unlike the backbone and heads there is no registry entry to read: the weights are a conversion of
    /// upstream's PyTorch checkpoint, so the URL is a setting. Fails with a clear message rather than a silent
    /// no-op when it has not been set.</para></summary>
    public static async Task<JObject> AudioLabWakeInstallDenoiser(Session session, WebSocket ws)
    {
        try
        {
            async Task SendAsync(JObject payload) => await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false);
            string url = WakeWordService.GetSettings().DenoiserUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                await SendAsync(new JObject { ["error"] = "No denoiser URL is configured. Set one in the wake settings first — the weights are a conversion of upstream's PyTorch checkpoint, so there is no default download." });
                return null;
            }
            bool installed = await WakeWordService.InstallDenoiserAsync(url,
                msg => SendAsync(new JObject { ["status"] = msg }), Program.GlobalProgramCancel);
            await SendAsync(new JObject { ["success"] = installed });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Installing the denoiser failed: {ex.ReadableString()}");
            await ws.SendJson(new JObject { ["error"] = ex.Message }, API.WebsocketTimeout);
        }
        return null;
    }

    /// <summary>Downloads Silero VAD, the model that ends an utterance when the speaker stops.
    ///
    /// <para>Without it the service waits a fixed three seconds after the wake word and then transcribes, which
    /// cuts off any question longer than that and makes every short command wait the full three seconds. Takes
    /// no URL, unlike the denoiser: this file has a canonical MIT home and the engine reads its ONNX directly,
    /// so there is nothing to convert and nothing to host.</para></summary>
    public static async Task<JObject> AudioLabWakeInstallVad(Session session, WebSocket ws)
    {
        try
        {
            async Task SendAsync(JObject payload) => await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false);
            bool installed = await WakeWordService.InstallVadAsync(
                msg => SendAsync(new JObject { ["status"] = msg }), Program.GlobalProgramCancel);
            if (installed && WakeWordService.Running)
            {
                // The model set is built at Start, so a listener that came up without a VAD is still running
                // without one. Restart it rather than reporting success on something that has not taken effect.
                await SendAsync(new JObject { ["status"] = "Restarting the listener to pick up the new model." });
                WakeWordService.Stop();
                string error = WakeWordService.Start();
                if (error is not null)
                {
                    await SendAsync(new JObject { ["error"] = error });
                    return null;
                }
            }
            await SendAsync(new JObject { ["success"] = installed });
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Wake] Installing the end-of-speech model failed: {ex.ReadableString()}");
            await ws.SendJson(new JObject { ["error"] = ex.Message }, API.WebsocketTimeout);
        }
        return null;
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
