using System.Net.WebSockets;
using Hartsy.Extensions.AudioLab.AudioServices.Voice;
using Hartsy.Extensions.AudioLab.Tests.Support;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="RemoteTextService"/> against <see cref="FakeVoiceTurnServer"/>: every wire
/// frame type, <c>stopReason</c> mapping, a mid-stream error, a connect failure, and cancellation closing the
/// socket -- no SwarmUI core, no LLMAssistant, no engine.</summary>
public class RemoteTextServiceTests
{
    private static ModelSpec Spec() => new() { Requested = "ignored-by-remote-text-service", Modality = Modality.Text };

    private static TextRequest SimpleRequest() => new() { Messages = [new TextMessage { Role = TextRole.User, Content = "Hello" }] };

    private static async Task<(JObject ServerRequest, List<TextChunk> Chunks, List<string> Notices)> RunScriptedTurnAsync(
        FakeVoiceTurnServer server, IEnumerable<JObject> replyFrames, TextRequest? request = null, string? model = "qwen3", string? assistantId = "asst-1")
    {
        RemoteTextService service = new(server.BaseUrl, "sess-123", model, assistantId);
        List<string> notices = [];
        service.Notice += notices.Add;

        Task<JObject> serverTask = Task.Run(async () =>
        {
            (JObject req, WebSocket socket) = await server.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (JObject frame in replyFrames)
            {
                await FakeVoiceTurnServer.SendAsync(socket, frame, CancellationToken.None).ConfigureAwait(false);
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            return req;
        });

        List<TextChunk> chunks = [];
        await foreach (TextChunk chunk in service.StreamAsync(Spec(), request ?? SimpleRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }
        JObject serverRequest = await serverTask.ConfigureAwait(false);
        return (serverRequest, chunks, notices);
    }

    [Fact]
    public async Task StreamAsync_SendsTheMappedRequestFrame()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        TextRequest request = new()
        {
            Messages =
            [
                new TextMessage { Role = TextRole.System, Content = "Be terse." },
                new TextMessage { Role = TextRole.User, Content = "Hi" },
            ],
            MaxTokens = 123,
            Temperature = 0.42,
            EnableThinking = false,
        };
        (JObject serverRequest, _, _) = await RunScriptedTurnAsync(server, [new JObject { ["done"] = true, ["full_text"] = "ok" }], request);
        Assert.Equal("sess-123", serverRequest["session_id"]!.ToString());
        Assert.Equal("qwen3", serverRequest["model"]!.ToString());
        Assert.Equal("asst-1", serverRequest["assistantId"]!.ToString());
        Assert.Equal(123, serverRequest["maxTokens"]!.Value<int>());
        Assert.Equal(0.42, serverRequest["temperature"]!.Value<double>(), 3);
        Assert.False(serverRequest["enableThinking"]!.Value<bool>());
        JArray messages = (JArray)serverRequest["messages"]!;
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]["role"]!.ToString());
        Assert.Equal("Be terse.", messages[0]["content"]!.ToString());
        Assert.Equal("user", messages[1]["role"]!.ToString());
    }

    [Fact]
    public async Task StreamAsync_ToolMessages_SerializeToolCallIdNameAndToolCalls()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        TextRequest request = new()
        {
            Messages =
            [
                new TextMessage { Role = TextRole.User, Content = "What's the time?" },
                new TextMessage
                {
                    Role = TextRole.Assistant, Content = "",
                    ToolCalls = [new NativeToolCall { Id = "call-1", Name = "get_time", Arguments = "{}" }],
                },
                new TextMessage { Role = TextRole.Tool, Content = "{\"time\":\"noon\"}", ToolCallId = "call-1", Name = "get_time" },
            ],
        };
        (JObject serverRequest, _, _) = await RunScriptedTurnAsync(server, [new JObject { ["done"] = true, ["full_text"] = "It's noon." }], request);
        JArray messages = (JArray)serverRequest["messages"]!;
        JArray toolCalls = (JArray)messages[1]["toolCalls"]!;
        Assert.Equal("call-1", toolCalls[0]["id"]!.ToString());
        Assert.Equal("get_time", toolCalls[0]["name"]!.ToString());
        Assert.Equal("call-1", messages[2]["toolCallId"]!.ToString());
        Assert.Equal("get_time", messages[2]["name"]!.ToString());
    }

    [Fact]
    public async Task StreamAsync_ChunkFrames_YieldChunkKindChunksInOrder()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        (_, List<TextChunk> chunks, _) = await RunScriptedTurnAsync(server,
        [
            new JObject { ["chunk"] = "Hello" },
            new JObject { ["chunk"] = ", world" },
            new JObject { ["done"] = true, ["full_text"] = "Hello, world" },
        ]);
        Assert.Equal(TextChunkKind.Chunk, chunks[0].Kind);
        Assert.Equal("Hello", chunks[0].Text);
        Assert.Equal(TextChunkKind.Chunk, chunks[1].Kind);
        Assert.Equal(", world", chunks[1].Text);
        TextChunk result = Assert.Single(chunks, c => c.Kind == TextChunkKind.Result);
        Assert.Equal("Hello, world", result.Text);
        TextChunk stop = Assert.Single(chunks, c => c.Kind == TextChunkKind.StopReason);
        Assert.Equal(StopReason.Stop, stop.Stop);
    }

    [Fact]
    public async Task StreamAsync_NativeToolCallThenToolResult_MatchToolLoopsOwnChunkShapes()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        // LLMAssistant's ForwardFramesAsync embeds `arguments`/`result` as ParseJsonOrEmpty(...) -- a nested
        // JSON value, not a JSON-encoded string -- so these fixtures use real nested objects, not string literals.
        (_, List<TextChunk> chunks, _) = await RunScriptedTurnAsync(server,
        [
            new JObject { ["native_tool_call"] = new JObject { ["id"] = "call-1", ["name"] = "get_time", ["arguments"] = new JObject() } },
            new JObject { ["tool_result"] = new JObject { ["id"] = "call-1", ["name"] = "get_time", ["result"] = new JObject { ["time"] = "noon" } } },
            new JObject { ["chunk"] = "It's noon." },
            new JObject { ["done"] = true, ["full_text"] = "It's noon." },
        ]);

        TextChunk call = Assert.Single(chunks, c => c.Kind == TextChunkKind.NativeToolCall);
        Assert.Equal("call-1", call.ToolCall!.Id);
        Assert.Equal("get_time", call.ToolCall!.Name);
        Assert.Equal("{}", call.ToolCall!.Arguments); // compact, not JToken.ToString()'s indented default.

        TextChunk result = Assert.Single(chunks, c => c.Kind == TextChunkKind.Status && c.Status?.Phase == ToolLoop.ToolResultPhase);
        Assert.Equal("call-1", result.ToolCall!.Id);
        Assert.StartsWith(ToolLoop.ToolResultPrefix, result.Text);
        Assert.Equal(ToolLoop.ToolResultPrefix + "{\"time\":\"noon\"}", result.Text);

        // Never a ToolCall stop: that is what would send VoiceAgentSession's ToolLoop looking for a dispatch
        // through the (intentionally empty) local ToolRegistry, even though the real dispatch already happened,
        // remotely, by the time `done` arrived.
        Assert.DoesNotContain(chunks, c => c.Kind == TextChunkKind.StopReason && c.Stop == StopReason.ToolCall);
    }

    [Theory]
    [InlineData("stop", StopReason.Stop)]
    [InlineData("length", StopReason.Length)]
    [InlineData("cancelled", StopReason.Cancelled)]
    [InlineData("canceled", StopReason.Cancelled)]
    [InlineData("tool_call", StopReason.Stop)] // never ToolCall, whatever the wire says -- see the class remarks.
    [InlineData("something_unexpected", StopReason.Stop)]
    [InlineData(null, StopReason.Stop)]
    public async Task StreamAsync_Done_MapsStopReason_AndNeverToolCall(string? wireStopReason, StopReason expected)
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        JObject doneFrame = new() { ["done"] = true, ["full_text"] = "ok" };
        if (wireStopReason is not null)
        {
            doneFrame["stopReason"] = wireStopReason;
        }
        (_, List<TextChunk> chunks, _) = await RunScriptedTurnAsync(server, [doneFrame]);
        TextChunk stop = Assert.Single(chunks, c => c.Kind == TextChunkKind.StopReason);
        Assert.Equal(expected, stop.Stop);
        Assert.NotEqual(StopReason.ToolCall, stop.Stop);
    }

    [Fact]
    public async Task StreamAsync_Notice_RaisesTheEvent_AndYieldsNoChunkForIt()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        (_, List<TextChunk> chunks, List<string> notices) = await RunScriptedTurnAsync(server,
        [
            new JObject { ["notice"] = "tool calling is unavailable for this model" },
            new JObject { ["done"] = true, ["full_text"] = "ok" },
        ]);
        Assert.Equal(["tool calling is unavailable for this model"], notices);
        Assert.DoesNotContain(chunks, c => c.Text != null && c.Text.Contains("unavailable"));
    }

    [Fact]
    public async Task StreamAsync_ErrorFrame_EndsTheStreamWithOneErrorStopChunk()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        (_, List<TextChunk> chunks, _) = await RunScriptedTurnAsync(server, [new JObject { ["error"] = "the model crashed" }]);
        TextChunk only = Assert.Single(chunks);
        Assert.Equal(TextChunkKind.StopReason, only.Kind);
        Assert.Equal(StopReason.Error, only.Stop);
        Assert.Equal("the model crashed", only.Text);
    }

    [Fact]
    public async Task StreamAsync_ServerClosesWithoutDoneOrError_EndsWithAnErrorStopChunk()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        (_, List<TextChunk> chunks, _) = await RunScriptedTurnAsync(server, [new JObject { ["chunk"] = "partial" }]);
        Assert.Contains(chunks, c => c.Kind == TextChunkKind.Chunk && c.Text == "partial");
        TextChunk stop = Assert.Single(chunks, c => c.Kind == TextChunkKind.StopReason);
        Assert.Equal(StopReason.Error, stop.Stop);
    }

    [Fact]
    public async Task StreamAsync_CannotConnect_YieldsASingleErrorStopChunk_RatherThanThrowing()
    {
        // Nothing is listening on this port.
        RemoteTextService service = new("http://127.0.0.1:1", "sess-1", "qwen3", null);
        List<TextChunk> chunks = [];
        await foreach (TextChunk chunk in service.StreamAsync(Spec(), SimpleRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }
        TextChunk only = Assert.Single(chunks);
        Assert.Equal(TextChunkKind.StopReason, only.Kind);
        Assert.Equal(StopReason.Error, only.Stop);
    }

    [Fact]
    public async Task StreamAsync_Cancellation_ThrowsAndClosesTheClientSocket()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        RemoteTextService service = new(server.BaseUrl, "sess-1", "qwen3", null);
        using CancellationTokenSource cts = new();

        Task<WebSocket> acceptTask = Task.Run(async () =>
        {
            (JObject _, WebSocket socket) = await server.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            return socket;
        });

        IAsyncEnumerator<TextChunk> enumerator = service.StreamAsync(Spec(), SimpleRequest(), cts.Token).GetAsyncEnumerator();
        Task<bool> moveNextTask = enumerator.MoveNextAsync().AsTask();
        WebSocket serverSocket = await acceptTask; // the connection is up; the client is now blocked reading a reply.

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNextTask);

        bool sawClosure;
        try
        {
            byte[] buffer = new byte[16];
            WebSocketReceiveResult result = await serverSocket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            sawClosure = result.MessageType == WebSocketMessageType.Close;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            sawClosure = true; // an aborted (not gracefully closed) client connection surfaces this way instead.
        }
        Assert.True(sawClosure);
    }

    [Fact]
    public async Task StreamAsync_ANormalTurn_ClosesGracefully_WithNoServerSideException()
    {
        // Mirrors LLMAssistantVoiceTurnWS's own shape: the server sends its frames, then (as API.cs does right
        // after the handler returns) calls CloseAsync and waits for the client's half of the handshake. If
        // RemoteTextService just let its `using` abort the socket instead, this call would throw instead of
        // completing -- a WebSocketException the real server logs as an internal error for what was actually a
        // clean finish.
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        RemoteTextService service = new(server.BaseUrl, "sess-1", "qwen3", null);

        Exception? serverCloseException = null;
        WebSocketState serverStateAfterClose = WebSocketState.None;
        Task serverTask = Task.Run(async () =>
        {
            (JObject _, WebSocket socket) = await server.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            await FakeVoiceTurnServer.SendAsync(socket, new JObject { ["chunk"] = "Hi" }, CancellationToken.None).ConfigureAwait(false);
            await FakeVoiceTurnServer.SendAsync(socket, new JObject { ["done"] = true, ["full_text"] = "Hi" }, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                serverCloseException = ex;
            }
            serverStateAfterClose = socket.State;
        });

        List<TextChunk> chunks = [];
        await foreach (TextChunk chunk in service.StreamAsync(Spec(), SimpleRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }
        await serverTask;

        Assert.Null(serverCloseException);
        Assert.Equal(WebSocketState.Closed, serverStateAfterClose);
        Assert.Contains(chunks, c => c.Kind == TextChunkKind.StopReason && c.Stop == StopReason.Stop);
    }

    [Fact]
    public async Task GenerateAsync_AccumulatesChunks_IntoAFinalResult()
    {
        await using FakeVoiceTurnServer server = FakeVoiceTurnServer.Start();
        RemoteTextService service = new(server.BaseUrl, "sess-1", "qwen3", null);
        Task serverTask = Task.Run(async () =>
        {
            (JObject _, WebSocket socket) = await server.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            await FakeVoiceTurnServer.SendAsync(socket, new JObject { ["chunk"] = "Hi " }, CancellationToken.None);
            await FakeVoiceTurnServer.SendAsync(socket, new JObject { ["chunk"] = "there" }, CancellationToken.None);
            await FakeVoiceTurnServer.SendAsync(socket, new JObject { ["done"] = true, ["full_text"] = "Hi there" }, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        TextResult result = await service.GenerateAsync(Spec(), SimpleRequest(), CancellationToken.None);
        await serverTask;
        Assert.Equal("Hi there", result.Text);
        Assert.Equal(StopReason.Stop, result.Stop);
        Assert.True(result.PromptTokens >= 0);
        Assert.True(result.CompletionTokens > 0);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("ab", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    public void CountTokens_IsCeilCharsOverFour(string text, int expected)
    {
        RemoteTextService service = new("http://localhost", "s", null, null);
        Assert.Equal(expected, service.CountTokens(Spec(), text));
    }

    [Fact]
    public void Unload_AlwaysReturnsFalse()
    {
        RemoteTextService service = new("http://localhost", "s", null, null);
        Assert.False(service.Unload());
        Assert.False(service.Unload("cuda:0"));
    }

    [Theory]
    [InlineData("http://localhost:7801", "ws://localhost:7801")]
    [InlineData("https://example.com", "wss://example.com")]
    [InlineData("http://localhost:7801/", "ws://localhost:7801")]
    public void ToWebSocketUrl_MapsHttpAndHttps(string http, string expectedPrefix)
    {
        string ws = RemoteTextService.ToWebSocketUrl(http.TrimEnd('/'));
        Assert.Equal(expectedPrefix, ws);
    }

    [Fact]
    public void ToWebSocketUrl_RejectsANonHttpUrl()
    {
        Assert.Throws<ArgumentException>(() => RemoteTextService.ToWebSocketUrl("ftp://example.com"));
    }
}
