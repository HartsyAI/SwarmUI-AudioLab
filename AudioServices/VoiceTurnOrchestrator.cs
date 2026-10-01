using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioAPI;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Audio.Streaming;
using HartsyInference.Engine.Audio.Wake;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Talks to one connected satellite: status frames and reply audio. A thin, testable facade over
/// <see cref="WakeWordService"/>'s own static surface for exactly those three operations, so
/// <see cref="SatelliteTurnRunner"/> can be driven by a fake in a test instead of a real listener and socket.</summary>
internal interface ISatelliteLink
{
    /// <summary>See <see cref="WakeWordService.SendStatusAsync"/>.</summary>
    Task<bool> SendStatusAsync(string deviceId, string state, string detail = null);

    /// <summary>See <see cref="WakeWordService.BeginAudio"/>.</summary>
    WakeAudioStream BeginAudio(string deviceId, int sampleRate);

    /// <summary>See <see cref="WakeWordService.SendAudioAsync"/>.</summary>
    Task<int> SendAudioAsync(string deviceId, ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken cancel);
}

/// <summary>Asks the configured assistant one question and gets its whole reply back. A thin, testable facade
/// over the legacy route's loopback HTTP call to <c>LLMAssistantVoiceTurn</c>.</summary>
internal interface IVoiceAssistantCaller
{
    Task<(string Reply, string Error)> AskAsync(string text, CancellationToken cancel);
}

/// <summary>The real <see cref="ISatelliteLink"/>, over <see cref="WakeWordService"/>'s static surface.</summary>
internal sealed class WakeServiceSatelliteLink : ISatelliteLink
{
    public static readonly WakeServiceSatelliteLink Instance = new();

    private WakeServiceSatelliteLink()
    {
    }

    public Task<bool> SendStatusAsync(string deviceId, string state, string detail = null) =>
        WakeWordService.SendStatusAsync(deviceId, state, detail);

    public WakeAudioStream BeginAudio(string deviceId, int sampleRate) =>
        WakeWordService.BeginAudio(deviceId, sampleRate);

    public Task<int> SendAudioAsync(string deviceId, ReadOnlyMemory<byte> pcm, int sampleRate, CancellationToken cancel) =>
        WakeWordService.SendAudioAsync(deviceId, pcm, sampleRate, cancel);
}

/// <summary>The real <see cref="IVoiceAssistantCaller"/>, over a loopback HTTP call to LLMAssistant's one-shot
/// endpoint.
///
/// <para>A loopback call rather than a direct one because the assistant lives in a different extension, loaded
/// into its own assembly context: calling it in-process would mean this extension could not be installed
/// without that one. The cost is a request to ourselves on an interface that never leaves the machine, which is
/// cheap next to the model it is asking.</para></summary>
internal sealed class LoopbackAssistantCaller : IVoiceAssistantCaller
{
    public static readonly LoopbackAssistantCaller Instance = new();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Why the last loopback call failed, so a caller can say something more useful than "it did not
    /// work". Best-effort and racy across concurrent turns, which is acceptable for a diagnostic.</summary>
    private string _lastPostFailure;

    private LoopbackAssistantCaller()
    {
    }

    public async Task<(string Reply, string Error)> AskAsync(string text, CancellationToken cancel)
    {
        WakeWordSettings settings = WakeWordService.GetSettings();
        // Whatever address this server is actually listening on, not 127.0.0.1. A server bound to one specific
        // interface — which is how the production box is configured — refuses the loopback address, and the
        // whole turn failed there with nothing to say but "could not open a session". PageURL is the existing
        // answer to the same question: it maps a wildcard bind to localhost and otherwise uses the real host.
        string baseUrl = $"{WebServer.PageURL}/API";

        JObject session = await PostAsync($"{baseUrl}/GetNewSession", new JObject(), cancel).ConfigureAwait(false);
        string sessionId = session?["session_id"]?.ToString();
        if (string.IsNullOrEmpty(sessionId))
        {
            // The reason, not just the fact. This travels to the device as the `error` status detail and shows
            // up in a bench run, which on a box whose logs are not readable from here is the only way anyone
            // finds out why a turn produced silence.
            return (null, $"could not open a session at {baseUrl}: {_lastPostFailure ?? "no session_id in the reply"}");
        }

        JObject request = new()
        {
            ["session_id"] = sessionId,
            ["message"] = text,
        };
        if (!string.IsNullOrWhiteSpace(settings.AssistantId))
        {
            request["assistantId"] = settings.AssistantId;
        }
        JObject answer = await PostAsync($"{baseUrl}/LLMAssistantVoiceTurn", request, cancel).ConfigureAwait(false);
        if (answer is null)
        {
            return (null, "the assistant did not answer. Is the LLMAssistant extension installed?");
        }
        if (answer["success"]?.Value<bool>() != true)
        {
            return (null, answer["error"]?.ToString() ?? "the assistant returned an error with no message.");
        }
        string reply = answer["response"]?.ToString();
        return string.IsNullOrWhiteSpace(reply)
            ? (null, "the assistant produced no speech.")
            : (reply, null);
    }

    private async Task<JObject> PostAsync(string url, JObject body, CancellationToken cancel)
    {
        try
        {
            using StringContent content = new(body.ToString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.PostAsync(url, content, cancel).ConfigureAwait(false);
            string raw = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return JObject.Parse(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastPostFailure = ex.Message;
            Logs.Debug($"[AudioLab][Turn] {url} failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Runs a whole voice turn against an injected <see cref="ISatelliteLink"/> and
/// <see cref="IVoiceAssistantCaller"/>, so the orchestration itself -- the decision to answer, the status
/// sequence, the barge-in cancellation -- is testable without a real listener, socket, HTTP call, or TTS engine.
///
/// <para>Generic/testable-primitive split, same reasoning as <see cref="LazyIdleResource{T}"/> and
/// <see cref="AcquireTeardownGate"/> elsewhere in this extension: the real dependencies (a live wake listener, a
/// loopback HTTP call, a real TTS provider) cannot run in a unit test, so what is actually under test here is
/// the sequencing, with those three swapped for fakes. <see cref="VoiceTurnOrchestrator"/> is the thin
/// production wiring on top.</para></summary>
internal sealed class SatelliteTurnRunner
{
    /// <summary>Sample rate the device is sent. Matches what the satellite's amp runs at, so nothing resamples
    /// on a microcontroller.</summary>
    private const int DeviceSampleRate = 16000;

    private readonly ISatelliteLink _link;
    private readonly IVoiceAssistantCaller _assistant;

    /// <summary>Synthesizes and delivers the reply, returning bytes sent. Defaults to the real TTS-backed
    /// implementation; a test supplies a trivial one instead, so proving the orchestration's call order needs
    /// no TTS provider, no engine, and no GPU.</summary>
    private readonly Func<string, string, CancellationToken, Task<int>> _speak;

    /// <summary>Turns in flight, keyed by device. One at a time per satellite: a second wake word during a
    /// reply cancels the first rather than interleaving two voices on one speaker.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    private int _loggedSessionModeUnavailable;

    public SatelliteTurnRunner(ISatelliteLink link, IVoiceAssistantCaller assistant,
        Func<string, string, CancellationToken, Task<int>> speak = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _speak = speak ?? SpeakToDeviceAsync;
    }

    /// <summary>Cancels every turn in flight (shutdown).</summary>
    public void CancelAll()
    {
        foreach (CancellationTokenSource cts in _running.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _running.Clear();
    }

    /// <summary>Decides whether this detection is ours to answer, and starts the turn if so.</summary>
    /// <remarks>Returns immediately. A turn takes seconds and this is called from the wake service's own
    /// notification path, which also has a transcript to deliver to the device and webhooks to post.
    ///
    /// <para><paramref name="mode"/> selects <see cref="SatelliteVoiceMode.Session"/> having no effect yet
    /// beyond a once-logged warning: every turn runs the <see cref="SatelliteVoiceMode.Legacy"/> sequence below
    /// regardless, since <see cref="SatelliteVoiceMode.Session"/> is not implemented (see its own remarks on
    /// why). Checked here, not by the caller, so the warning fires at most once per process even though
    /// <see cref="WakeWordService.GetSettings"/> is re-read on every detection.</para></remarks>
    public void OnDetected(JObject payload, SatelliteVoiceMode mode)
    {
        string deviceId = payload["device_id"]?.ToString();
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }
        // A configured route means an external orchestrator owns this word. Answering it here as well would
        // give the user two replies, from two systems that do not know about each other.
        //
        // But the transcript went out marked `handled`, because that mark is set for the whole listener and not
        // per word — so the device has already stood down and is waiting for a reply this is about to not send.
        // Releasing it here is the difference between the external route answering a device that is listening
        // and one that sits deaf for the whole 45 seconds its watchdog takes to give up.
        if (!string.IsNullOrWhiteSpace(payload["route"]?.ToString()))
        {
            _ = _link.SendStatusAsync(deviceId, WakeStatus.Done);
            return;
        }
        // Prefer the command; fall back to the transcript only when the engine did not separate them. An
        // empty command is the user saying the wake word and nothing else, and is not a question.
        JToken commandToken = payload["command"];
        string text = commandToken is null || commandToken.Type == JTokenType.Null
            ? payload["transcript"]?.ToString()
            : commandToken.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            _ = _link.SendStatusAsync(deviceId, WakeStatus.Done);
            return;
        }
        if (mode == SatelliteVoiceMode.Session && Interlocked.Exchange(ref _loggedSessionModeUnavailable, 1) == 0)
        {
            Logs.Warning("[AudioLab][Turn] SatelliteVoiceMode is set to Session, but Session mode needs engine "
                + "support that is not published yet (no hook for raw post-wake audio on a device the wake "
                + "listener already owns); running the Legacy turn instead. See the AudioLab README's Wake word "
                + "listener section.");
        }

        CancellationTokenSource cts = new();
        if (_running.TryRemove(deviceId, out CancellationTokenSource previous))
        {
            // Barge-in: the user spoke again while the last reply was still playing. Stop that one.
            try { previous.Cancel(); previous.Dispose(); } catch (ObjectDisposedException) { }
        }
        _running[deviceId] = cts;
        _ = Task.Run(() => RunTurnAsync(deviceId, text, cts), CancellationToken.None);
    }

    private async Task RunTurnAsync(string deviceId, string text, CancellationTokenSource cts)
    {
        long started = Environment.TickCount64;
        try
        {
            await _link.SendStatusAsync(deviceId, WakeStatus.Thinking).ConfigureAwait(false);
            (string reply, string error) = await _assistant.AskAsync(text, cts.Token).ConfigureAwait(false);
            if (error is not null)
            {
                Logs.Error($"[AudioLab][Turn] '{deviceId}': {error}");
                await _link.SendStatusAsync(deviceId, WakeStatus.Error, error).ConfigureAwait(false);
                return;
            }
            long thought = Environment.TickCount64 - started;

            await _link.SendStatusAsync(deviceId, WakeStatus.Speaking).ConfigureAwait(false);
            int sent = await _speak(deviceId, reply, cts.Token).ConfigureAwait(false);
            Logs.Debug($"[AudioLab][Turn] '{deviceId}': {thought}ms to a {reply.Length}-character reply, "
                + $"{sent / 2.0 / DeviceSampleRate:0.0}s of audio, {Environment.TickCount64 - started}ms total.");
        }
        catch (OperationCanceledException)
        {
            // Barge-in or shutdown. The device stops receiving audio and its own ring drains; nothing to say.
            Logs.Debug($"[AudioLab][Turn] '{deviceId}': turn cancelled.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Turn] '{deviceId}' failed: {ex.ReadableString()}");
            await _link.SendStatusAsync(deviceId, WakeStatus.Error, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            // Only the turn that is still the device's current one may end it. On a barge-in the cancelled turn
            // and the turn that replaced it are running side by side for a moment, and a `done` from the old
            // one would close the new one on the device — which stops listening to a reply that is still on its
            // way, and reopens itself to hearing that reply as a wake word.
            bool stillOurs = _running.TryGetValue(deviceId, out CancellationTokenSource current)
                && ReferenceEquals(current, cts);
            if (stillOurs)
            {
                _running.TryRemove(deviceId, out _);
                await _link.SendStatusAsync(deviceId, WakeStatus.Done).ConfigureAwait(false);
            }
            cts.Dispose();
        }
    }

    /// <summary>Synthesizes the reply and pushes it to the device as it is produced. The default
    /// <see cref="_speak"/> implementation; a test supplies its own instead.</summary>
    /// <remarks>Sentence by sentence where the model supports it, so the satellite starts playing after the
    /// first sentence rather than after the last. <see cref="ISatelliteLink.SendAudioAsync"/> paces each chunk,
    /// so this loop runs at roughly the speed of speech and cancels promptly on a barge-in.</remarks>
    private async Task<int> SpeakToDeviceAsync(string deviceId, string reply, CancellationToken cancel)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById("piper_tts")
            ?? AudioProviderRegistry.GetByCategory(AudioCategory.TTS).FirstOrDefault()
            ?? throw new InvalidOperationException("No text-to-speech provider is available.");

        Dictionary<string, object> args = new()
        {
            ["text"] = reply,
            ["voice"] = provider.Id.Equals("piper_tts", StringComparison.OrdinalIgnoreCase)
                ? "en_US-lessac-medium" : AudioConfiguration.DefaultVoice,
            ["language"] = AudioConfiguration.DefaultLanguage,
            ["volume"] = 1.0,
        };

        if (!AudioEngineBridge.SupportsNativeStreaming(provider.Id))
        {
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, null).ConfigureAwait(false);
            if (result["success"]?.Value<bool>() != true)
            {
                throw new InvalidOperationException(result["error"]?.ToString() ?? "synthesis produced no audio.");
            }
            byte[] wav = Convert.FromBase64String(result["audio_data"]?.ToString() ?? "");
            (int rate, int channels, int bits) = AudioIo.ReadWavFormat(wav);
            float[] mono = AudioLabAPI.PcmToMono(AudioIo.StripWavHeader(wav), channels, bits);
            return await _link.SendAudioAsync(deviceId, Encode(mono, rate), DeviceSampleRate, cancel)
                .ConfigureAwait(false);
        }

        // One stream for the whole reply, not one per sentence. Each call to SendAudioAsync numbers its frames
        // from zero and marks its last one final, which is exactly what the device reads as "a new reply is
        // starting" — so a sentence at a time would reset its playback ring between every sentence and it would
        // hear only fragments of the last one.
        WakeAudioStream stream = _link.BeginAudio(deviceId, DeviceSampleRate);
        if (stream is null)
        {
            return 0;  // The satellite dropped between the transcript and the reply.
        }
        await using (stream.ConfigureAwait(false))
        {
            await foreach (AudioChunk chunk in AudioEngineBridge.ProcessStreamAsync(provider.Id, args, cancel)
                .ConfigureAwait(false))
            {
                if (chunk.Samples is null || chunk.Samples.Length == 0)
                {
                    continue;
                }
                float[] mono = chunk.Channels > 1 ? Downmix(chunk.Samples, chunk.Channels) : chunk.Samples;
                await stream.WriteAsync(Encode(mono, chunk.SampleRate), cancel).ConfigureAwait(false);
            }
            await stream.CompleteAsync(cancel).ConfigureAwait(false);
            return stream.BytesSent;
        }
    }

    /// <summary>Resamples to the device's rate and packs to little-endian 16-bit.</summary>
    private static byte[] Encode(float[] mono, int sourceRate)
    {
        if (sourceRate != DeviceSampleRate)
        {
            mono = HartsyInference.Audio.Io.Resampler.Create(sourceRate, DeviceSampleRate).Resample(mono);
        }
        byte[] pcm = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            short sample = (short)Math.Clamp(mono[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        float[] mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[i * channels + c];
            }
            mono[i] = sum / channels;
        }
        return mono;
    }
}

/// <summary>Runs a whole voice turn on the server and sends the reply back down the socket the satellite
/// already has open.
///
/// <para>Without this a satellite runs its own turn: it takes the transcript, opens a connection to ask the
/// assistant, opens another to ask for speech, and reads the audio back. Three connections and three protocols
/// on a device with a few hundred kilobytes of RAM, and every one of them is latency the user waits through
/// with the light unchanged. The server is already holding a socket to that device, already has the transcript
/// in hand the moment it exists, and does not have to ask anybody for either.</para>
///
/// <para>So the device's job becomes: stream audio up, play audio down. Everything between is here --
/// specifically in <see cref="SatelliteTurnRunner"/>, which this class owns one instance of (wired to the real
/// listener, loopback call, and TTS engine) and is the only thing <see cref="OnDetected"/> delegates to. See
/// that class's remarks for why the split exists.</para>
///
/// <para>Off unless switched on. Switching it on also marks every transcript frame <c>handled</c>, which is
/// what tells a satellite to stand down; firmware too old to read that mark answers the turn as well, and the
/// user hears the reply twice. It stands aside for any wake word with a <c>route</c> configured, since a route
/// means somebody else has claimed that turn.</para></summary>
public static class VoiceTurnOrchestrator
{
    private static readonly SatelliteTurnRunner _runner =
        new(WakeServiceSatelliteLink.Instance, LoopbackAssistantCaller.Instance);

    private static bool _subscribed;

    /// <summary>Starts listening for transcripts. Safe to call more than once.</summary>
    public static void Start()
    {
        if (_subscribed)
        {
            return;
        }
        WakeWordService.Detected += OnDetected;
        _subscribed = true;
        Logs.Init("[AudioLab][Turn] Server-side voice turns are available; enable them in the wake word settings.");
    }

    public static void Stop()
    {
        if (!_subscribed)
        {
            return;
        }
        WakeWordService.Detected -= OnDetected;
        _subscribed = false;
        _runner.CancelAll();
    }

    private static void OnDetected(JObject payload)
    {
        WakeWordSettings settings = WakeWordService.GetSettings();
        if (!settings.ServerSideTurns)
        {
            return;
        }
        _runner.OnDetected(payload, settings.SatelliteVoiceMode);
    }
}
