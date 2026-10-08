using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class SessionTokenServiceTests
{
    private static SessionTokenService NewService() => new();

    [Fact]
    public void Issue_ReturnsNonEmptyToken()
    {
        var svc = NewService();
        var token = svc.Issue(SessionId.New(), DeviceId.New());
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public void Issue_DifferentSessionsGetDifferentTokens()
    {
        var svc = NewService();
        var device = DeviceId.New();
        var t1 = svc.Issue(SessionId.New(), device);
        var t2 = svc.Issue(SessionId.New(), device);
        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void Validate_ValidToken_ReturnsTrue()
    {
        var svc = NewService();
        var session = SessionId.New();
        var device = DeviceId.New();
        var token = svc.Issue(session, device);

        Assert.True(svc.Validate(token, session, device));
    }

    [Fact]
    public void Validate_WrongSession_ReturnsFalse()
    {
        var svc = NewService();
        var device = DeviceId.New();
        var token = svc.Issue(SessionId.New(), device);

        Assert.False(svc.Validate(token, SessionId.New(), device));
    }

    [Fact]
    public void Validate_WrongDevice_ReturnsFalse()
    {
        var svc = NewService();
        var session = SessionId.New();
        var token = svc.Issue(session, DeviceId.New());

        Assert.False(svc.Validate(token, session, DeviceId.New()));
    }

    [Fact]
    public void Revoke_InvalidatesToken()
    {
        var svc = NewService();
        var session = SessionId.New();
        var device = DeviceId.New();
        var token = svc.Issue(session, device);

        svc.Revoke(session);

        Assert.False(svc.Validate(token, session, device));
    }

    [Fact]
    public void Revoke_OnlyAffectsTargetSession()
    {
        var svc = NewService();
        var device = DeviceId.New();
        var s1 = SessionId.New();
        var s2 = SessionId.New();
        var t1 = svc.Issue(s1, device);
        var t2 = svc.Issue(s2, device);

        svc.Revoke(s1);

        Assert.False(svc.Validate(t1, s1, device));
        Assert.True(svc.Validate(t2, s2, device));
    }
}
