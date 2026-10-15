using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport;

public interface ITransport : IAsyncDisposable
{
    SessionId SessionId { get; }
    ConnectionMode Mode { get; }
    bool IsConnected { get; }
    Task<IChannel> OpenChannelAsync(ChannelType type, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    event EventHandler<ConnectionMode>? ConnectionModeChanged;
}
