using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Security;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// The in-memory token service with a durable copy, so the recovery tokens handed out before a restart still work
/// after it. Only the SHA-256 of a token is stored. A token that was used up or revoked is deleted before the call
/// returns, so a restart can never bring back something that was already spent.
/// </summary>
public sealed class PersistentSessionTokenService(SessionTokenService inner, IDbContextFactory<RemoteMeshDbContext> factory) : ISessionTokenService
{
    // The interface is synchronous (it sits in hot paths of the hub); each call here is one short statement.

    public string Issue(SessionId sessionId, DeviceId deviceId, TimeSpan? lifetime = null)
    {
        var (token, hash, expires) = inner.IssueWithHash(sessionId, deviceId, lifetime);
        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO ""SessionTokens"" (""TokenHash"", ""SessionId"", ""DeviceId"", ""Expires"")
            VALUES ({hash}, {sessionId.Value}, {deviceId.Value}, {expires}) ON CONFLICT DO NOTHING");
        return token;
    }

    public bool Validate(string token, SessionId sessionId, DeviceId deviceId) => inner.Validate(token, sessionId, deviceId);

    public bool TryConsume(string token, SessionId sessionId, DeviceId deviceId)
    {
        if (!inner.TryConsume(token, sessionId, deviceId)) return false;
        var hash = SessionTokenService.Hash(token);
        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($@"DELETE FROM ""SessionTokens"" WHERE ""TokenHash"" = {hash}");
        return true;
    }

    public void Revoke(SessionId sessionId)
    {
        inner.Revoke(sessionId);
        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($@"DELETE FROM ""SessionTokens"" WHERE ""SessionId"" = {sessionId.Value}");
    }
}
