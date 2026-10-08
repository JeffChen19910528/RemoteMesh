using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport;

public sealed class LoopbackTransport : ITransport
{
    private readonly Dictionary<ChannelType, LoopbackChannel> _channels = new();

    public LoopbackTransport(SessionId sessionId, ConnectionMode mode = ConnectionMode.Direct)
    {
        SessionId = sessionId;
        Mode = mode;
    }

    public SessionId SessionId { get; }
    public ConnectionMode Mode { get; }
    public bool IsConnected { get; private set; } = true;

    public event EventHandler<ConnectionMode>? ConnectionModeChanged;

    public Task<IChannel> OpenChannelAsync(ChannelType type, CancellationToken ct = default)
    {
        var channel = new LoopbackChannel(type);
        _channels[type] = channel;
        return Task.FromResult<IChannel>(channel);
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        foreach (var ch in _channels.Values)
            ch.CloseInternal();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
