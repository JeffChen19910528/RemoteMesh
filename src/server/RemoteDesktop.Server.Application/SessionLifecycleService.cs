using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// Everything that happens to a session after it was authorized: transport setup, going live, pausing when a peer
/// drops, resuming, and closing. State changes are compare-and-set so racing calls cannot both win and a finished
/// session cannot be revived.
/// </summary>
public sealed class SessionLifecycleService(
    SessionSupport support,
    IConnectionTracker connections,
    IDeviceRegistry devices,
    ISessionManager sessions,
    ISessionTokenService tokens,
    IRelayService relay,
    IRateLimiter rateLimiter,
    IAuditLog audit,
    IClientNotifier notifier,
    SessionRecoveryOptions recovery,
    SessionSecurityOptions security,
    TimeProvider clock)
{
    // ── Transport ────────────────────────────────────────────────────────

    public async Task SendIceCandidateAsync(ICallerContext caller, string sessionIdStr, string candidateJson)
    {
        var senderId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (string.IsNullOrEmpty(candidateJson) || candidateJson.Length > security.MaxCandidateJsonBytes)
            throw new SignalingException("Invalid candidate.");

        var session = await support.GetParticipantSessionAsync(sessionId, senderId);

        // Paused: a dropped connection being rebuilt negotiates its transport again.
        if (session.State is not (SessionState.EstablishingTransport or SessionState.Connected or SessionState.Paused))
            throw new SignalingException("ICE candidates can only be exchanged while establishing transport.");
        if (!rateLimiter.TryConsume($"ice:{sessionId}:{senderId}", 200, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");

        await notifier.NotifyAsync(SessionSupport.PeerOf(session, senderId), "IceCandidateReceived", sessionIdStr, candidateJson);
    }

    public async Task ReportConnectionModeAsync(ICallerContext caller, string sessionIdStr, string mode)
    {
        var reporterId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);

        if (!Enum.TryParse<ConnectionMode>(mode, ignoreCase: true, out var connectionMode)
            || connectionMode == ConnectionMode.Unknown || !Enum.IsDefined(connectionMode))
            throw new SignalingException("Unknown connection mode.");

        var session = await support.GetParticipantSessionAsync(sessionId, reporterId);

        // Only an approved session that is establishing its transport may become Connected. Without this a
        // requester could skip the target's approval simply by reporting "Connected".
        var moved = await sessions.TryTransitionAsync(sessionId, SessionState.EstablishingTransport, SessionState.Connected);
        if (!moved && session.State != SessionState.Connected)
            throw new SignalingException("Session is not ready for transport.");

        session.State = SessionState.Connected;
        session.ConnectionMode = connectionMode;
        await sessions.SaveAsync(sessionId);
        if (!moved) return; // repeat report on an already-connected session: nothing else to do

        await notifier.NotifyAsync(SessionSupport.PeerOf(session, reporterId), "SessionStateChanged", SessionDto.From(session));

        // Each participant gets a recovery token (single-use, valid for the rest of the session) so it can
        // ResumeSession after a drop.
        var remaining = session.ExpiresAt - clock.GetUtcNow();
        foreach (var participant in new[] { session.RequesterDeviceId, session.TargetDeviceId })
        {
            if (connections.GetConnectionId(participant) is not null && remaining > TimeSpan.Zero)
                await notifier.NotifyAsync(participant, "RecoveryTokenIssued", sessionIdStr,
                    tokens.Issue(sessionId, participant, remaining));
        }

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = reporterId,
            SessionId = sessionId,
            EventType = AuditEventType.RemoteSessionStarted,
            Success = true,
            Reason = $"transport:{connectionMode}"
        });
    }

    /// <summary>Fresh single-use relay token for a participant of an approved session (e.g. relay reconnect).</summary>
    public async Task<AuthResultDto> RequestRelayTokenAsync(ICallerContext caller, string sessionIdStr)
    {
        var deviceId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (!rateLimiter.TryConsume($"relaytok:{sessionId}:{deviceId}", security.RelayTokenRequestsPerMinute, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");

        var session = await support.GetParticipantSessionAsync(sessionId, deviceId);
        if (session.State is not (SessionState.EstablishingTransport or SessionState.Connected))
            throw new SignalingException("Session is not ready for transport.");
        return support.IssueAuth(session, deviceId);
    }

    /// <summary>
    /// The pinned public key of the other participant, needed to derive the end-to-end session key. Only a participant
    /// of an approved session may ask, so this is not a way to look up arbitrary devices' keys.
    /// </summary>
    public async Task<string> GetPeerPublicKeyAsync(ICallerContext caller, string sessionIdStr) =>
        (await GetPeerIdentityAsync(caller, sessionIdStr)).PublicKeyBase64;

    /// <summary>
    /// Who the other participant is (device ID, name) and the key the server pinned for it. The client pins what it
    /// sees here the first time and refuses the session if the key ever differs, so the server cannot swap it later.
    /// </summary>
    public async Task<DeviceInfoDto> GetPeerIdentityAsync(ICallerContext caller, string sessionIdStr)
    {
        var deviceId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (!rateLimiter.TryConsume($"peerkey:{sessionId}:{deviceId}", security.RelayTokenRequestsPerMinute, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");

        var session = await support.GetParticipantSessionAsync(sessionId, deviceId);
        if (session.State is not (SessionState.EstablishingTransport or SessionState.Connected or SessionState.Paused))
            throw new SignalingException("Session is not ready for transport.");

        var peerId = SessionSupport.PeerOf(session, deviceId);
        var key = await devices.GetPublicKeyAsync(peerId);
        var info = await devices.FindAsync(peerId);
        if (key is null || info is null) throw new SignalingException("Session not found.");
        return DeviceInfoDto.From(info, key);
    }

    /// <summary>
    /// The other participant's key history, oldest first, for a client whose pinned key for that device no longer
    /// matches: each step is signed by both keys, so the client can walk from the key it pinned to the current one
    /// and trust the result without trusting this server. Same access rules as <see cref="GetPeerIdentityAsync"/>.
    /// </summary>
    public async Task<List<KeyRotationDto>> GetPeerKeyRotationsAsync(ICallerContext caller, string sessionIdStr)
    {
        var deviceId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (!rateLimiter.TryConsume($"peerkey:{sessionId}:{deviceId}", security.RelayTokenRequestsPerMinute, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");

        var session = await support.GetParticipantSessionAsync(sessionId, deviceId);
        if (session.State is not (SessionState.EstablishingTransport or SessionState.Connected or SessionState.Paused))
            throw new SignalingException("Session is not ready for transport.");

        var rotations = await devices.GetRotationsAsync(SessionSupport.PeerOf(session, deviceId));
        return rotations.Select(KeyRotationDto.From).ToList();
    }

    // ── Closing ──────────────────────────────────────────────────────────

    public async Task CloseSessionAsync(ICallerContext caller, string sessionIdStr, string? reason)
    {
        var closerId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (reason is { Length: > 256 }) reason = reason[..256];

        // Participant check BEFORE any side effect: outsiders must not be able to revoke or terminate sessions.
        var session = await support.GetParticipantSessionAsync(sessionId, closerId);

        tokens.Revoke(sessionId);
        relay.CloseSession(sessionId); // a closed session must not keep a live relay
        await sessions.TerminateAsync(sessionId, reason);

        await notifier.NotifyAsync(SessionSupport.PeerOf(session, closerId), "SessionClosed", sessionIdStr, reason ?? string.Empty);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = closerId,
            SessionId = sessionId,
            EventType = AuditEventType.RemoteSessionTerminated,
            Success = true,
            Reason = reason
        });
    }

    // ── Recovery ─────────────────────────────────────────────────────────

    /// <summary>
    /// After a drop the device reconnects, proves its identity, then resumes with its single-use recovery token.
    /// The reply carries a fresh recovery token for the next drop and the new key epoch.
    /// </summary>
    public async Task<SessionDto> ResumeSessionAsync(ICallerContext caller, string sessionIdStr, string token)
    {
        var deviceId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);

        if (!rateLimiter.TryConsume($"resume:{sessionId}:{deviceId}", recovery.MaxResumeAttemptsPerMinute, TimeSpan.FromMinutes(1)))
        {
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = deviceId,
                SessionId = sessionId,
                EventType = AuditEventType.RateLimitTriggered,
                Success = false,
                Reason = "resume rate limit exceeded"
            });
            throw new SignalingException("Too many resume attempts. Try again later.");
        }

        // One generic error for unknown session, non-participant and bad token: no state oracle.
        var session = await sessions.GetAsync(sessionId);
        var authorized = session is not null
            && (session.RequesterDeviceId == deviceId || session.TargetDeviceId == deviceId)
            && !string.IsNullOrEmpty(token) && token.Length <= SessionSupport.MaxTokenChars
            && tokens.TryConsume(token, sessionId, deviceId);
        if (!authorized)
        {
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = deviceId,
                SessionId = sessionId,
                EventType = AuditEventType.AuthenticationFailure,
                Success = false,
                Reason = "resume-denied"
            });
            throw new SignalingException("Cannot resume this session.");
        }

        var result = await sessions.TryResumeAsync(sessionId, clock.GetUtcNow(), recovery.Window);
        if (result == ResumeResult.WindowExpired)
        {
            tokens.Revoke(sessionId);
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = deviceId,
                SessionId = sessionId,
                EventType = AuditEventType.RemoteSessionExpired,
                Success = false,
                Reason = "recovery-window-expired"
            });
            throw new SignalingException("Recovery window expired.");
        }
        if (result == ResumeResult.NotResumable)
            throw new SignalingException("Cannot resume this session.");

        if (result == ResumeResult.Resumed)
        {
            var peerId = SessionSupport.PeerOf(session!, deviceId);
            await notifier.NotifyAsync(peerId, "SessionStateChanged", SessionDto.From(session!));
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = deviceId,
                TargetDeviceId = peerId,
                SessionId = sessionId,
                EventType = AuditEventType.RemoteSessionResumed,
                Success = true
            });
        }

        var dto = SessionDto.From(session!);
        var remaining = session!.ExpiresAt - clock.GetUtcNow();
        dto.Auth = new AuthResultDto
        {
            SessionId = sessionIdStr,
            SessionToken = tokens.Issue(sessionId, deviceId, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1)),
            State = session.State.ToString(),
            GrantedPermissions = (long)session.Permissions.Granted
        };
        return dto;
    }

    private async Task WithdrawPendingRequestAsync(SessionInfo s)
    {
        if (!await sessions.TryTransitionAsync(s.SessionId, SessionState.Negotiating, SessionState.Failed)) return; // answered meanwhile
        tokens.Revoke(s.SessionId);
        await notifier.NotifyAsync(s.TargetDeviceId, "SessionClosed", s.SessionId.ToString(), "requester-disconnected");
        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = s.RequesterDeviceId,
            TargetDeviceId = s.TargetDeviceId,
            SessionId = s.SessionId,
            EventType = AuditEventType.RemoteSessionRejected,
            Success = false,
            Reason = "requester-disconnected"
        });
    }

    /// <summary>
    /// A dropped peer pauses its Connected sessions instead of leaving them live forever; the reaper expires them
    /// if nobody resumes within the recovery window.
    /// </summary>
    public async Task PauseConnectedSessionsAsync(DeviceId deviceId)
    {
        foreach (var s in await sessions.GetActiveSessionsAsync(deviceId))
        {
            // A request still waiting for the target's answer dies with the requester: otherwise the target is
            // left looking at a prompt for somebody who is already gone.
            if (s.State == SessionState.Negotiating && s.RequesterDeviceId == deviceId)
            {
                await WithdrawPendingRequestAsync(s);
                continue;
            }
            if (s.State != SessionState.Connected) continue;
            if (!await sessions.TryPauseAsync(s.SessionId, clock.GetUtcNow())) continue;

            var peerId = SessionSupport.PeerOf(s, deviceId);
            await notifier.NotifyAsync(peerId, "SessionStateChanged", SessionDto.From(s));
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = deviceId,
                TargetDeviceId = peerId,
                SessionId = s.SessionId,
                EventType = AuditEventType.RemoteSessionPaused,
                Success = true,
                Reason = "peer-disconnected"
            });
        }
    }
}
