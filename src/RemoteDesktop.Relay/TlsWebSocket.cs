using System.Buffers.Binary;
using System.Net.Security;
using System.Net.WebSockets;

namespace RemoteDesktop.Relay;

/// <summary>
/// A byte stream over the messages of a WebSocket: what is written goes out as binary messages, what comes in is read
/// as a continuous stream. TLS brings its own record framing, so it does not care where the messages begin and end.
/// </summary>
internal sealed class WebSocketByteStream(WebSocket socket) : Stream
{
    private byte[] _left = [];
    private int _leftPos;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private bool _ended;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    // TLS may write a closing alert from a synchronous Dispose; that must work (it only queues one small message).
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        if (destination.IsEmpty) return 0;
        while (_leftPos >= _left.Length)
        {
            if (_ended || socket.State is not (WebSocketState.Open or WebSocketState.CloseSent)) return 0;
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(_buffer.AsMemory(), ct); }
            catch (WebSocketException) { _ended = true; return 0; }
            if (result.MessageType == WebSocketMessageType.Close) { _ended = true; return 0; }
            _left = _buffer.AsMemory(0, result.Count).ToArray();
            _leftPos = 0;
        }
        var n = Math.Min(_left.Length - _leftPos, destination.Length);
        _left.AsMemory(_leftPos, n).CopyTo(destination);
        _leftPos += n;
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default) =>
        socket.SendAsync(source, WebSocketMessageType.Binary, endOfMessage: true, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        // The TLS wrapper owns closing; the socket is disposed by whoever owns the connection.
        base.Dispose(disposing);
    }
}

/// <summary>
/// A WebSocket whose messages travel inside a TLS stream that runs over another WebSocket. Each message is
/// <c>[length:4][bytes]</c> inside TLS, so message boundaries survive and the relay transport above it does not change.
/// </summary>
internal sealed class TlsWebSocket : WebSocket
{
    private readonly SslStream _tls;
    private readonly WebSocket _outer;
    private readonly IDisposable? _certificate;
    private readonly byte[] _header = new byte[4];
    private int _remaining = -1;     // bytes left of the message being read; -1 = between messages
    private WebSocketState _state = WebSocketState.Open;

    public TlsWebSocket(SslStream tls, WebSocket outer, IDisposable? certificate = null)
    {
        _tls = tls;
        _outer = outer;
        _certificate = certificate;
    }

    /// <summary>What the handshake settled on, for example <c>Tls13 TLS_AES_256_GCM_SHA384</c>.</summary>
    public string Description => $"{_tls.SslProtocol} {_tls.NegotiatedCipherSuite}";

    public override WebSocketCloseStatus? CloseStatus => _outer.CloseStatus;
    public override string? CloseStatusDescription => _outer.CloseStatusDescription;
    public override string? SubProtocol => _outer.SubProtocol;
    public override WebSocketState State =>
        _state != WebSocketState.Open ? _state : _outer.State;

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _outer.Abort();
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        _state = WebSocketState.Closed;
        try { await _tls.ShutdownAsync(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        await _outer.CloseAsync(closeStatus, statusDescription, ct);
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        try { await _tls.ShutdownAsync(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        await _outer.CloseOutputAsync(closeStatus, statusDescription, ct);
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _tls.Dispose();
        _outer.Dispose();
        _certificate?.Dispose(); // deletes the temporary key container on Windows
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        if (messageType != WebSocketMessageType.Binary) throw new ArgumentException("Only binary messages are carried.", nameof(messageType));
        if (!endOfMessage) throw new NotSupportedException("Messages are sent whole.");
        var framed = new byte[4 + buffer.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, buffer.Length);
        buffer.CopyTo(framed.AsMemory(4));
        try { await _tls.WriteAsync(framed, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _state = WebSocketState.Aborted;
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, ex);
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, ct).AsTask();

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            if (_remaining < 0)
            {
                if (!await ReadExactAsync(_header, ct)) return Closed();
                var length = BinaryPrimitives.ReadInt32BigEndian(_header);
                if (length < 0 || length > RelayTransport.MaxMessageBytes)
                {
                    _state = WebSocketState.Aborted;
                    _outer.Abort(); // a length nobody would send: the stream is not what it should be
                    throw new WebSocketException(WebSocketError.InvalidState, "Invalid message length.");
                }
                _remaining = length;
                if (length == 0) { _remaining = -1; return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Binary, true); }
            }

            var want = Math.Min(buffer.Length, _remaining);
            var read = await _tls.ReadAsync(buffer[..want], ct);
            if (read == 0) return Closed();
            _remaining -= read;
            var done = _remaining == 0;
            if (done) _remaining = -1;
            return new ValueWebSocketReceiveResult(read, WebSocketMessageType.Binary, done);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            // A record that fails its integrity check, or a stream that ends mid-record: the connection is over.
            _state = WebSocketState.Aborted;
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, ex);
        }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var r = await ReceiveAsync(buffer.AsMemory(), ct);
        return new WebSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage, _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null, null);
    }

    private ValueWebSocketReceiveResult Closed()
    {
        _state = WebSocketState.CloseReceived;
        return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
    }

    private async Task<bool> ReadExactAsync(byte[] destination, CancellationToken ct)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await _tls.ReadAsync(destination.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
