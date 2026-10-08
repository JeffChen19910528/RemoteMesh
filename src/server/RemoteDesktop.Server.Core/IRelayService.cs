using System.Net.WebSockets;
using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public enum RelayOutcome
{
    Completed,
    Unauthorized,   // bad/used/expired token, not a participant, wrong session state, duplicate role
    PeerTimeout,    // the other participant never showed up
    Closed,         // session was closed/expired while relaying
    Busy            // the relay is at capacity (Maximum Sessions); nothing was consumed, retry later
}

public interface IRelayService
{
    /// <summary>
    /// Pairs the session's requester and target and forwards their (end-to-end encrypted) frames without
    /// interpreting them. Unauthorized callers all get the same close, whatever the reason.
    /// </summary>
    Task<RelayOutcome> HandleConnectionAsync(WebSocket socket, SessionId sessionId, DeviceId deviceId,
        string sessionToken, CancellationToken ct = default);

    /// <summary>Drops any live relay for the session (session closed, expired or revoked).</summary>
    void CloseSession(SessionId sessionId);
}
