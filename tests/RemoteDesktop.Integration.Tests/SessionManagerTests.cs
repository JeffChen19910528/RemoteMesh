using RemoteDesktop.Core;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Integration.Tests;

public sealed class SessionManagerTests
{
    private static InMemorySessionManager NewMgr() => new();

    [Fact]
    public async Task Create_ReturnsSessionWithCreatedState()
    {
        var mgr = NewMgr();
        var a = DeviceId.New();
        var b = DeviceId.New();

        var session = await mgr.CreateAsync(a, b);

        Assert.Equal(SessionState.Created, session.State);
        Assert.Equal(a, session.RequesterDeviceId);
        Assert.Equal(b, session.TargetDeviceId);
        Assert.False(session.IsExpired);
    }

    [Fact]
    public async Task Create_SessionIsRetrievable()
    {
        var mgr = NewMgr();
        var session = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());

        var found = await mgr.GetAsync(session.SessionId);
        Assert.NotNull(found);
        Assert.Equal(session.SessionId, found!.SessionId);
    }

    [Fact]
    public async Task UpdateState_ChangesState()
    {
        var mgr = NewMgr();
        var session = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());

        await mgr.UpdateStateAsync(session.SessionId, SessionState.Connected);

        var updated = await mgr.GetAsync(session.SessionId);
        Assert.Equal(SessionState.Connected, updated!.State);
    }

    [Fact]
    public async Task Terminate_SetsDisconnectedState()
    {
        var mgr = NewMgr();
        var session = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());

        await mgr.TerminateAsync(session.SessionId, "user closed");

        var terminated = await mgr.GetAsync(session.SessionId);
        Assert.Equal(SessionState.Disconnected, terminated!.State);
    }

    [Fact]
    public async Task GetActiveSessions_ExcludesTerminated()
    {
        var mgr = NewMgr();
        var deviceId = DeviceId.New();
        var s1 = await mgr.CreateAsync(deviceId, DeviceId.New());
        var s2 = await mgr.CreateAsync(deviceId, DeviceId.New());
        await mgr.TerminateAsync(s1.SessionId);

        var active = await mgr.GetActiveSessionsAsync(deviceId);

        Assert.DoesNotContain(active, s => s.SessionId == s1.SessionId);
        Assert.Contains(active, s => s.SessionId == s2.SessionId);
    }

    [Fact]
    public async Task GetUnknownSession_ReturnsNull()
    {
        var mgr = NewMgr();
        Assert.Null(await mgr.GetAsync(SessionId.New()));
    }
}
