using RemoteDesktop.Core;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Relay;

public interface IRelayClient : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri relayUri, SessionId sessionId, string sessionToken, DeviceId deviceId,
        CancellationToken ct = default);
    Task<ITransport> GetTransportAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}
