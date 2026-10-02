using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using HartsyInference.Tools;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices.Voice;

/// <summary>An <see cref="ITextService"/> that answers through LLMAssistant's <c>LLMAssistantVoiceTurnWS</c> route
/// instead of a local model: a loopback WebSocket client, one fresh connection per <see cref="StreamAsync"/> call,
/// carrying the real browser session's id so the reply comes from that user's own assistants and permissions
/// rather than an anonymous one (see <see cref="VoiceSessionEndpoints"/>, which is the only place that constructs
/// this and already holds that <c>Session</c>).
///
/// <para><b>Tool calls have exactly one owner: LLMAssistant.</b> It dispatches tools through its own registry and
/// reports both halves back over the wire (<c>native_tool_call</c> then <c>tool_result</c>); this class re-emits
/// them as the exact <see cref="TextChunk"/> shapes <see cref="HartsyInference.Tools.ToolLoop"/> itself produces
/// (<see cref="TextChunkKind.NativeToolCall"/>, then a <see cref="TextChunkKind.Status"/> chunk phased
/// <see cref="ToolLoop.ToolResultPhase"/>) so <c>VoiceAgentSession</c>'s own turn code records the exchange in its
/// conversation and raises its own <c>ToolCall</c>/<c>ToolResult</c> events -- exactly as it would for a tool a
/// local engine called. The stream this class yields never ends with <see cref="StopReason.ToolCall"/>, under any
/// wire <c>stopReason</c>, which is what keeps <c>ToolLoop</c> from ever trying to dispatch through the empty
/// <see cref="ToolRegistry"/> the voice session is built with -- dispatch already happened, remotely, by the time
/// a <c>done</c> frame arrives. The one wire event with nowhere to land in that chunk vocabulary is
/// <c>notice</c> (no <see cref="TextChunkKind"/> for it, and an unmatched <see cref="TextChunkKind.Status"/> phase
/// is silently dropped by the session's own switch): it is raised on <see cref="Notice"/> instead, which
/// <see cref="VoiceSessionEndpoints"/> subscribes to directly.</para></summary>
internal sealed class RemoteTextService : ITextService
{
    private readonly string _wsUrl;
    private readonly string _sessionId;
    private readonly string _model;
    private readonly string _assistantId;

    /// <summary>Raised for a <c>{notice:"..."}</c> wire frame (eg "tool calling is unavailable for this model").
    /// Invoked on whatever thread is driving <see cref="StreamAsync"/>; a throwing handler is logged and does not
    /// stop the stream.</summary>
    public event Action<string> Notice;

    /// <param name="pageUrl">The server's own base URL (eg <c>SwarmUI.Core.WebServer.PageURL</c>), http(s).</param>
    /// <param name="sessionId">The browser session id to forward, so LLMAssistant answers as that user.</param>
    /// <param name="model">LLMAssistant model id, or null to let it pick its default.</param>
    /// <param name="assistantId">LLMAssistant assistant id, or null for its default.</param>
    public RemoteTextService(string pageUrl, string sessionId, string model, string assistantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _wsUrl = ToWebSocketUrl(pageUrl.TrimEnd('/')) + "/API/LLMAssistantVoiceTurnWS";
        _sessionId = sessionId;
        _model = model;
        _assistantId = assistantId;
    }

    /// <summary>Converts an http(s) base URL to its ws(s) equivalent. Internal and pure so a test can check the
    /// mapping without opening a socket.</summary>
    internal static string ToWebSocketUrl(string httpUrl)
    {
        if (httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "wss://" + httpUrl["https://".Length..];
        }
        if (httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "ws://" + httpUrl["http://".Length..];
        }
        throw new ArgumentException($"'{httpUrl}' is not an http(s) URL.", nameof(httpUrl));
    }

    /// <summary>Streams one LLMAssistant turn over a fresh loopback WebSocket. Never yields
    /// <see cref="TextChunkKind.ToolCallDelta"/>/<see cref="TextChunkKind.ToolCallAbort"/> (LLMAssistant's wire
    /// protocol has no streaming-tool-call-delta concept) and never a <see cref="StopReason.ToolCall"/> stop (see
    /// the class remarks). Ending normally (<c>done</c>, <c>error</c>, or the server closing first) completes a
    /// polite WebSocket close handshake first (<see cref="CloseGracefullyAsync"/>) -- LLMAssistant's own route
    /// relies on that handshake to let its post-handler <c>CloseAsync</c> finish cleanly, which a bare
    /// <c>Dispose</c> (a TCP abort) would otherwise surface there as a logged error for what was actually a
    /// clean finish. Cancellation skips that and disposes the socket immediately -- abandoning the turn is the
    /// point there -- which LLMAssistant's own route reads as the client closing and cancels its turn on.</summary>
    public async IAsyncEnumerable<TextChunk> StreamAsync(ModelSpec spec, TextRequest request, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using ClientWebSocket socket = new();
        bool connected = false;
        string connectError = null;
        try
        {
            await socket.ConnectAsync(new Uri(_wsUrl), cancel).ConfigureAwait(false);
            JObject startFrame = BuildRequestFrame(request);
            byte[] bytes = Encoding.UTF8.GetBytes(startFrame.ToString(Newtonsoft.Json.Formatting.None));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel).ConfigureAwait(false);
            connected = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            connectError = ex.Message;
        }
        if (!connected)
        {
            await CloseGracefullyAsync(socket).ConfigureAwait(false);
            yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = $"Could not reach the assistant at {_wsUrl}: {connectError}" };
            yield break;
        }
        Dictionary<string, NativeToolCall> pendingCalls = new(StringComparer.Ordinal);
        int toolIndex = 0;
        while (true)
        {
            JObject frame = null;
            string receiveError = null;
            try
            {
                frame = await ReceiveJsonAsync(socket, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                receiveError = ex.Message;
            }
            if (receiveError is not null)
            {
                await CloseGracefullyAsync(socket).ConfigureAwait(false);
                yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = $"Lost the connection to the assistant: {receiveError}" };
                yield break;
            }
            if (frame is null)
            {
                // The server already sent its own Close frame (ReceiveJsonAsync returns null for that message
                // type): CloseAsync here only needs to send our acknowledging Close, which .NET's WebSocket
                // completes at once when the peer's Close was already received -- not a second round trip.
                await CloseGracefullyAsync(socket).ConfigureAwait(false);
                yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = "The assistant closed the connection without finishing the reply." };
                yield break;
            }
            if (frame.TryGetValue("chunk", out JToken chunkToken))
            {
                yield return new TextChunk { Kind = TextChunkKind.Chunk, Text = chunkToken.ToString() };
                continue;
            }
            if (frame["native_tool_call"] is JObject callFrame)
            {
                NativeToolCall call = new()
                {
                    Id = Str(callFrame, "id"),
                    Name = Str(callFrame, "name"),
                    // LLMAssistant's ForwardFramesAsync embeds ParseJsonOrEmpty(call.Arguments) -- a nested JSON
                    // value, not a JSON-encoded string -- so this re-serializes it compactly rather than taking
                    // JToken.ToString()'s indented default, which would otherwise hand NativeToolCall.Arguments
                    // (documented as a raw JSON string) a multi-line one.
                    Arguments = JsonFieldAsCompactString(callFrame, "arguments"),
                };
                if (!string.IsNullOrEmpty(call.Id))
                {
                    pendingCalls[call.Id] = call;
                }
                yield return new TextChunk { Kind = TextChunkKind.NativeToolCall, ToolCall = call, ToolCallIndex = toolIndex++ };
                continue;
            }
            if (frame["tool_result"] is JObject resultFrame)
            {
                string id = Str(resultFrame, "id");
                NativeToolCall call = id.Length > 0 && pendingCalls.TryGetValue(id, out NativeToolCall found)
                    ? found
                    : new NativeToolCall { Id = id, Name = Str(resultFrame, "name") };
                pendingCalls.Remove(id);
                yield return new TextChunk
                {
                    Kind = TextChunkKind.Status,
                    Status = new TextStatus(ToolLoop.ToolResultPhase),
                    // Same nested-JSON-value shape as "arguments" above (LLMAssistant's result is also
                    // ParseJsonOrEmpty(...), not a bare string).
                    Text = ToolLoop.ToolResultPrefix + JsonFieldAsCompactString(resultFrame, "result"),
                    ToolCall = call,
                    ToolCallIndex = toolIndex++,
                };
                continue;
            }
            if (frame["notice"] is JToken noticeToken)
            {
                RaiseNotice(noticeToken.ToString());
                continue;
            }
            if (frame["done"]?.Value<bool>() == true)
            {
                // Polite close before yielding the last chunk: LLMAssistant keeps one uncancelled background
                // ReceiveAsync to detect a disconnect, and its own post-handler CloseAsync relies on this side
                // completing the WebSocket close handshake. A browser's WebSocket does this on its own;
                // ClientWebSocket does not unless asked, and the `using` above would otherwise TCP-abort, which
                // surfaces there as a logged error for what was actually a clean finish.
                await CloseGracefullyAsync(socket).ConfigureAwait(false);
                yield return new TextChunk { Kind = TextChunkKind.Result, Text = Str(frame, "full_text") };
                yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = MapStopReason(frame["stopReason"]?.ToString()) };
                yield break;
            }
            if (frame["error"] is JToken errorToken)
            {
                await CloseGracefullyAsync(socket).ConfigureAwait(false);
                yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = errorToken.ToString() };
                yield break;
            }
            // An unrecognized frame shape: ignore and keep reading, so a future, additive wire field does not
            // kill an otherwise-healthy turn.
        }
    }

    /// <summary>Accumulates <see cref="StreamAsync"/>. <see cref="TextResult.PromptTokens"/>/
    /// <see cref="TextResult.CompletionTokens"/> are the same local <see cref="CountTokens"/> heuristic the wire
    /// protocol gives no real counts for.</summary>
    public async Task<TextResult> GenerateAsync(ModelSpec spec, TextRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        StringBuilder full = new();
        StopReason stop = StopReason.Stop;
        await foreach (TextChunk chunk in StreamAsync(spec, request, cancel).WithCancellation(cancel).ConfigureAwait(false))
        {
            switch (chunk.Kind)
            {
                case TextChunkKind.Chunk:
                    full.Append(chunk.Text);
                    break;
                case TextChunkKind.Result:
                    full.Clear();
                    full.Append(chunk.Text);
                    break;
                case TextChunkKind.StopReason:
                    stop = chunk.Stop ?? StopReason.Stop;
                    break;
            }
        }
        string text = full.ToString();
        string prompt = string.Concat(request.Messages.Select(m => m.Content));
        return new TextResult { Text = text, Stop = stop, PromptTokens = CountTokens(spec, prompt), CompletionTokens = CountTokens(spec, text) };
    }

    /// <summary>Local heuristic -- ceil(chars/4) -- never a blocking loopback call. The remote end owns the real
    /// tokenizer and nothing here can reach it without a round trip this method's contract forbids.</summary>
    public int CountTokens(ModelSpec spec, string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>Nothing is resident on this side to unload; always false.</summary>
    public bool Unload(string device = null) => false;

    private JObject BuildRequestFrame(TextRequest request)
    {
        JArray messages = [];
        foreach (TextMessage message in request.Messages)
        {
            JObject m = new() { ["role"] = RoleString(message.Role), ["content"] = message.Content };
            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                m["toolCallId"] = message.ToolCallId;
            }
            if (!string.IsNullOrEmpty(message.Name))
            {
                m["name"] = message.Name;
            }
            if (message.ToolCalls is { Count: > 0 })
            {
                JArray calls = [];
                foreach (NativeToolCall call in message.ToolCalls)
                {
                    // LLMAssistant's own doc for this field: "the same {id, name, arguments} shape a
                    // native_tool_call frame sent earlier" -- which embeds arguments as a parsed JSON value,
                    // not a string (see JsonFieldAsCompactString's remarks on the read side). Mirrored here so
                    // a replayed call round-trips the same shape either direction.
                    calls.Add(new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = ParseJsonOrEmptyObject(call.Arguments) });
                }
                m["toolCalls"] = calls;
            }
            messages.Add(m);
        }
        JObject frame = new()
        {
            ["session_id"] = _sessionId,
            ["messages"] = messages,
            ["maxTokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
        };
        if (request.EnableThinking.HasValue)
        {
            frame["enableThinking"] = request.EnableThinking.Value;
        }
        if (!string.IsNullOrWhiteSpace(_model))
        {
            frame["model"] = _model;
        }
        if (!string.IsNullOrWhiteSpace(_assistantId))
        {
            frame["assistantId"] = _assistantId;
        }
        if (!string.IsNullOrWhiteSpace(request.PrefixCacheKey))
        {
            // The voice session's per-call key, set on its priming request and on every turn. LLMAssistant scopes
            // its local engine's prefix-KV reuse by conversationId (under the calling user and the model), so each
            // call keeps its own retained entry; without it, every call made from this browser session would
            // share one entry keyed by session_id.
            frame["conversationId"] = request.PrefixCacheKey;
        }
        return frame;
    }

    private void RaiseNotice(string text)
    {
        try
        {
            Notice?.Invoke(text);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab][Voice] A Notice handler threw: {ex.Message}");
        }
    }

    private static string RoleString(TextRole role) => role switch
    {
        TextRole.System => "system",
        TextRole.User => "user",
        TextRole.Assistant => "assistant",
        TextRole.Tool => "tool",
        _ => "user",
    };

    /// <summary>Never <see cref="StopReason.ToolCall"/>, whatever the wire says -- see the class remarks. Anything
    /// unrecognized (including null, absent, or anything tool-shaped) maps to the safe default, <see cref="StopReason.Stop"/>.</summary>
    private static StopReason MapStopReason(string wire) => wire?.ToLowerInvariant() switch
    {
        "length" => StopReason.Length,
        "cancelled" or "canceled" => StopReason.Cancelled,
        "error" => StopReason.Error,
        _ => StopReason.Stop,
    };

    private static string Str(JObject obj, string key) => obj[key]?.ToString() ?? "";

    /// <summary>Reads a field that may be a nested JSON value (LLMAssistant's <c>arguments</c>/<c>result</c>,
    /// each <c>ParseJsonOrEmpty(...)</c>'d before being embedded) back into the compact JSON-string form
    /// <see cref="NativeToolCall.Arguments"/> and a tool-result's text both document, rather than
    /// <see cref="JToken.ToString()"/>'s indented default. A field that is already a plain JSON string is
    /// returned as-is, not re-encoded.</summary>
    private static string JsonFieldAsCompactString(JObject obj, string key)
    {
        JToken token = obj[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            return "";
        }
        return token.Type == JTokenType.String ? token.Value<string>() ?? "" : token.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Parses a JSON-arguments string into a <see cref="JToken"/> for embedding as a nested wire value
    /// (the inverse of <see cref="JsonFieldAsCompactString"/>); an empty/invalid string becomes an empty object,
    /// mirroring LLMAssistant's own <c>ParseJsonOrEmpty</c> rather than failing the whole request over one
    /// malformed replayed call.</summary>
    private static JToken ParseJsonOrEmptyObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }
        try
        {
            return JToken.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JObject();
        }
    }

    /// <summary>Completes the WebSocket close handshake rather than letting <see cref="StreamAsync"/>'s own
    /// <c>using</c> abort the TCP connection -- see the class remarks. <see cref="ClientWebSocket.CloseAsync"/>
    /// is state-aware: on a socket still <see cref="WebSocketState.Open"/> it sends Close and waits for the
    /// peer's Close in reply; on one that already received the peer's Close first
    /// (<see cref="WebSocketState.CloseReceived"/>, the "server closed first" exit path) it only needs to send
    /// the acknowledging Close and returns at once -- not a second round trip either way. Bounded by its own
    /// short timeout and never throws: a close that cannot complete gracefully (the connection is already gone)
    /// just falls back to the `using`'s abrupt Dispose, which is correct for that case too.</summary>
    private static async Task CloseGracefullyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the `using` at the call site still disposes (aborts) the socket regardless.
        }
    }

    private static async Task<JObject> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream accumulated = new();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            accumulated.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        string raw = Encoding.UTF8.GetString(accumulated.ToArray());
        return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
    }
}
