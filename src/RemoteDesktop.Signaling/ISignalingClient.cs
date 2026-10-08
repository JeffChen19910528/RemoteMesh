using RemoteDesktop.Core;
using RemoteDesktop.Security;

namespace RemoteDesktop.Signaling;

public interface ISignalingClient : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri signalingUri, DeviceId deviceId, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    /// <summary>Registers the device by proving possession of its private key (challenge-response); the key never leaves the signer.</summary>
    Task RegisterDeviceAsync(DeviceInfo device, IDeviceSigner signer, CancellationToken ct = default);
    Task<SessionInfo> RequestSessionAsync(DeviceId targetDeviceId, CancellationToken ct = default);
    Task RespondToSessionAsync(SessionId sessionId, bool accept, CancellationToken ct = default);
    Task HeartbeatAsync(CancellationToken ct = default);

    // Streams for incoming events
    IAsyncEnumerable<SessionInfo> IncomingSessionRequestsAsync(CancellationToken ct = default);
    IAsyncEnumerable<SessionInfo> SessionStateUpdatesAsync(CancellationToken ct = default);
}
