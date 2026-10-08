namespace RemoteDesktop.Core;

public sealed class AuditEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DeviceId ActorDeviceId { get; init; }
    public DeviceId? TargetDeviceId { get; init; }
    public SessionId? SessionId { get; init; }
    public required AuditEventType EventType { get; init; }
    public required bool Success { get; init; }
    public string? Reason { get; init; }
}
