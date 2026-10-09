using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Security;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// What a starting server does with the sessions and tokens the previous run left behind. Every connection died with
/// that run, so a restored session is in the state it would have had if all its connections had just dropped:
/// <list type="bullet">
/// <item>Connected → Paused, with the recovery window starting now (the people come back through ResumeSession, with the
/// recovery token they already hold, and get a new key epoch).</item>
/// <item>Paused stays Paused, keeping its original start, so a long outage does not stretch the window.</item>
/// <item>Anything still being negotiated or approved → Failed: the prompt that was waiting for an answer is gone.</item>
/// <item>Finished or past its expiry: not restored.</item>
/// </list>
/// Only tokens that can still be used are restored.
/// </summary>
internal static class SessionRestore
{
    public static async Task RunAsync(
        IDbContextFactory<RemoteMeshDbContext> factory, InMemorySessionManager sessions, SessionTokenService tokens,
        SessionRecoveryOptions recovery, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var finished = new[] { (int)SessionState.Disconnected, (int)SessionState.Failed, (int)SessionState.Expired };

        var rows = await db.Sessions.Where(r => !finished.Contains(r.State)).ToListAsync(ct);
        foreach (var r in rows)
        {
            var session = new SessionInfo
            {
                SessionId = new SessionId(r.SessionId),
                RequesterDeviceId = new DeviceId(r.RequesterDeviceId),
                TargetDeviceId = new DeviceId(r.TargetDeviceId),
                RequesterShortId = r.RequesterShortId,
                CreatedAt = r.CreatedAt,
                ExpiresAt = r.ExpiresAt,
                State = (SessionState)r.State,
                ConnectionMode = (ConnectionMode)r.ConnectionMode,
                PausedAt = r.PausedAt,
                Permissions = new PermissionSet((Permission)r.Permissions),
                Epoch = r.Epoch
            };

            if (r.ExpiresAt <= now) session.State = SessionState.Expired;
            else switch (session.State)
            {
                case SessionState.Connected:
                    session.State = SessionState.Paused;
                    session.PausedAt = now;
                    break;
                case SessionState.Paused:
                    session.PausedAt ??= now;
                    break;
                default: // Created, Negotiating, Authenticating, Authorizing, EstablishingTransport, Disconnecting
                    session.State = session.State == SessionState.Disconnecting ? SessionState.Disconnected : SessionState.Failed;
                    break;
            }

            if (session.State is SessionState.Paused) sessions.Restore(session);
            if (session.State != (SessionState)r.State || session.PausedAt != r.PausedAt)
                await PersistentSessionManager.Upsert(db, session, ct);
        }

        // A token is only good for a session that came back; the rest, and anything expired, goes.
        var live = (await db.Sessions.Where(r => r.State == (int)SessionState.Paused).Select(r => r.SessionId).ToListAsync(ct)).ToHashSet();
        await db.SessionTokens.Where(t => t.Expires < now).ExecuteDeleteAsync(ct);
        foreach (var t in await db.SessionTokens.AsNoTracking().ToListAsync(ct))
        {
            if (live.Contains(t.SessionId))
                tokens.Restore(t.TokenHash, new SessionId(t.SessionId), new DeviceId(t.DeviceId), t.Expires);
            else
                await db.SessionTokens.Where(x => x.TokenHash == t.TokenHash).ExecuteDeleteAsync(ct);
        }
    }
}
