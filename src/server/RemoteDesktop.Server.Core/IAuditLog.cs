using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface IAuditLog
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(DeviceId deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
