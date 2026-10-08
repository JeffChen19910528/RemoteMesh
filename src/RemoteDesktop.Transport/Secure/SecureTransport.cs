using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;

namespace RemoteDesktop.Transport.Secure;

/// <summary>
/// Wraps any transport (direct, relay) so every channel it opens is end-to-end encrypted with the session key.
/// Session code must open channels through this wrapper: plain channels are never handed to the application.
/// </summary>
public sealed class SecureTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly ISessionCrypto _crypto;
    private readonly bool _isInitiator;

    public SecureTransport(ITransport inner, ISessionCrypto crypto, bool isInitiator)
    {
        _inner = inner;
        _crypto = crypto;
        _isInitiator = isInitiator;
    }

    public SessionId SessionId => _inner.SessionId;
    public ConnectionMode Mode => _inner.Mode;
    public bool IsConnected => _inner.IsConnected;

    public event EventHandler<ConnectionMode>? ConnectionModeChanged
    {
        add => _inner.ConnectionModeChanged += value;
        remove => _inner.ConnectionModeChanged -= value;
    }

    public async Task<IChannel> OpenChannelAsync(ChannelType type, CancellationToken ct = default) =>
        new SecureChannel(await _inner.OpenChannelAsync(type, ct), _crypto, SessionId, _isInitiator);

    public Task CloseAsync(CancellationToken ct = default) => _inner.CloseAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await _crypto.DisposeSessionKeyAsync(SessionId);
    }
}
