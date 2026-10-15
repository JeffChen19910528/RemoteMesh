using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Persistence;

public sealed class PostgresAuditLog(IDbContextFactory<RemoteMeshDbContext> factory) : IAuditLog
{
    /// <summary>One query never returns more than this many entries.</summary>
    public const int MaxQueryResults = 10_000;

    public async Task WriteAsync(AuditEvent e, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(new AuditLogRecord
        {
            EventId = e.EventId,
            Timestamp = e.Timestamp,
            ActorDeviceId = e.ActorDeviceId.Value,
            TargetDeviceId = e.TargetDeviceId?.Value,
            SessionId = e.SessionId?.Value,
            EventType = (int)e.EventType,
            Success = e.Success,
            Reason = e.Reason is { Length: > 1024 } r ? r[..1024] : e.Reason
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(DeviceId deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = deviceId.Value;
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => (a.ActorDeviceId == id || a.TargetDeviceId == id) && a.Timestamp >= from && a.Timestamp <= to)
            .OrderBy(a => a.Timestamp).Take(MaxQueryResults).ToListAsync(ct);
        return rows.Select(a => new AuditEvent
        {
            EventId = a.EventId,
            Timestamp = a.Timestamp,
            ActorDeviceId = new DeviceId(a.ActorDeviceId),
            TargetDeviceId = a.TargetDeviceId is { } t ? new DeviceId(t) : null,
            SessionId = a.SessionId is { } s ? new SessionId(s) : null,
            EventType = (AuditEventType)a.EventType,
            Success = a.Success,
            Reason = a.Reason
        }).ToList();
    }

    /// <summary>Deletes audit entries older than the cutoff; returns how many.</summary>
    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs.Where(a => a.Timestamp < olderThan).ExecuteDeleteAsync(ct);
    }
}
