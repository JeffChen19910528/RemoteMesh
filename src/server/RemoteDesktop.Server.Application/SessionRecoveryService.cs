using Microsoft.Extensions.Hosting;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// Housekeeping that stops sessions outliving their authorization:
/// Paused sessions past the recovery window and unanswered requests past the approval timeout are ended, their
/// tokens revoked and any relay dropped. Without it a dropped peer or an ignored request would leave a ghost
/// session whose token stays valid.
/// </summary>
public sealed class SessionRecoveryService(
    ISessionManager sessions,
    ISessionTokenService tokens,
    IAuditLog audit,
    IClientNotifier notifier,
    IRelayService relay,
    SessionRecoveryOptions options,
    SessionSecurityOptions security,
    TimeProvider clock)
{
    public async Task<int> ExpirePausedSessionsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expired = 0;
        foreach (var s in await sessions.GetSessionsByStateAsync(SessionState.Paused, ct))
        {
            // The window check happens atomically inside the manager, so a concurrent resume always wins cleanly.
            if (!await sessions.TryExpireIfPausedAsync(s.SessionId, now, options.Window, ct)) continue;
            expired++;
            await EndAsync(s, "recovery-window-expired", AuditEventType.RemoteSessionExpired, ct);
        }
        return expired;
    }

    /// <summary>Fails requests the target never answered within the approval timeout.</summary>
    public async Task<int> ExpireUnansweredRequestsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expired = 0;
        foreach (var s in await sessions.GetSessionsByStateAsync(SessionState.Negotiating, ct))
        {
            if (now - s.CreatedAt <= security.ApprovalTimeout) continue;
            if (!await sessions.TryTransitionAsync(s.SessionId, SessionState.Negotiating, SessionState.Failed, ct)) continue;
            expired++;
            await EndAsync(s, "approval-timeout", AuditEventType.RemoteSessionRejected, ct);
        }
        return expired;
    }

    /// <summary>
    /// Session Expiration: a session past its expiry is ended whatever state it is in (connected sessions included),
    /// its tokens are revoked and its relay dropped. Without this a connected session would outlive its authorization.
    /// </summary>
    public async Task<int> ExpireOverdueSessionsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expired = 0;
        foreach (var state in new[] { SessionState.Authorizing, SessionState.EstablishingTransport, SessionState.Connected, SessionState.Paused })
            foreach (var s in await sessions.GetSessionsByStateAsync(state, ct))
            {
                if (s.ExpiresAt > now) continue;
                if (!await sessions.TryTransitionAsync(s.SessionId, state, SessionState.Expired, ct)) continue;
                expired++;
                await EndAsync(s, "session-expired", AuditEventType.RemoteSessionExpired, ct);
            }
        return expired;
    }

    /// <summary>Drops finished sessions from memory once they are older than the retention period.</summary>
    public Task<int> PurgeFinishedSessionsAsync(CancellationToken ct = default) =>
        sessions.PurgeFinishedAsync(clock.GetUtcNow() - security.FinishedSessionRetention, ct);

    private async Task EndAsync(SessionInfo s, string reason, AuditEventType type, CancellationToken ct)
    {
        tokens.Revoke(s.SessionId);
        relay.CloseSession(s.SessionId);

        foreach (var device in new[] { s.RequesterDeviceId, s.TargetDeviceId })
        {
            await notifier.NotifyAsync(device, "SessionClosed", new object?[] { s.SessionId.ToString(), reason }, ct);
        }

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = s.RequesterDeviceId,
            TargetDeviceId = s.TargetDeviceId,
            SessionId = s.SessionId,
            EventType = type,
            Success = false,
            Reason = reason
        }, ct);
    }
}

public sealed class SessionRecoveryReaper(SessionRecoveryService service, SessionRecoveryOptions options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await service.ExpirePausedSessionsAsync(stoppingToken);
                    await service.ExpireUnansweredRequestsAsync(stoppingToken);
                    await service.ExpireOverdueSessionsAsync(stoppingToken);
                    await service.PurgeFinishedSessionsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep sweeping; one bad pass must not stop cleanup.
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
