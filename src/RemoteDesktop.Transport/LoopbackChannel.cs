using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport;

public sealed class LoopbackChannel : IChannel
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public LoopbackChannel(ChannelType type)
    {
        ChannelId = Guid.NewGuid();
        Type = type;
    }

    public Guid ChannelId { get; }
    public ChannelType Type { get; }
    public bool IsOpen { get; private set; } = true;

    // When set, SendAsync writes to this peer's incoming buffer (paired mode).
    // When null, SendAsync echo-writes to own incoming buffer (loopback mode).
    public ChannelWriter<ReadOnlyMemory<byte>>? PeerWriter { get; set; }

    public ChannelWriter<ReadOnlyMemory<byte>> Writer => _incoming.Writer;

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var writer = PeerWriter ?? _incoming.Writer;
        await writer.WriteAsync(data.ToArray(), ct);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _incoming.Reader.ReadAllAsync(ct))
            yield return item;
    }

    internal void CloseInternal()
    {
        IsOpen = false;
        _incoming.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        CloseInternal();
        return ValueTask.CompletedTask;
    }
}
