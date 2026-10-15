using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class SessionInfoTests
{
    [Fact]
    public void IsExpired_WhenExpiresAtInPast_ReturnsTrue()
    {
        var session = new SessionInfo
        {
            SessionId = SessionId.New(),
            RequesterDeviceId = DeviceId.New(),
            TargetDeviceId = DeviceId.New(),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        Assert.True(session.IsExpired);
    }

    [Fact]
    public void IsExpired_WhenExpiresAtInFuture_ReturnsFalse()
    {
        var session = new SessionInfo
        {
            SessionId = SessionId.New(),
            RequesterDeviceId = DeviceId.New(),
            TargetDeviceId = DeviceId.New(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        Assert.False(session.IsExpired);
    }

    [Fact]
    public void State_DefaultsToCreated()
    {
        var session = new SessionInfo
        {
            SessionId = SessionId.New(),
            RequesterDeviceId = DeviceId.New(),
            TargetDeviceId = DeviceId.New(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        Assert.Equal(SessionState.Created, session.State);
    }

    [Fact]
    public void Permissions_DefaultsToViewOnly()
    {
        var session = new SessionInfo
        {
            SessionId = SessionId.New(),
            RequesterDeviceId = DeviceId.New(),
            TargetDeviceId = DeviceId.New(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        Assert.True(session.Permissions.Has(Permission.ViewScreen));
        Assert.False(session.Permissions.Has(Permission.ControlMouse));
    }
}
