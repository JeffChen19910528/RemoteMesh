using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Relay;

public sealed class RelayChannel : IChannel
{
    private const int ReceiveBufferCapacity = 256;

    private readonly ChannelType _type;
    private readonly WebSocket _webSocket;
    private readonly SemaphoreSlim _sendLock;

    internal Channel<ReadOnlyMemory<byte>> ReceiveBuffer { get; } =
        Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ReceiveBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

    public RelayChannel(ChannelType type, WebSocket ws, SemaphoreSlim sendLock)
    {
        ChannelId = Guid.NewGuid();
        _type = type;
        _webSocket = ws;
        _sendLock = sendLock;
    }

    public Guid ChannelId { get; }
    public ChannelType Type => _type;
    public bool IsOpen => _webSocket.State == WebSocketState.Open;

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length + 1 > RelayTransport.MaxMessageBytes)
            throw new ArgumentException("Message exceeds the relay message size limit.", nameof(data));

        var buf = new byte[1 + data.Length];
        buf[0] = (byte)_type;
        data.CopyTo(buf.AsMemory(1));

        await _sendLock.WaitAsync(ct);
        try { await _webSocket.SendAsync(buf, WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in ReceiveBuffer.Reader.ReadAllAsync(ct))
            yield return item;
    }

    public ValueTask DisposeAsync()
    {
        ReceiveBuffer.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
