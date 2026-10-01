using System.Net.WebSockets;

namespace Hartsy.Extensions.AudioLab.Tests.Support;

/// <summary>A scripted <see cref="WebSocket"/> for testing <c>VoiceSessionEndpoints.ReceiveInboundAsync</c>
/// directly, without a real browser connection. Each scripted message is handed back across as many
/// <see cref="ReceiveAsync"/> calls as the caller's own buffer size forces -- a payload larger than that buffer
/// naturally exercises the multi-continuation-frame path with no extra knob needed.</summary>
internal sealed class FakeInboundWebSocket : WebSocket
{
    public sealed record ScriptedMessage(WebSocketMessageType Type, byte[] Payload);

    private readonly Queue<ScriptedMessage> _script;
    private byte[] _currentPayload;
    private int _currentOffset;
    private WebSocketMessageType _currentType;
    private WebSocketState _state = WebSocketState.Open;

    public WebSocketCloseStatus? ClosedWith { get; private set; }

    public string ClosedDescription { get; private set; }

    public FakeInboundWebSocket(IEnumerable<ScriptedMessage> script)
    {
        _script = new Queue<ScriptedMessage>(script);
    }

    public override WebSocketCloseStatus? CloseStatus => ClosedWith;

    public override string CloseStatusDescription => ClosedDescription;

    public override WebSocketState State => _state;

    public override string SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        ClosedDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_currentPayload is null)
        {
            if (_script.Count == 0)
            {
                _state = WebSocketState.Closed;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            ScriptedMessage next = _script.Dequeue();
            _currentPayload = next.Payload;
            _currentOffset = 0;
            _currentType = next.Type;
        }
        int remaining = _currentPayload.Length - _currentOffset;
        int take = Math.Min(remaining, buffer.Count);
        Array.Copy(_currentPayload, _currentOffset, buffer.Array, buffer.Offset, take);
        _currentOffset += take;
        bool end = _currentOffset >= _currentPayload.Length;
        WebSocketMessageType type = _currentType;
        if (end)
        {
            _currentPayload = null;
        }
        return Task.FromResult(new WebSocketReceiveResult(take, type, end));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
