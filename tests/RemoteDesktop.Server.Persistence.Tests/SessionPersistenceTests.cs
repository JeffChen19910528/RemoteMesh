using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Persistence;
using RemoteDesktop.Server.Security;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Server.Persistence.Tests;

/// <summary>A server "instance": fresh in-memory state over the same database, started the way the real server starts.</summary>
internal sealed class ServerInstance
{
    public InMemorySessionManager Memory { get; }
    public PersistentSessionManager Sessions { get; }
    public SessionTokenService MemoryTokens { get; }
    public PersistentSessionTokenService Tokens { get; }
    public IDbContextFactory<RemoteMeshDbContext> Factory { get; }

    private ServerInstance(IDbContextFactory<RemoteMeshDbContext> factory)
    {
        Factory = factory;
        Memory = new InMemorySessionManager();
        Sessions = new PersistentSessionManager(Memory, factory);
        MemoryTokens = new SessionTokenService();
        Tokens = new PersistentSessionTokenService(MemoryTokens, factory);
    }

    public static async Task<ServerInstance> StartAsync(IDbContextFactory<RemoteMeshDbContext> factory, DateTimeOffset? now = null)
    {
        var s = new ServerInstance(factory);
        await SessionRestore.RunAsync(factory, s.Memory, s.MemoryTokens, new SessionRecoveryOptions(), now ?? DateTimeOffset.UtcNow, default);
        return s;
    }
}

public sealed class SessionPersistenceTests : PostgresTestBase
{
    private static async Task<SessionInfo> ConnectedAsync(ServerInstance server, DeviceId a, DeviceId b)
    {
        var s = await server.Sessions.CreateAsync(a, b);
        s.Permissions.Grant(Permission.ViewScreen | Permission.ControlMouse);
        s.RequesterShortId = "123456789";
        await server.Sessions.UpdateStateAsync(s.SessionId, SessionState.EstablishingTransport);
        await server.Sessions.SaveAsync(s.SessionId);
        Assert.True(await server.Sessions.TryTransitionAsync(s.SessionId, SessionState.EstablishingTransport, SessionState.Connected));
        s.ConnectionMode = ConnectionMode.Relay;
        await server.Sessions.SaveAsync(s.SessionId);
        return s;
    }

    [PostgresFact]
    public async Task AConnectedSession_ComesBackPaused_WithEverythingItHad()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var a = DeviceId.New();
        var b = DeviceId.New();
        var s = await ConnectedAsync(first, a, b);
        Assert.Equal(ResumeResult.Resumed, await first.Sessions.TryResumeAsync(s.SessionId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))); // epoch 1

        var restartedAt = DateTimeOffset.UtcNow;
        var second = await ServerInstance.StartAsync(factory, restartedAt);
        var back = await second.Sessions.GetAsync(s.SessionId);

        Assert.NotNull(back);
        Assert.Equal(SessionState.Paused, back.State);   // every connection died with the old server
        Assert.Equal(restartedAt, back.PausedAt);        // the recovery window starts now
        Assert.Equal(a, back.RequesterDeviceId);
        Assert.Equal(b, back.TargetDeviceId);
        Assert.Equal(1, back.Epoch);
        Assert.Equal("123456789", back.RequesterShortId);
        Assert.Equal(ConnectionMode.Relay, back.ConnectionMode);
        Assert.Equal(Permission.ViewScreen | Permission.ControlMouse, back.Permissions.Granted);

        // ...and the people can come back through the normal resume path, which gives a new epoch.
        Assert.Equal(ResumeResult.Resumed, await second.Sessions.TryResumeAsync(s.SessionId, restartedAt.AddSeconds(5), TimeSpan.FromMinutes(1)));
        Assert.Equal(2, (await second.Sessions.GetAsync(s.SessionId))!.Epoch);
    }

    [PostgresFact]
    public async Task APausedSession_KeepsItsOriginalWindow_SoALongOutageDoesNotStretchIt()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var s = await ConnectedAsync(first, DeviceId.New(), DeviceId.New());
        var pausedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        Assert.True(await first.Sessions.TryPauseAsync(s.SessionId, pausedAt));

        var second = await ServerInstance.StartAsync(factory);

        // PostgreSQL keeps microseconds, .NET keeps 100 ns: the same moment, give or take that rounding.
        Assert.True(Math.Abs(((await second.Sessions.GetAsync(s.SessionId))!.PausedAt!.Value - pausedAt).TotalMilliseconds) < 1);
        Assert.Equal(ResumeResult.WindowExpired, await second.Sessions.TryResumeAsync(s.SessionId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
    }

    [PostgresFact]
    public async Task SessionsStillBeingAskedAbout_FailOnRestart_AndFinishedOnesStayGone()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var pending = await first.Sessions.CreateAsync(DeviceId.New(), DeviceId.New());
        await first.Sessions.UpdateStateAsync(pending.SessionId, SessionState.Negotiating);
        var transport = await first.Sessions.CreateAsync(DeviceId.New(), DeviceId.New());
        await first.Sessions.UpdateStateAsync(transport.SessionId, SessionState.EstablishingTransport);
        var done = await ConnectedAsync(first, DeviceId.New(), DeviceId.New());
        await first.Sessions.TerminateAsync(done.SessionId);

        var second = await ServerInstance.StartAsync(factory);

        Assert.Null(await second.Sessions.GetAsync(pending.SessionId));   // not restored...
        Assert.Null(await second.Sessions.GetAsync(transport.SessionId));
        Assert.Null(await second.Sessions.GetAsync(done.SessionId));
        await using var db = await factory.CreateDbContextAsync();         // ...and the database says why
        Assert.Equal((int)SessionState.Failed, (await db.Sessions.SingleAsync(r => r.SessionId == pending.SessionId.Value)).State);
        Assert.Equal((int)SessionState.Failed, (await db.Sessions.SingleAsync(r => r.SessionId == transport.SessionId.Value)).State);
        Assert.Equal((int)SessionState.Disconnected, (await db.Sessions.SingleAsync(r => r.SessionId == done.SessionId.Value)).State);
        Assert.Equal(0, await second.Sessions.CountActiveAsync());
    }

    [PostgresFact]
    public async Task ASessionPastItsExpiry_IsNotRestored()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var s = await ConnectedAsync(first, DeviceId.New(), DeviceId.New());

        var second = await ServerInstance.StartAsync(factory, s.ExpiresAt.AddMinutes(1));

        Assert.Null(await second.Sessions.GetAsync(s.SessionId));
    }

    [PostgresFact]
    public async Task ARestoredSessionIsStillAKnownSession_ForTheCapsAndTheQueries()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var a = DeviceId.New();
        await ConnectedAsync(first, a, DeviceId.New());

        var second = await ServerInstance.StartAsync(factory);

        Assert.Single(await second.Sessions.GetActiveSessionsAsync(a));
        Assert.Equal(1, await second.Sessions.CountActiveAsync());
        Assert.Single(await second.Sessions.GetSessionsByStateAsync(SessionState.Paused));
    }

    [PostgresFact]
    public async Task ManyChangesRacingEachOther_LeaveTheNewestStateInTheDatabase()
    {
        var factory = await OpenAsync();
        var server = await ServerInstance.StartAsync(factory);
        var s = await ConnectedAsync(server, DeviceId.New(), DeviceId.New());

        // Resume (epoch up) from many callers at once; pause/resume interleaved.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            server.Sessions.TryResumeAsync(s.SessionId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))));

        var inMemory = (await server.Sessions.GetAsync(s.SessionId))!;
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Sessions.AsNoTracking().SingleAsync(r => r.SessionId == s.SessionId.Value);
        Assert.Equal(40, inMemory.Epoch);
        Assert.Equal(inMemory.Epoch, stored.Epoch); // the database never ends up behind
    }

    [PostgresFact]
    public async Task PurgingFinishedSessions_AlsoEmptiesTheDatabase()
    {
        var factory = await OpenAsync();
        var server = await ServerInstance.StartAsync(factory);
        var s = await ConnectedAsync(server, DeviceId.New(), DeviceId.New());
        await server.Sessions.TerminateAsync(s.SessionId);

        Assert.Equal(1, await server.Sessions.PurgeFinishedAsync(DateTimeOffset.UtcNow.AddMinutes(1)));

        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.Sessions.AnyAsync(r => r.SessionId == s.SessionId.Value));
    }
}

public sealed class SessionTokenPersistenceTests : PostgresTestBase
{
    private static async Task<(SessionId Session, DeviceId Device)> PausedSessionAsync(ServerInstance server)
    {
        var device = DeviceId.New();
        var s = await server.Sessions.CreateAsync(device, DeviceId.New());
        await server.Sessions.UpdateStateAsync(s.SessionId, SessionState.EstablishingTransport);
        Assert.True(await server.Sessions.TryTransitionAsync(s.SessionId, SessionState.EstablishingTransport, SessionState.Connected));
        return (s.SessionId, device);
    }

    [PostgresFact]
    public async Task ARecoveryTokenHandedOutBeforeARestart_WorksOnceAfterIt()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var (session, device) = await PausedSessionAsync(first);
        var token = first.Tokens.Issue(session, device, TimeSpan.FromMinutes(30));

        var second = await ServerInstance.StartAsync(factory);

        Assert.False(second.Tokens.Validate(token, session, DeviceId.New()));  // still bound to its device
        Assert.False(second.Tokens.Validate(token, SessionId.New(), device));  // ...and its session
        Assert.True(second.Tokens.Validate(token, session, device));
        Assert.True(second.Tokens.TryConsume(token, session, device));
        Assert.False(second.Tokens.TryConsume(token, session, device));        // single use
    }

    [PostgresFact]
    public async Task ATokenThatWasUsedUpBeforeARestart_IsNotBroughtBack()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var (session, device) = await PausedSessionAsync(first);
        var token = first.Tokens.Issue(session, device, TimeSpan.FromMinutes(30));
        Assert.True(first.Tokens.TryConsume(token, session, device));

        var second = await ServerInstance.StartAsync(factory);

        Assert.False(second.Tokens.Validate(token, session, device));
        Assert.False(second.Tokens.TryConsume(token, session, device));
    }

    [PostgresFact]
    public async Task ARevokedToken_AndAnExpiredOne_AreNotBroughtBack()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var (s1, d1) = await PausedSessionAsync(first);
        var (s2, d2) = await PausedSessionAsync(first);
        var revoked = first.Tokens.Issue(s1, d1, TimeSpan.FromMinutes(30));
        first.Tokens.Revoke(s1);
        var shortLived = first.Tokens.Issue(s2, d2, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var second = await ServerInstance.StartAsync(factory);

        Assert.False(second.Tokens.Validate(revoked, s1, d1));
        Assert.False(second.Tokens.Validate(shortLived, s2, d2));
    }

    [PostgresFact]
    public async Task ATokenForASessionThatDidNotComeBack_IsDropped()
    {
        var factory = await OpenAsync();
        var first = await ServerInstance.StartAsync(factory);
        var device = DeviceId.New();
        var pending = await first.Sessions.CreateAsync(device, DeviceId.New());
        await first.Sessions.UpdateStateAsync(pending.SessionId, SessionState.Negotiating);
        var token = first.Tokens.Issue(pending.SessionId, device, TimeSpan.FromMinutes(30));

        var second = await ServerInstance.StartAsync(factory);

        Assert.False(second.Tokens.Validate(token, pending.SessionId, device));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.SessionTokens.ToListAsync()); // not even a row is left
    }

    [PostgresFact]
    public async Task TheDatabaseNeverHoldsATokenItself_OnlyItsHash()
    {
        var factory = await OpenAsync();
        var server = await ServerInstance.StartAsync(factory);
        var (session, device) = await PausedSessionAsync(server);
        var token = server.Tokens.Issue(session, device, TimeSpan.FromMinutes(30));

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.SessionTokens.AsNoTracking().SingleAsync();

        Assert.Equal(32, row.TokenHash.Length);
        Assert.Equal(SessionTokenService.Hash(token), row.TokenHash);
        Assert.DoesNotContain(token, Convert.ToBase64String(row.TokenHash));
        Assert.NotEqual(Convert.FromBase64String(token), row.TokenHash);
        // and presenting the hash as if it were the token gets nowhere
        Assert.False(server.Tokens.Validate(Convert.ToBase64String(row.TokenHash), session, device));
        Assert.False(server.Tokens.Validate(Convert.ToHexString(row.TokenHash), session, device));
    }

    [PostgresFact]
    public async Task ConsumingFromManyThreadsAtOnce_StillLetsExactlyOneThrough_AndLeavesNothingBehind()
    {
        var factory = await OpenAsync();
        var server = await ServerInstance.StartAsync(factory);
        var (session, device) = await PausedSessionAsync(server);
        var token = server.Tokens.Issue(session, device, TimeSpan.FromMinutes(30));

        var wins = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => server.Tokens.TryConsume(token, session, device))));

        Assert.Equal(1, wins.Count(w => w));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.SessionTokens.ToListAsync());
    }
}
