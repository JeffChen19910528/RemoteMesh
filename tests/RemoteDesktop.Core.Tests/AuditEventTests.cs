using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class AuditEventTests
{
    [Fact]
    public void EventId_IsUniquePerEvent()
    {
        var e1 = new AuditEvent { EventType = AuditEventType.RemoteSessionStarted, Success = true, ActorDeviceId = DeviceId.New() };
        var e2 = new AuditEvent { EventType = AuditEventType.RemoteSessionStarted, Success = true, ActorDeviceId = DeviceId.New() };
        Assert.NotEqual(e1.EventId, e2.EventId);
    }

    [Fact]
    public void Timestamp_IsNearNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var e = new AuditEvent { EventType = AuditEventType.AuthenticationSuccess, Success = true, ActorDeviceId = DeviceId.New() };
        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.InRange(e.Timestamp, before, after);
    }
}
