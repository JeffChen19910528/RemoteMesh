using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Application;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Session;
using RemoteDesktop.Server.Signaling;

namespace RemoteDesktop.Integration.Tests;

/// <summary>Maximum Sessions, Session Expiration and memory hygiene on the server.</summary>
public sealed class ResourceLimitTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private sealed record Device(HubConnection Conn, DeviceInfoDto Dto, List<AuthResultDto> Auth, List<AuthResultDto> Authorized)
    {
        public DeviceId Id => DeviceId.Parse(Dto.DeviceId);
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly List<HubConnection> _conns = new();
        public ManualClock Clock { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }

        public Env(SessionSecurityOptions? security = null, RelayOptions? relay = null)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(Clock);
                s.RemoveAll<SessionRecoveryOptions>();
                s.AddSingleton(new SessionRecoveryOptions { SweepInterval = TimeSpan.FromHours(1) });
                if (security is not null) { s.RemoveAll<SessionSecurityOptions>(); s.AddSingleton(security); }
                if (relay is not null) s.AddSingleton(relay);
            }));
        }

        public T Svc<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

        public async Task<Device> DeviceAsync(string name)
        {
            var conn = new HubConnectionBuilder()
                .WithUrl("http://localhost/hubs/signaling", o =>
                {
                    o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                }).Build();
            _conns.Add(conn);
            var auth = new List<AuthResultDto>();
            var authorized = new List<AuthResultDto>();
            conn.On<AuthResultDto>("AuthSuccess", a => { lock (auth) auth.Add(a); });
            conn.On<AuthResultDto>("SessionAuthorized", a => { lock (authorized) authorized.Add(a); });
            await conn.StartAsync();
            var dto = TestAuth.MakeDevice(name);
            await TestAuth.RegisterAsync(conn, dto);
            return new Device(conn, dto, auth, authorized);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _conns) await c.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    private static async Task Eventually(Func<bool> cond, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond());
    }

    // ── Maximum Sessions ─────────────────────────────────────────────────

    [Fact]
    public async Task ServerWideSessionCap_RefusesNewSessions_UntilOthersEnd()
    {
        await using var env = new Env(new SessionSecurityOptions { MaxTotalActiveSessions = 2 });
        var bob = await env.DeviceAsync("Bob");
        var a = await env.DeviceAsync("A");
        var b = await env.DeviceAsync("B");
        var c = await env.DeviceAsync("C");

        var first = await a.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await b.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);

        var ex = await Assert.ThrowsAsync<HubException>(() => c.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
        Assert.Contains("busy", ex.Message);

        await a.Conn.InvokeAsync("CloseSession", first.SessionId, "done"); // a slot frees up
        await c.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
    }

    [Fact]
    public async Task RelayAtCapacity_RefusesNewPairings_WithoutUsingUpTheToken()
    {
        await using var env = new Env(relay: new RelayOptions { MaxConcurrentPairings = 1 });
        var sessions = env.Svc<ISessionManager>();
        var tokens = env.Svc<ISessionTokenService>();

        async Task<(SessionId Id, DeviceId R, string Token)> NewAsync()
        {
            var (r, t) = (DeviceId.New(), DeviceId.New());
            var s = await sessions.CreateAsync(r, t);
            await sessions.UpdateStateAsync(s.SessionId, SessionState.EstablishingTransport);
            return (s.SessionId, r, tokens.Issue(s.SessionId, r));
        }

        async Task<WebSocket> Connect(SessionId s, DeviceId d, string token)
        {
            var c = env.Factory.Server.CreateWebSocketClient();
            c.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await c.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={s}&deviceId={d}"), CancellationToken.None);
        }

        var one = await NewAsync();
        var two = await NewAsync();
        await Connect(one.Id, one.R, one.Token);          // takes the only slot (waits for its peer)
        await Eventually(() => ((InMemoryRelayService)env.Svc<IRelayService>()).ActivePairings == 1);

        var refused = await Connect(two.Id, two.R, two.Token);
        var close = await refused.ReceiveAsync(new byte[8], CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.InternalServerError, close.CloseStatus);
        Assert.True(tokens.Validate(two.Token, two.Id, two.R)); // capacity refusal did not burn the token
    }

    // ── Session expiration ───────────────────────────────────────────────

    [Fact]
    public async Task ConnectedSession_IsEnded_WhenItPassesItsExpiry_RelayDropped_TokensRevoked()
    {
        await using var env = new Env();
        var alice = await env.DeviceAsync("Alice");
        var bob = await env.DeviceAsync("Bob");
        var closedForBob = new List<string>();
        bob.Conn.On<string, string>("SessionClosed", (id, _) => { lock (closedForBob) closedForBob.Add(id); });

        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewOnly);
        await Eventually(() => alice.Auth.Count == 1 && bob.Authorized.Count == 1);
        await alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Relay");
        var sid = SessionId.Parse(s.SessionId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = env.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["X-Relay-Token"] = alice.Auth[0].SessionToken;
        var ws = await client.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={sid}&deviceId={alice.Dto.DeviceId}"), cts.Token);
        var recovery = env.Svc<SessionRecoveryService>();

        Assert.Equal(0, await recovery.ExpireOverdueSessionsAsync()); // not yet
        env.Clock.Advance(TimeSpan.FromHours(9));                      // default session lifetime is 8 h
        Assert.Equal(1, await recovery.ExpireOverdueSessionsAsync());
        Assert.Equal(0, await recovery.ExpireOverdueSessionsAsync());  // idempotent

        Assert.Equal(SessionState.Expired, (await env.Svc<ISessionManager>().GetAsync(sid))!.State);
        Assert.False(env.Svc<ISessionTokenService>().Validate(bob.Authorized[0].SessionToken, sid, bob.Id));
        var close = await ws.ReceiveAsync(new byte[8], cts.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        await Eventually(() => { lock (closedForBob) return closedForBob.Contains(s.SessionId); });
    }

    // ── Memory hygiene ───────────────────────────────────────────────────

    [Fact]
    public async Task FinishedSessions_AreForgotten_AfterTheRetentionPeriod_ButLiveOnesAreKept()
    {
        await using var env = new Env(new SessionSecurityOptions { FinishedSessionRetention = TimeSpan.FromHours(1) });
        var mgr = env.Svc<ISessionManager>();
        var done = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        await mgr.UpdateStateAsync(done.SessionId, SessionState.Disconnected);
        var failed = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        await mgr.UpdateStateAsync(failed.SessionId, SessionState.Failed);
        var live = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        await mgr.UpdateStateAsync(live.SessionId, SessionState.Connected);
        var recovery = env.Svc<SessionRecoveryService>();

        Assert.Equal(0, await recovery.PurgeFinishedSessionsAsync()); // too recent

        env.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(2, await recovery.PurgeFinishedSessionsAsync());

        Assert.Null(await mgr.GetAsync(done.SessionId));
        Assert.Null(await mgr.GetAsync(failed.SessionId));
        Assert.NotNull(await mgr.GetAsync(live.SessionId));
    }

    [Fact]
    public async Task CountActive_IgnoresFinishedAndOverdueSessions()
    {
        var clock = new ManualClock();
        var mgr = new InMemorySessionManager(clock);
        await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        var b = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        var c = await mgr.CreateAsync(DeviceId.New(), DeviceId.New());
        await mgr.UpdateStateAsync(b.SessionId, SessionState.Disconnected);
        await mgr.UpdateStateAsync(c.SessionId, SessionState.Expired);

        Assert.Equal(1, await mgr.CountActiveAsync());
        clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(0, await mgr.CountActiveAsync());
    }
}
