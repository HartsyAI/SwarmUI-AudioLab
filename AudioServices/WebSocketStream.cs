using System.IO;
using System.Net.WebSockets;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Presents a <see cref="WebSocket"/> as an ordered byte <see cref="Stream"/>.
///
/// <para>The wake protocol is a byte stream — a JSON line, then a counted payload — and the engine's frame
/// codec reads it that way. Wrapping the socket rather than reimplementing the protocol for WebSockets means
/// there is exactly one parser, so the TCP and tunnelled paths cannot drift apart.</para>
///
/// <para>Message boundaries are deliberately ignored on read: a sender may split a frame across several
/// WebSocket messages or pack several frames into one, and the codec handles both because it works in bytes.
/// Writes go out as one binary message each, which is what a microcontroller client will expect.</para></summary>
public sealed class WebSocketStream(WebSocket socket) : Stream
{
    private readonly WebSocket _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _pending = [];
    private int _pendingOffset;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (_pendingOffset >= _pending.Length)
        {
            byte[] rented = new byte[16 * 1024];
            WebSocketReceiveResult result;
            using MemoryStream accumulated = new();
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(rented), cancel).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return 0;
                }
                accumulated.Write(rented, 0, result.Count);
            }
            while (!result.EndOfMessage);

            _pending = accumulated.ToArray();
            _pendingOffset = 0;
            if (_pending.Length == 0)
            {
                return 0;
            }
        }

        int take = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
        _pendingOffset += take;
        return take;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
    {
        // A WebSocket throws if two sends overlap, and the wake service writes from both the connection loop
        // and the detection worker.
        await _writeLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancel).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        WriteAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancel) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writeLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
