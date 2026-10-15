using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport;

public interface IChannel : IAsyncDisposable
{
    Guid ChannelId { get; }
    ChannelType Type { get; }
    bool IsOpen { get; }
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default);
}
