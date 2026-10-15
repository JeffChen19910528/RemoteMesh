using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// The in-memory session manager (which decides everything, under its locks) with every change written through to
/// PostgreSQL, so a restarted server knows its sessions again. The database is a durable copy, not a second arbiter:
/// two instances cannot share sessions this way (that needs a manager that decides in the database).
/// </summary>
public sealed class PersistentSessionManager : ISessionManager
{
    private const int Stripes = 64;

    private readonly InMemorySessionManager _inner;
    private readonly IDbContextFactory<RemoteMeshDbContext> _factory;
    // Saves of one session run one at a time and each reads the session when it gets its turn, so the last
    // write to the database is always the newest state, even when several changes race.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public PersistentSessionManager(InMemorySessionManager inner, IDbContextFactory<RemoteMeshDbContext> factory)
    {
        _inner = inner;
        _factory = factory;
    }

    public async Task<SessionInfo> CreateAsync(DeviceId requester, DeviceId target, CancellationToken ct = default)
    {
        var s = await _inner.CreateAsync(requester, target, ct);
        await SaveAsync(s.SessionId, ct);
        return s;
    }

    public Task<SessionInfo?> GetAsync(SessionId sessionId, CancellationToken ct = default) => _inner.GetAsync(sessionId, ct);

    public async Task UpdateStateAsync(SessionId sessionId, SessionState newState, CancellationToken ct = default)
    {
        await _inner.UpdateStateAsync(sessionId, newState, ct);
        await SaveAsync(sessionId, ct);
    }

    public async Task TerminateAsync(SessionId sessionId, string? reason = null, CancellationToken ct = default)
    {
        await _inner.TerminateAsync(sessionId, reason, ct);
        await SaveAsync(sessionId, ct);
    }

    public Task<IReadOnlyList<SessionInfo>> GetActiveSessionsAsync(DeviceId deviceId, CancellationToken ct = default) =>
        _inner.GetActiveSessionsAsync(deviceId, ct);

    public Task<IReadOnlyList<SessionInfo>> GetSessionsByStateAsync(SessionState state, CancellationToken ct = default) =>
        _inner.GetSessionsByStateAsync(state, ct);

    public Task<int> CountActiveAsync(CancellationToken ct = default) => _inner.CountActiveAsync(ct);

    public async Task<int> PurgeFinishedAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        var removed = await _inner.PurgeFinishedAsync(createdBefore, ct);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var finished = new[] { (int)SessionState.Disconnected, (int)SessionState.Failed, (int)SessionState.Expired };
        await db.Sessions.Where(s => finished.Contains(s.State) && s.CreatedAt < createdBefore).ExecuteDeleteAsync(ct);
        return removed;
    }

    public async Task<bool> TryTransitionAsync(SessionId sessionId, SessionState expected, SessionState next, CancellationToken ct = default)
    {
        var done = await _inner.TryTransitionAsync(sessionId, expected, next, ct);
        if (done) await SaveAsync(sessionId, ct);
        return done;
    }

    public async Task<bool> TryPauseAsync(SessionId sessionId, DateTimeOffset now, CancellationToken ct = default)
    {
        var done = await _inner.TryPauseAsync(sessionId, now, ct);
        if (done) await SaveAsync(sessionId, ct);
        return done;
    }

    public async Task<ResumeResult> TryResumeAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window, CancellationToken ct = default)
    {
        var result = await _inner.TryResumeAsync(sessionId, now, window, ct);
        if (result != ResumeResult.NotResumable) await SaveAsync(sessionId, ct); // resumed (new epoch) or expired
        return result;
    }

    public async Task<bool> TryExpireIfPausedAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window, CancellationToken ct = default)
    {
        var done = await _inner.TryExpireIfPausedAsync(sessionId, now, window, ct);
        if (done) await SaveAsync(sessionId, ct);
        return done;
    }

    public async Task SaveAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var gate = _gates[(sessionId.Value.GetHashCode() & int.MaxValue) % Stripes];
        await gate.WaitAsync(ct);
        try
        {
            var s = await _inner.GetAsync(sessionId, ct);
            await using var db = await _factory.CreateDbContextAsync(ct);
            if (s is null)
            {
                await db.Sessions.Where(r => r.SessionId == sessionId.Value).ExecuteDeleteAsync(ct);
                return;
            }
            await Upsert(db, s, ct);
        }
        finally { gate.Release(); }
    }

    internal static Task<int> Upsert(RemoteMeshDbContext db, SessionInfo s, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Sessions"" (""SessionId"", ""RequesterDeviceId"", ""TargetDeviceId"", ""RequesterShortId"", ""CreatedAt"",
                                    ""ExpiresAt"", ""State"", ""ConnectionMode"", ""PausedAt"", ""Permissions"", ""Epoch"")
            VALUES ({s.SessionId.Value}, {s.RequesterDeviceId.Value}, {s.TargetDeviceId.Value}, {s.RequesterShortId}, {s.CreatedAt},
                    {s.ExpiresAt}, {(int)s.State}, {(int)s.ConnectionMode}, {s.PausedAt}, {(int)s.Permissions.Granted}, {s.Epoch})
            ON CONFLICT (""SessionId"") DO UPDATE SET
                ""RequesterShortId"" = EXCLUDED.""RequesterShortId"", ""State"" = EXCLUDED.""State"",
                ""ConnectionMode"" = EXCLUDED.""ConnectionMode"", ""PausedAt"" = EXCLUDED.""PausedAt"",
                ""Permissions"" = EXCLUDED.""Permissions"", ""Epoch"" = EXCLUDED.""Epoch""", ct);
}
