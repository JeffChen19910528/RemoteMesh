using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Infrastructure;

public sealed class InMemoryAuditLog : IAuditLog
{
    /// <summary>Oldest events are discarded beyond this, so event spam cannot exhaust server memory.</summary>
    public const int MaxEvents = 100_000;

    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        _events.Enqueue(auditEvent);
        while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DeviceId deviceId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var results = _events
            .Where(e => (e.ActorDeviceId == deviceId || e.TargetDeviceId == deviceId)
                        && e.Timestamp >= from
                        && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToList();

        return Task.FromResult<IReadOnlyList<AuditEvent>>(results);
    }

    public int Count => _events.Count;
}
