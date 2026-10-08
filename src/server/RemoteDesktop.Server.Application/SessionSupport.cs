using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// The checks and helpers every signaling operation repeats: who is calling, is the caller a participant, who is
/// the peer, token issuing and input validation. Kept in one place so a rule (for example the uniform
/// "Session not found." error) cannot drift between operations.
/// </summary>
public sealed class SessionSupport(
    IConnectionTracker connections,
    ISessionManager sessions,
    ISessionTokenService tokens,
    SessionSecurityOptions security)
{
    public const int MaxTokenChars = 128;

    public DeviceId RequireRegistered(ICallerContext caller)
    {
        var deviceId = connections.GetDeviceId(caller.ConnectionId);
        if (!deviceId.HasValue)
            throw new SignalingException("Device is not registered on this connection. Call BeginRegistration and RegisterDevice first.");
        return deviceId.Value;
    }

    public static SessionId ParseSessionId(string? value)
    {
        if (!SessionId.TryParse(value ?? string.Empty, out var id))
            throw new SignalingException("Invalid session ID.");
        return id;
    }

    public static DeviceId PeerOf(SessionInfo s, DeviceId me) =>
        s.RequesterDeviceId == me ? s.TargetDeviceId : s.RequesterDeviceId;

    /// <summary>Returns the session only if <paramref name="deviceId"/> is a participant; one error for both failures.</summary>
    public async Task<SessionInfo> GetParticipantSessionAsync(SessionId sessionId, DeviceId deviceId)
    {
        var s = await sessions.GetAsync(sessionId);
        if (s is null || (s.RequesterDeviceId != deviceId && s.TargetDeviceId != deviceId))
            throw new SignalingException("Session not found.");
        return s;
    }

    /// <summary>A single-use token bound to (session, device) plus what the holder was granted.</summary>
    public AuthResultDto IssueAuth(SessionInfo session, DeviceId forDevice) => new()
    {
        SessionId = session.SessionId.ToString(),
        SessionToken = tokens.Issue(session.SessionId, forDevice, security.RelayTokenLifetime),
        State = session.State.ToString(),
        GrantedPermissions = (long)session.Permissions.Granted
    };

    /// <summary>Per-target pending, per-device active and server-wide caps.</summary>
    public async Task EnforceSessionCapsAsync(DeviceId requesterId, DeviceId targetId, DateTimeOffset now)
    {
        var pending = (await sessions.GetActiveSessionsAsync(targetId))
            .Count(s => s.State == SessionState.Negotiating && now - s.CreatedAt <= security.ApprovalTimeout);
        if (pending >= security.MaxPendingRequestsPerTarget)
            throw new SignalingException("Device is not available.");

        var active = (await sessions.GetActiveSessionsAsync(requesterId)).Count;
        if (active >= security.MaxActiveSessionsPerDevice)
            throw new SignalingException("Too many active sessions.");

        if (await sessions.CountActiveAsync() >= security.MaxTotalActiveSessions)
            throw new SignalingException("Server is busy. Try again later.");
    }

    public static void RequireText(string? value, int maxLength, string error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new SignalingException(error);
    }

    public static byte[] DecodeBase64(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxBytes * 2) // base64 expands by 4/3
            throw new SignalingException("Invalid data.");
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw new SignalingException("Invalid data."); }
    }
}
