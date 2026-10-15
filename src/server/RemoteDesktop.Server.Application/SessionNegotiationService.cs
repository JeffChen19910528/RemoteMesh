using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// How a session comes into existence: an interactive request the target approves, a trusted-device fast path,
/// or a one-time code. Every path ends in EstablishingTransport with per-device single-use tokens, and a session
/// only becomes usable through the target's consent (approval, standing trust or the code it displayed).
/// </summary>
public sealed class SessionNegotiationService(
    SessionSupport support,
    IConnectionTracker connections,
    IDeviceRegistry devices,
    IShortIdDirectory shortIds,
    ISessionManager sessions,
    ITrustedDeviceStore trustedDevices,
    IOtpService otp,
    IRateLimiter rateLimiter,
    IAuditLog audit,
    IClientNotifier notifier,
    SessionSecurityOptions security,
    TimeProvider clock)
{
    private const long AllPermissionBits = (long)(Permission.FullControl | Permission.RemoteRestart | Permission.RemoteShutdown);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    // ── Interactive request / approval ───────────────────────────────────

    public async Task<SessionDto> RequestSessionAsync(ICallerContext caller, string targetDeviceIdStr)
    {
        var requesterId = support.RequireRegistered(caller);
        var targetId = await ResolveTargetAsync(requesterId, targetDeviceIdStr);
        await support.EnforceSessionCapsAsync(requesterId, targetId, clock.GetUtcNow());

        // Fast-path: requester is in target's trusted list — skip the approval dialog.
        if (await trustedDevices.IsTrustedAsync(targetId, requesterId))
        {
            var trustedEntry = await trustedDevices.GetAsync(targetId, requesterId);
            var permissions = trustedEntry?.DefaultPermissions ?? Permission.ViewOnly;

            var session = await sessions.CreateAsync(requesterId, targetId);
            session.Permissions.Grant(permissions);
            await sessions.UpdateStateAsync(session.SessionId, SessionState.EstablishingTransport);
            session.State = SessionState.EstablishingTransport;
            await sessions.SaveAsync(session.SessionId);

            var auth = support.IssueAuth(session, requesterId);
            await NotifyTargetEstablishedAsync(session, targetId);

            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = requesterId,
                TargetDeviceId = targetId,
                SessionId = session.SessionId,
                EventType = AuditEventType.RemoteSessionStarted,
                Success = true,
                Reason = "trusted-device"
            });

            var trustedDto = SessionDto.From(session);
            trustedDto.Auth = auth;
            return trustedDto;
        }

        // Standard path: ask the target.
        var pending = await sessions.CreateAsync(requesterId, targetId);
        await sessions.UpdateStateAsync(pending.SessionId, SessionState.Negotiating);
        pending.State = SessionState.Negotiating;
        pending.RequesterShortId = await shortIds.GetAsync(requesterId);
        await sessions.SaveAsync(pending.SessionId);

        var dto = SessionDto.From(pending);
        await notifier.NotifyAsync(targetId, "IncomingSessionRequest", dto);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = requesterId,
            TargetDeviceId = targetId,
            SessionId = pending.SessionId,
            EventType = AuditEventType.RemoteSessionRequested,
            Success = true
        });

        return dto;
    }

    public async Task RespondToSessionAsync(ICallerContext caller, string sessionIdStr, bool accept, long grantedPermissions)
    {
        var responderId = support.RequireRegistered(caller);
        var sessionId = SessionSupport.ParseSessionId(sessionIdStr);
        if (grantedPermissions < 0 || (grantedPermissions & ~AllPermissionBits) != 0)
            throw new SignalingException("Invalid permissions.");

        var session = await sessions.GetAsync(sessionId);
        // Unknown session and "not your session" look identical.
        if (session is null || session.TargetDeviceId != responderId)
            throw new SignalingException("Session not found.");

        // A request nobody answered in time is dead; answering it later must not grant anything.
        if (clock.GetUtcNow() - session.CreatedAt > security.ApprovalTimeout)
        {
            await sessions.TryTransitionAsync(sessionId, SessionState.Negotiating, SessionState.Failed);
            throw new SignalingException("Request expired.");
        }

        if (!accept)
        {
            if (!await sessions.TryTransitionAsync(sessionId, SessionState.Negotiating, SessionState.Failed))
                throw new SignalingException("Session is no longer pending.");
            session.State = SessionState.Failed;

            await notifier.NotifyAsync(session.RequesterDeviceId, "SessionStateChanged", SessionDto.From(session));
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = responderId,
                TargetDeviceId = session.RequesterDeviceId,
                SessionId = sessionId,
                EventType = AuditEventType.RemoteSessionRejected,
                Success = true
            });
            return;
        }

        // Only the first accept can win; a closed, failed or already-answered session cannot be revived.
        if (!await sessions.TryTransitionAsync(sessionId, SessionState.Negotiating, SessionState.Authorizing))
            throw new SignalingException("Session is no longer pending.");
        session.State = SessionState.Authorizing;
        await notifier.NotifyAsync(session.RequesterDeviceId, "SessionStateChanged", SessionDto.From(session));

        session.Permissions.Grant((Permission)grantedPermissions);
        if (!await sessions.TryTransitionAsync(sessionId, SessionState.Authorizing, SessionState.EstablishingTransport))
            throw new SignalingException("Session is no longer pending.");
        session.State = SessionState.EstablishingTransport;
        await sessions.SaveAsync(sessionId);

        // Each side gets a token bound to ITS OWN device; the relay pairs them by device role.
        var requesterAuth = support.IssueAuth(session, session.RequesterDeviceId);
        var targetAuth = support.IssueAuth(session, session.TargetDeviceId);

        await notifier.NotifyAsync(session.RequesterDeviceId, "AuthSuccess", requesterAuth);
        await notifier.NotifyAsync(responderId, "SessionAuthorized", targetAuth);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = responderId,
            TargetDeviceId = session.RequesterDeviceId,
            SessionId = sessionId,
            EventType = AuditEventType.RemoteSessionApproved,
            Success = true
        });
        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = responderId,
            TargetDeviceId = session.RequesterDeviceId,
            SessionId = sessionId,
            EventType = AuditEventType.PermissionGranted,
            Success = true,
            Reason = session.Permissions.Granted.ToString() // which rights, so the trail shows what was actually allowed
        });
    }

    // ── One-time code ────────────────────────────────────────────────────

    /// <summary>The target asks for a one-time code to display to the person it wants to let in.</summary>
    public string GenerateOtp(ICallerContext caller)
    {
        var deviceId = support.RequireRegistered(caller);
        if (!rateLimiter.TryConsume($"otpgen:{deviceId}", 10, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");
        return otp.Generate(deviceId);
    }

    /// <summary>The requester submits the code it read from the target's screen.</summary>
    public async Task<AuthResultDto> RequestSessionWithOtpAsync(ICallerContext caller, string targetDeviceIdStr, string code)
    {
        var requesterId = support.RequireRegistered(caller);

        if (await ParseTargetAsync(targetDeviceIdStr) is not { } targetId || targetId == requesterId)
            throw new SignalingException("Authentication failed.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16 || !code.Trim().All(char.IsAsciiDigit))
            throw new SignalingException("Authentication failed.");

        // Per-requester limit, plus a per-IP limit so spinning up new device IDs does not reset the budget.
        var ipKey = $"otpip:{caller.IpAddress}:{targetId}";
        var rateLimitKey = $"otp:{targetId}:{requesterId}";
        if (!rateLimiter.TryConsume(rateLimitKey, maxAttempts: 5, window: TimeSpan.FromMinutes(5))
            || !rateLimiter.TryConsume(ipKey, maxAttempts: 10, window: TimeSpan.FromMinutes(5)))
        {
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = requesterId,
                TargetDeviceId = targetId,
                EventType = AuditEventType.RateLimitTriggered,
                Success = false,
                Reason = "OTP rate limit exceeded"
            });
            throw new SignalingException("Too many authentication attempts. Try again later.");
        }

        var result = otp.Verify(targetId, code);
        if (result != OtpVerifyResult.Valid)
        {
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = requesterId,
                TargetDeviceId = targetId,
                EventType = AuditEventType.AuthenticationFailure,
                Success = false,
                Reason = result.ToString() // detail stays in the audit log, never sent to the caller
            });
            throw new SignalingException("Authentication failed.");
        }

        if (await devices.FindAsync(targetId) is null)
            throw new SignalingException("Authentication failed.");
        await support.EnforceSessionCapsAsync(requesterId, targetId, clock.GetUtcNow());

        var session = await sessions.CreateAsync(requesterId, targetId);
        session.Permissions.Grant(Permission.ViewOnly);
        await sessions.UpdateStateAsync(session.SessionId, SessionState.EstablishingTransport);
        session.State = SessionState.EstablishingTransport;
        await sessions.SaveAsync(session.SessionId);

        var authResult = support.IssueAuth(session, requesterId);
        rateLimiter.Reset(rateLimitKey);

        await NotifyTargetEstablishedAsync(session, targetId);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = requesterId,
            TargetDeviceId = targetId,
            SessionId = session.SessionId,
            EventType = AuditEventType.AuthenticationSuccess,
            Success = true
        });
        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = requesterId,
            TargetDeviceId = targetId,
            SessionId = session.SessionId,
            EventType = AuditEventType.RemoteSessionStarted,
            Success = true,
            Reason = "otp"
        });

        return authResult;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the target of a session request. Unknown and unavailable devices produce the same error, unknown
    /// lookups are budgeted (device-ID enumeration), and requests are rate limited per requester.
    /// </summary>
    private async Task<DeviceId> ResolveTargetAsync(DeviceId requesterId, string targetDeviceIdStr)
    {
        if (!rateLimiter.TryConsume($"reqsess:{requesterId}", security.SessionRequestsPerMinute, TimeSpan.FromMinutes(1)))
        {
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = requesterId,
                EventType = AuditEventType.RateLimitTriggered,
                Success = false,
                Reason = "session request rate limit exceeded"
            });
            throw new SignalingException("Too many requests. Try again later.");
        }

        var lookupKey = $"lookupfail:{requesterId}";
        if (rateLimiter.IsLockedOut(lookupKey, security.UnknownDeviceLookupsPer10Min, TenMinutes))
            throw new SignalingException("Too many failed lookups. Try again later.");

        // The target may be written as a short ID (nine digits) or the full ID. Guessing short IDs is the cheap
        // attack, so a lookup that finds nothing is counted and audited exactly like an unknown full ID.
        var isShort = ShortId.TryNormalize(targetDeviceIdStr, out _);
        if (await ParseTargetAsync(targetDeviceIdStr) is not { } targetId || targetId == requesterId)
        {
            rateLimiter.TryConsume(lookupKey, security.UnknownDeviceLookupsPer10Min, TenMinutes);
            if (isShort)
                await audit.WriteAsync(new AuditEvent
                {
                    ActorDeviceId = requesterId,
                    EventType = AuditEventType.AuthenticationFailure,
                    Success = false,
                    Reason = "unknown-device-lookup"
                });
            throw new SignalingException("Device is not available.");
        }

        var known = await devices.FindAsync(targetId) is not null;
        if (!known)
        {
            rateLimiter.TryConsume(lookupKey, security.UnknownDeviceLookupsPer10Min, TenMinutes);
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = requesterId,
                EventType = AuditEventType.AuthenticationFailure,
                Success = false,
                Reason = "unknown-device-lookup"
            });
        }
        if (!known || connections.GetConnectionId(targetId) is null)
            throw new SignalingException("Device is not available."); // same message: unknown vs offline is not observable
        return targetId;
    }

    /// <summary>A short ID is looked up; a full ID is parsed. Null when it is neither or nobody has that short ID.</summary>
    private async Task<DeviceId?> ParseTargetAsync(string? text)
    {
        if (ShortId.TryNormalize(text, out var digits)) return await shortIds.ResolveAsync(digits);
        return DeviceId.TryParse(text ?? string.Empty, out var id) ? id : null;
    }

    private async Task NotifyTargetEstablishedAsync(SessionInfo session, DeviceId targetId)
    {
        // Only mint a token if someone can receive it; an undelivered token would just sit in the table.
        if (connections.GetConnectionId(targetId) is null) return;
        await notifier.NotifyAsync(targetId, "SessionEstablished", support.IssueAuth(session, targetId));
    }
}
