using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
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

    /// <summary>The target's decision, with the rights it grants (least privilege: only what is passed is allowed).</summary>
    Task RespondToSessionAsync(SessionId sessionId, bool accept, Permission granted, CancellationToken ct = default);

    /// <summary>
    /// Requests a session and also returns the immediate authorization when the target trusts this device (no
    /// prompt needed); <c>Auth</c> is null when the target still has to approve.
    /// </summary>
    Task<(SessionInfo Session, AuthResultDto? Auth)> RequestSessionWithAuthAsync(DeviceId targetDeviceId, CancellationToken ct = default);

    /// <summary>Same, addressing the target by its short ID (nine digits) or its full ID, as typed.</summary>
    Task<(SessionInfo Session, AuthResultDto? Auth)> RequestSessionWithAuthAsync(string target, CancellationToken ct = default);

    /// <summary>
    /// This device's short ID. <paramref name="preferred"/> is the number it had before: it is kept if nobody else
    /// has it meanwhile, otherwise the server picks a new one.
    /// </summary>
    Task<string> ClaimShortIdAsync(string? preferred, CancellationToken ct = default);

    /// <summary>A one-time code to show the person who should be let in (valid briefly, single use).</summary>
    Task<string> GenerateOtpAsync(CancellationToken ct = default);

    /// <summary>Connects using the one-time code read from the target's screen; no approval prompt is needed.</summary>
    Task<AuthResultDto> RequestSessionWithOtpAsync(DeviceId targetDeviceId, string otp, CancellationToken ct = default);

    /// <summary>Same, addressing the target by its short ID or full ID, as typed.</summary>
    Task<AuthResultDto> RequestSessionWithOtpAsync(string target, string otp, CancellationToken ct = default);

    /// <summary>The other participant's pinned public key, for the end-to-end key agreement.</summary>
    Task<byte[]> GetPeerPublicKeyAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>The other participant's device ID and name with the key the server holds for it (to be pinned by the caller).</summary>
    Task<(DeviceId DeviceId, string Name, byte[] PublicKey)> GetPeerIdentityAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>
    /// Moves this device to <paramref name="newKeys"/>. The statement is signed by both keys; the server accepts it
    /// only while no session is active. Later registrations (after a reconnect) use the new key.
    /// </summary>
    Task RotateDeviceKeyAsync(DeviceKeyPair oldKeys, DeviceKeyPair newKeys, CancellationToken ct = default);

    /// <summary>The other participant's key history (each step signed by both keys), for checking a changed key.</summary>
    Task<IReadOnlyList<KeyRotation>> GetPeerKeyRotationsAsync(SessionId sessionId, DeviceId peer, CancellationToken ct = default);

    /// <summary>A fresh single-use relay token (for example after a relay reconnect).</summary>
    Task<AuthResultDto> RequestRelayTokenAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>Sends a transport negotiation message (direct-connection addresses) to the other participant of the session.</summary>
    Task SendTransportSignalAsync(SessionId sessionId, string payload, CancellationToken ct = default);

    /// <summary>A transport negotiation message from the other participant of a session.</summary>
    event Action<SessionId, string>? TransportSignalReceived;

    Task ReportConnectionModeAsync(SessionId sessionId, ConnectionMode mode, CancellationToken ct = default);
    Task CloseSessionAsync(SessionId sessionId, string? reason = null, CancellationToken ct = default);

    /// <summary>Lets a device connect without an approval prompt from now on, with at most the rights given here.</summary>
    Task AddTrustedDeviceAsync(DeviceId trustedDeviceId, string deviceName, Permission defaultPermissions = Permission.ViewOnly, CancellationToken ct = default);

    /// <summary>The device goes back to asking for approval like anyone else.</summary>
    Task RemoveTrustedDeviceAsync(DeviceId trustedDeviceId, CancellationToken ct = default);

    /// <summary>Devices this one lets connect without an approval prompt.</summary>
    Task<IReadOnlyList<TrustedDeviceDto>> GetTrustedDevicesAsync(CancellationToken ct = default);

    /// <summary>The requester was approved: carries its relay token and the granted permissions.</summary>
    event Action<AuthResultDto>? Approved;

    /// <summary>The target approved a request it received (its own relay token and the permissions it granted).</summary>
    event Action<AuthResultDto>? Authorized;

    /// <summary>A session was set up without a prompt (one-time code / trusted device): the target's token.</summary>
    event Action<AuthResultDto>? Established;

    /// <summary>The server reported a state change of a session this device takes part in (declined, expired, ...).</summary>
    event Action<SessionInfo>? SessionStateChanged;

    /// <summary>The server ended the session (peer closed, expired, rejected).</summary>
    event Action<SessionId, string>? SessionEnded;
    Task HeartbeatAsync(CancellationToken ct = default);

    // Streams for incoming events
    IAsyncEnumerable<SessionInfo> IncomingSessionRequestsAsync(CancellationToken ct = default);
    IAsyncEnumerable<SessionInfo> SessionStateUpdatesAsync(CancellationToken ct = default);
}
