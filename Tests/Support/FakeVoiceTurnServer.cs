using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.Tests.Support;

/// <summary>A minimal loopback stand-in for LLMAssistant's <c>LLMAssistantVoiceTurnWS</c> route, built from
/// <see cref="HttpListener"/> so <see cref="RemoteTextServiceTests"/> can drive <c>RemoteTextService</c> against a
/// real socket without SwarmUI core or LLMAssistant itself.</summary>
internal sealed class FakeVoiceTurnServer : IAsyncDisposable
{
    private readonly HttpListener _listener;

    private FakeVoiceTurnServer(HttpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Binds a free loopback port, retrying on collision.</summary>
    public static FakeVoiceTurnServer Start()
    {
        Exception last = null;
        for (int attempt = 0; attempt < 25; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return new FakeVoiceTurnServer(listener, port);
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                listener.Close();
            }
        }
        throw new InvalidOperationException("Could not bind a free loopback port for the fake server.", last);
    }

    /// <summary>Accepts one connection and reads its first (and, for this fake, only inbound) JSON frame -- the
    /// request <see cref="RemoteTextService"/> sends on connect.</summary>
    public async Task<(JObject Request, WebSocket Socket)> AcceptAsync(CancellationToken cancel)
    {
        HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(cancel).ConfigureAwait(false);
        HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        WebSocket socket = wsContext.WebSocket;
        JObject request = await ReceiveJsonAsync(socket, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The client closed before sending its request frame.");
        return (request, socket);
    }

    public static Task SendAsync(WebSocket socket, JObject frame, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame.ToString(Newtonsoft.Json.Formatting.None));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel);
    }

    /// <summary>Null when the socket closed (gracefully or by abort) before a full text frame arrived -- the
    /// cancellation test's way of observing that the client actually closed its end.</summary>
    public static async Task<JObject> ReceiveJsonAsync(WebSocket socket, CancellationToken cancel)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream accumulated = new();
        WebSocketReceiveResult result;
        try
        {
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
        }
        catch (WebSocketException)
        {
            return null; // the client aborted rather than closing gracefully.
        }
        string raw = Encoding.UTF8.GetString(accumulated.ToArray());
        return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
