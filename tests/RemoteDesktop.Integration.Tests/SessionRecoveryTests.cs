using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Application;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Signaling;

namespace RemoteDesktop.Integration.Tests;

public sealed class SessionRecoveryTests : IAsyncLifetime
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private readonly ManualClock _clock = new();
    private WebApplicationFactory<Program> _factory = null!;
    private readonly List<HubConnection> _connections = new();

    private DeviceInfoDto _aliceDto = null!;
    private DeviceInfoDto _bobDto = null!;
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;
    private string _sessionId = null!;
    private string _aliceToken = null!;
    private string _bobToken = null!;
    private readonly List<SessionDto> _bobStateUpdates = new();
    private readonly List<string> _bobClosed = new();

    private ISessionManager Sessions => _factory.Services.GetRequiredService<ISessionManager>();
    private ISessionTokenService Tokens => _factory.Services.GetRequiredService<ISessionTokenService>();

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(_clock);
            s.RemoveAll<SessionRecoveryOptions>();
            // Long sweep interval: tests invoke the sweep explicitly for determinism.
            s.AddSingleton(new SessionRecoveryOptions
            {
                Window = TimeSpan.FromSeconds(60),
                SweepInterval = TimeSpan.FromHours(1),
                MaxResumeAttemptsPerMinute = 5
            });
        }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private HubConnection NewConnection()
    {
        var c = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        _connections.Add(c);
        return c;
    }

    private static DeviceInfoDto MakeDevice(string name) => TestAuth.MakeDevice(name);

    private async Task WaitUntil(Func<bool> cond)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond());
    }

    /// <summary>Alice requests, Bob accepts, Alice reports transport → Connected, both receive recovery tokens.</summary>
    private async Task EstablishConnectedSessionAsync()
    {
        _aliceDto = MakeDevice("Alice");
        _bobDto = MakeDevice("Bob");
        _alice = NewConnection();
        _bob = NewConnection();

        var aliceToken = new TaskCompletionSource<string>();
        var bobToken = new TaskCompletionSource<string>();
        var incoming = new TaskCompletionSource<SessionDto>();
        _alice.On<string, string>("RecoveryTokenIssued", (_, t) => aliceToken.TrySetResult(t));
        _bob.On<string, string>("RecoveryTokenIssued", (_, t) => bobToken.TrySetResult(t));
        _bob.On<SessionDto>("IncomingSessionRequest", d => incoming.TrySetResult(d));
        _bob.On<SessionDto>("SessionStateChanged", d => { lock (_bobStateUpdates) _bobStateUpdates.Add(d); });
        _bob.On<string, string>("SessionClosed", (id, _) => { lock (_bobClosed) _bobClosed.Add(id); });

        await _alice.StartAsync();
        await _bob.StartAsync();
        await TestAuth.RegisterAsync(_alice, _aliceDto);
        await TestAuth.RegisterAsync(_bob, _bobDto);

        await _alice.InvokeAsync<SessionDto>("RequestSession", _bobDto.DeviceId);
        var req = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _sessionId = req.SessionId;
        await _bob.InvokeAsync("RespondToSession", _sessionId, true, (long)Permission.ViewOnly);
        await _alice.InvokeAsync("ReportConnectionMode", _sessionId, "Direct");

        _aliceToken = await aliceToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _bobToken = await bobToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<SessionInfo> GetSessionAsync() =>
        (await Sessions.GetAsync(SessionId.Parse(_sessionId)))!;

    private async Task<HubConnection> ReconnectAliceAsync()
    {
        var c = NewConnection();
        await c.StartAsync();
        await TestAuth.RegisterAsync(c, _aliceDto);
        return c;
    }

    // ── Pause on disconnect ──────────────────────────────────────────────

    [Fact]
    public async Task PeerDisconnect_PausesSession_AndNotifiesOtherPeer()
    {
        await EstablishConnectedSessionAsync();
        Assert.Equal(SessionState.Connected, (await GetSessionAsync()).State);

        await _alice.StopAsync();

        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        Assert.NotNull((await GetSessionAsync()).PausedAt);
        await WaitUntil(() => { lock (_bobStateUpdates) return _bobStateUpdates.Any(u => u.State == nameof(SessionState.Paused)); });
    }

    [Fact]
    public async Task Disconnect_BeforeConnected_DoesNotPause()
    {
        var alice = NewConnection();
        var bob = NewConnection();
        var aliceDto = MakeDevice("A");
        var bobDto = MakeDevice("B");
        var incoming = new TaskCompletionSource<SessionDto>();
        bob.On<SessionDto>("IncomingSessionRequest", d => incoming.TrySetResult(d));
        await alice.StartAsync();
        await bob.StartAsync();
        await TestAuth.RegisterAsync(alice, aliceDto);
        await TestAuth.RegisterAsync(bob, bobDto);
        await alice.InvokeAsync<SessionDto>("RequestSession", bobDto.DeviceId);
        var req = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await alice.StopAsync();
        await Task.Delay(200);

        var s = await Sessions.GetAsync(SessionId.Parse(req.SessionId));
        Assert.NotEqual(SessionState.Paused, s!.State);
        Assert.Null(s.PausedAt);
    }

    // ── Resume ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_WithValidToken_WithinWindow_RestoresConnected_AndNotifiesPeer()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var alice2 = await ReconnectAliceAsync();
        var dto = await alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken);

        Assert.Equal(nameof(SessionState.Connected), dto.State);
        var s = await GetSessionAsync();
        Assert.Equal(SessionState.Connected, s.State);
        Assert.Null(s.PausedAt);
        await WaitUntil(() => { lock (_bobStateUpdates) return _bobStateUpdates.Last().State == nameof(SessionState.Connected); });
    }

    [Fact]
    public async Task Resume_TargetDevice_WithItsOwnToken()
    {
        await EstablishConnectedSessionAsync();
        await _bob.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);

        var bob2 = NewConnection();
        await bob2.StartAsync();
        await TestAuth.RegisterAsync(bob2, _bobDto);
        var dto = await bob2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _bobToken);

        Assert.Equal(nameof(SessionState.Connected), dto.State);
    }

    [Fact]
    public async Task Resume_WithWrongOrForeignOrEmptyToken_Rejected_SessionStaysPaused()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        var alice2 = await ReconnectAliceAsync();

        foreach (var bad in new[] { "not-a-token", "", _bobToken /* valid, but bound to Bob's device */ })
        {
            var ex = await Assert.ThrowsAsync<HubException>(
                () => alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, bad));
            Assert.Contains("Cannot resume", ex.Message);
        }
        Assert.Equal(SessionState.Paused, (await GetSessionAsync()).State);
    }

    [Fact]
    public async Task Resume_ByNonParticipant_OrUnknownSession_GetsSameGenericError()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);

        var mallory = NewConnection();
        await mallory.StartAsync();
        await TestAuth.RegisterAsync(mallory, MakeDevice("Mallory"));

        var stolen = await Assert.ThrowsAsync<HubException>(
            () => mallory.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken));
        var unknown = await Assert.ThrowsAsync<HubException>(
            () => mallory.InvokeAsync<SessionDto>("ResumeSession", SessionId.New().ToString(), _aliceToken));

        Assert.Equal(stolen.Message, unknown.Message);
        Assert.Equal(SessionState.Paused, (await GetSessionAsync()).State);
    }

    [Fact]
    public async Task Resume_UnregisteredConnection_Rejected()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        var anon = NewConnection();
        await anon.StartAsync();

        await Assert.ThrowsAsync<HubException>(
            () => anon.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken));
    }

    [Fact]
    public async Task Resume_AfterWindow_Fails_ExpiresSession_RevokesToken()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        _clock.Advance(TimeSpan.FromSeconds(61));

        var alice2 = await ReconnectAliceAsync();
        var ex = await Assert.ThrowsAsync<HubException>(
            () => alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken));

        Assert.Contains("window expired", ex.Message);
        Assert.Equal(SessionState.Expired, (await GetSessionAsync()).State);
        Assert.False(Tokens.Validate(_aliceToken, SessionId.Parse(_sessionId), DeviceId.Parse(_aliceDto.DeviceId)));
    }

    [Fact]
    public async Task Resume_WhenAlreadyConnected_IsIdempotent()
    {
        await EstablishConnectedSessionAsync();
        // Alice reconnected on a new connection before the server noticed the old one drop.
        var alice2 = await ReconnectAliceAsync();

        var dto = await alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken);

        Assert.Equal(nameof(SessionState.Connected), dto.State);
    }

    [Fact]
    public async Task Resume_AttemptsAreRateLimited()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        var alice2 = await ReconnectAliceAsync();

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<HubException>(() => alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, "guess" + i));

        var ex = await Assert.ThrowsAsync<HubException>(
            () => alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken)); // even the right token
        Assert.Contains("Too many", ex.Message);
    }

    [Fact]
    public async Task Resume_ClosedSession_NotResumable()
    {
        await EstablishConnectedSessionAsync();
        await _alice.InvokeAsync("CloseSession", _sessionId, "done"); // revokes tokens, terminates
        var alice2 = await ReconnectAliceAsync();

        await Assert.ThrowsAsync<HubException>(
            () => alice2.InvokeAsync<SessionDto>("ResumeSession", _sessionId, _aliceToken));
    }

    // ── Sweeper ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_ExpiresPausedSessionsPastWindow_AndTellsPeer()
    {
        await EstablishConnectedSessionAsync();
        await _alice.StopAsync();
        await WaitUntil(() => GetSessionAsync().Result.State == SessionState.Paused);
        var sweeper = _factory.Services.GetRequiredService<SessionRecoveryService>();

        Assert.Equal(0, await sweeper.ExpirePausedSessionsAsync()); // still inside the window
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await sweeper.ExpirePausedSessionsAsync());
        Assert.Equal(0, await sweeper.ExpirePausedSessionsAsync()); // idempotent

        Assert.Equal(SessionState.Expired, (await GetSessionAsync()).State);
        Assert.False(Tokens.Validate(_bobToken, SessionId.Parse(_sessionId), DeviceId.Parse(_bobDto.DeviceId)));
        await WaitUntil(() => { lock (_bobClosed) return _bobClosed.Contains(_sessionId); });
    }

    [Fact]
    public async Task Sweep_DoesNotTouchConnectedSessions()
    {
        await EstablishConnectedSessionAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        var sweeper = _factory.Services.GetRequiredService<SessionRecoveryService>();

        Assert.Equal(0, await sweeper.ExpirePausedSessionsAsync());
        Assert.Equal(SessionState.Connected, (await GetSessionAsync()).State);
    }

    // ── Atomic transitions (unit) ────────────────────────────────────────

    [Fact]
    public async Task SessionManager_ResumeAndExpire_RaceHasExactlyOneWinner()
    {
        var mgr = new RemoteDesktop.Server.Session.InMemorySessionManager();
        var window = TimeSpan.FromSeconds(60);
        for (var i = 0; i < 100; i++)
        {
            var s = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
            await mgr.UpdateStateAsync(s.SessionId, SessionState.Connected);
            var t0 = DateTimeOffset.UtcNow;
            Assert.True(await mgr.TryPauseAsync(s.SessionId, t0));

            var now = t0 + window; // exactly at the boundary: still resumable, not expirable
            var resume = mgr.TryResumeAsync(s.SessionId, now, window);
            var expire = mgr.TryExpireIfPausedAsync(s.SessionId, now, window);
            await Task.WhenAll(resume, expire);

            Assert.Equal(ResumeResult.Resumed, await resume);
            Assert.False(await expire);
            Assert.Equal(SessionState.Connected, s.State);
        }
    }

    [Fact]
    public async Task SessionManager_PauseOnlyFromConnected()
    {
        var mgr = new RemoteDesktop.Server.Session.InMemorySessionManager();
        var s = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        Assert.False(await mgr.TryPauseAsync(s.SessionId, DateTimeOffset.UtcNow));
        Assert.False(await mgr.TryPauseAsync(SessionId.New(), DateTimeOffset.UtcNow));
        Assert.Equal(ResumeResult.NotResumable, await mgr.TryResumeAsync(s.SessionId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
    }
}
