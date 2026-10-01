using System.IO;
using System.Net.WebSockets;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using HartsyInference.Engine.Requests;
using HartsyInference.Tools;
using HartsyInference.Voice;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>The browser "Voice Agent" WebSocket session: one <c>HartsyInference.Voice</c> phone-style call per
/// socket, answered through LLMAssistant instead of a local model (see <see cref="RemoteTextService"/>).</summary>
[API.APIClass("Browser voice agent: a real-time WebSocket session over HartsyInference.Voice, answered through LLMAssistant.")]
public static class VoiceSessionEndpoints
{
    /// <summary>How often the outbound pump checks for new reply audio. Matches the engine's own sender cadence
    /// (eg <c>PhoneLinkServer</c>'s 20 ms ticks) and <see cref="VoiceAgentOptions.OutboundSampleRate"/>'s frame math.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Shape of <see cref="VoiceAgentSession.ReadOutbound(Span{float}, out int)"/>, extracted so
    /// <see cref="PumpOutboundAsync"/> can be driven by a fake in <c>VoiceSessionEndpointsPumpTests</c> instead
    /// of a real session.</summary>
    internal delegate int ReadOutboundDelegate(Span<float> destination, out int turnId);

    public static void Register()
    {
        API.RegisterAPICall(AudioLabVoiceSession, false, AudioLabPermissions.PermProcessAudio);
    }

    /// <summary>One call. The handshake frame core already parsed for every route (<see cref="rawInput"/>, with
    /// <c>session_id</c> stripped) is this route's own <c>start</c> message
    /// (<c>{model, assistantId?, voice?, systemPrompt?, bargeIn?, inputRate}</c>) -- there is no second message
    /// before audio starts. This method does not return until the call ends: a WebSocket handler must loop itself
    /// or the framework closes the socket the moment it returns.</summary>
    public static async Task<JObject> AudioLabVoiceSession(Session session, WebSocket ws, JObject rawInput)
    {
        if (!VoiceSessionStart.TryParse(rawInput, out VoiceSessionStartRequest start, out string parseError))
        {
            await ws.SendJsonNoError(new JObject { ["error"] = parseError }, API.WebsocketTimeout);
            return null;
        }
        if (!VoiceInboundResampler.TryCreate(start.InputRate, out VoiceInboundResampler inbound, out string resamplerError))
        {
            await ws.SendJsonNoError(new JObject { ["error"] = resamplerError }, API.WebsocketTimeout);
            return null;
        }

        SemaphoreSlim sendLock = new(1, 1);
        async Task SendJsonAsync(JObject payload)
        {
            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ws.SendJson(payload, API.WebsocketTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab][Voice] Event send failed (socket likely closing): {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }
        async Task SendBinaryAsync(byte[] bytes)
        {
            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, Program.GlobalProgramCancel).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        RemoteTextService text = new(WebServer.PageURL, session.ID, start.Model, start.AssistantId);
        text.Notice += noticeText => _ = SendJsonAsync(new JObject { ["notice"] = noticeText });

        VoiceModelLease lease;
        try
        {
            await SendJsonAsync(new JObject { ["state"] = "Warming" }).ConfigureAwait(false);
            lease = await VoiceEngineModels.Shared.AcquireAsync(start, text, Program.GlobalProgramCancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab][Voice] Could not prepare the voice models: {ex.ReadableString()}");
            await SendJsonAsync(new JObject { ["error"] = $"Could not prepare the voice models: {ex.Message}" }).ConfigureAwait(false);
            return null;
        }
        if (lease.Notice is not null)
        {
            await SendJsonAsync(new JObject { ["notice"] = lease.Notice }).ConfigureAwait(false);
        }

        // Tool dispatch has exactly one owner, LLMAssistant: an empty registry means VoiceAgentSession's own
        // ToolLoop never has anything to dispatch even if it were somehow reached. See RemoteTextService's remarks.
        ToolRegistry emptyTools = new();
        VoiceAgentSession voiceSession = new(lease.Set, text, emptyTools, lease.SessionOptions);
        VoiceEngineModels.Shared.RegisterSession(voiceSession, lease);

        // Written on the event pump's pool thread (BargeIn), read on the outbound pump's own loop: a CAS keeps
        // both sides' view of "flush everything at or below this turn" consistent without a lock on the hot path.
        int lastFlushedTurnId = 0;
        void MarkFlushed(int turnId)
        {
            int current;
            do
            {
                current = Volatile.Read(ref lastFlushedTurnId);
                if (turnId <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref lastFlushedTurnId, turnId, current) != current);
        }

        voiceSession.EventRaised += ev => _ = SendJsonAsync(EventToJson(ev, MarkFlushed));

        using CancellationTokenSource pumpCancel = new();
        Task pumpTask = Task.CompletedTask;
        try
        {
            await voiceSession.StartAsync(Program.GlobalProgramCancel).ConfigureAwait(false);
            pumpTask = PumpOutboundAsync(voiceSession.ReadOutbound, lease.SessionOptions.OutboundSampleRate,
                () => Volatile.Read(ref lastFlushedTurnId), SendBinaryAsync, pumpCancel.Token);
            await ReceiveInboundAsync(ws, voiceSession, inbound, Program.GlobalProgramCancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[AudioLab][Voice] Session for user '{session.User?.UserID}' ended on an error: {ex.ReadableString()}");
        }
        finally
        {
            pumpCancel.Cancel();
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                await voiceSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab][Voice] Ending the session threw: {ex.Message}");
            }
            await VoiceEngineModels.Shared.ReleaseSessionAsync(voiceSession).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Reads client frames until the socket closes or a JSON <c>{"end":true}</c> arrives: binary frames
    /// are mono PCM16 at <c>start.inputRate</c>, resampled to 16 kHz and pushed into the session; any other text
    /// frame is ignored rather than ending the call, so a forward-compatible addition cannot kill an old client.</summary>
    private static async Task ReceiveInboundAsync(WebSocket ws, VoiceAgentSession voiceSession, VoiceInboundResampler inbound, CancellationToken cancel)
    {
        byte[] buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !cancel.IsCancellationRequested)
        {
            using MemoryStream accumulated = new();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                accumulated.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                float[] resampled = inbound.Push(Pcm16.ToFloat(accumulated.ToArray()));
                if (resampled.Length > 0)
                {
                    voiceSession.PushInbound(resampled);
                }
                continue;
            }
            string raw = Encoding.UTF8.GetString(accumulated.ToArray());
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            JObject message = JObject.Parse(raw);
            if (message["end"] is not null)
            {
                return;
            }
        }
    }

    /// <summary>Reads reply audio in ~20 ms steps and sends it as turn-tagged binary frames; never blocks the
    /// session's own audio threads (<see cref="VoiceAgentSession.ReadOutbound(Span{float}, out int)"/> never
    /// blocks, and returns one turn's audio at most per call, stopping where the turn changes -- so every frame
    /// this sends carries exactly the turn id that produced its samples, never two turns under one tag). Drops
    /// anything tagged at or below the last barge-in's turn, mirroring the engine's own voice-host sender -- the
    /// flush window the engine's docs describe can hand a tagged straggler to a reader an instant before the
    /// producer notices the flush moved again. Internal, and <paramref name="readOutbound"/> is a delegate rather
    /// than a <see cref="VoiceAgentSession"/> directly, so <c>VoiceSessionEndpointsPumpTests</c> can drive this
    /// with a fake read instead of a real session.</summary>
    internal static async Task PumpOutboundAsync(ReadOutboundDelegate readOutbound, int outboundSampleRate,
        Func<int> lastFlushedTurnIdRef, Func<byte[], Task> sendBinaryAsync, CancellationToken cancel)
    {
        int frameSamples = Math.Max(1, outboundSampleRate / 50); // 20 ms
        float[] buffer = new float[frameSamples];
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(PumpInterval, cancel).ConfigureAwait(false);
            int read = readOutbound(buffer, out int turnId);
            if (read <= 0)
            {
                continue;
            }
            if (turnId != 0 && turnId <= lastFlushedTurnIdRef())
            {
                continue; // a stale turn's straggling audio; the client has already been told to drop this turn.
            }
            await sendBinaryAsync(VoiceOutboundFrame.Encode(turnId, buffer.AsSpan(0, read))).ConfigureAwait(false);
        }
    }

    /// <summary>Maps one engine event to this route's JSON event vocabulary. <paramref name="markFlushed"/> is
    /// called for a <see cref="VoiceAgentEventKind.BargeIn"/> so the outbound pump above starts dropping that
    /// turn's straggling audio at the same moment the client is told to.</summary>
    private static JObject EventToJson(VoiceAgentEvent ev, Action<int> markFlushed)
    {
        switch (ev.Kind)
        {
            case VoiceAgentEventKind.StateChanged:
                return new JObject { ["state"] = ev.State.ToString() };
            case VoiceAgentEventKind.UserTranscript:
                return new JObject { ["transcript"] = new JObject { ["role"] = "user", ["text"] = ev.Text, ["turnId"] = ev.TurnId } };
            case VoiceAgentEventKind.AssistantTranscript:
                return new JObject { ["transcript"] = new JObject { ["role"] = "assistant", ["text"] = ev.Text, ["turnId"] = ev.TurnId } };
            case VoiceAgentEventKind.BargeIn:
                markFlushed(ev.TurnId);
                return new JObject { ["bargein"] = new JObject { ["turnId"] = ev.TurnId } };
            case VoiceAgentEventKind.ToolCall:
                return new JObject
                {
                    ["tool_call"] = new JObject { ["id"] = ev.ToolCall?.Id, ["name"] = ev.ToolCall?.Name, ["arguments"] = ev.ToolCall?.Arguments, ["turnId"] = ev.TurnId },
                };
            case VoiceAgentEventKind.ToolResult:
                return new JObject
                {
                    ["tool_result"] = new JObject { ["id"] = ev.ToolCall?.Id, ["name"] = ev.ToolCall?.Name, ["result"] = ev.Text, ["turnId"] = ev.TurnId },
                };
            case VoiceAgentEventKind.UtteranceDiscarded:
                return new JObject { ["notice"] = ev.Text };
            case VoiceAgentEventKind.TurnCompleted:
                return new JObject { ["metrics"] = MetricsToJson(ev.Metrics ?? default) };
            case VoiceAgentEventKind.Error:
                return new JObject { ["error"] = ev.Text };
            default:
                return new JObject { ["notice"] = $"Unhandled voice event {ev.Kind}." };
        }
    }

    private static JObject MetricsToJson(VoiceTurnMetrics m) => new()
    {
        ["turnId"] = m.TurnId,
        ["kind"] = m.Kind.ToString(),
        ["voice.frontend.ms.p50"] = m.FrontendP50Ms,
        ["voice.frontend.ms.p99"] = m.FrontendP99Ms,
        ["voice.frontend.ms.max"] = m.FrontendMaxMs,
        ["voice.endpoint.ms"] = m.EndpointMs,
        ["voice.stt.ms"] = m.SttMs,
        ["voice.llm.ttft_ms"] = m.LlmTtftMs,
        ["voice.llm.first_sentence_ms"] = m.LlmFirstSentenceMs,
        ["voice.tts.first_chunk_ms"] = m.TtsFirstChunkMs,
        ["voice.transport.ms"] = m.TransportMs,
        ["voice.turn.total_ms"] = m.TotalMs,
        ["voice.bargein.stop_ms"] = m.BargeInStopMs,
        ["toolCalls"] = m.ToolCalls,
        ["interrupted"] = m.Interrupted,
    };
}
