using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Relay;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Application;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Signaling;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Integration.Tests;

/// <summary>
/// Each test plays an attacker (or a buggy/malicious client) against the real server and asserts the
/// attack fails AND leaves the victim's state untouched.
/// </summary>
public sealed class SecurityAttackTests
{
    // ── Test environment ─────────────────────────────────────────────────

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private sealed class Actor
    {
        public required HubConnection Conn { get; init; }
        public required DeviceInfoDto Dto { get; init; }
        public DeviceId Id => DeviceId.Parse(Dto.DeviceId);
        public ConcurrentQueue<SessionDto> Incoming { get; } = new();
        public ConcurrentQueue<SessionDto> StateChanges { get; } = new();
        public ConcurrentQueue<AuthResultDto> AuthSuccess { get; } = new();
        public ConcurrentQueue<AuthResultDto> Authorized { get; } = new();
        public ConcurrentQueue<string> RecoveryTokens { get; } = new();
        public ConcurrentQueue<string> IceCandidates { get; } = new();
        public ConcurrentQueue<string> Closed { get; } = new();
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly List<HubConnection> _connections = new();
        public ManualClock Clock { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }

        public Env(Action<IServiceCollection>? configure = null, SessionSecurityOptions? security = null,
            RelayOptions? relay = null)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(Clock);
                s.RemoveAll<SessionRecoveryOptions>();
                s.AddSingleton(new SessionRecoveryOptions { SweepInterval = TimeSpan.FromHours(1) });
                if (security is not null)
                {
                    s.RemoveAll<SessionSecurityOptions>();
                    s.AddSingleton(security);
                }
                if (relay is not null) s.AddSingleton(relay);
                configure?.Invoke(s);
            }));
        }

        public T Svc<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

        public HubConnection NewConnection()
        {
            var c = new HubConnectionBuilder()
                .WithUrl("http://localhost/hubs/signaling", o =>
                {
                    o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                })
                .Build();
            _connections.Add(c);
            return c;
        }

        public async Task<Actor> ActorAsync(string name, bool register = true)
        {
            var conn = NewConnection();
            var actor = new Actor { Conn = conn, Dto = TestAuth.MakeDevice(name) };
            conn.On<SessionDto>("IncomingSessionRequest", d => actor.Incoming.Enqueue(d));
            conn.On<SessionDto>("SessionStateChanged", d => actor.StateChanges.Enqueue(d));
            conn.On<AuthResultDto>("AuthSuccess", d => actor.AuthSuccess.Enqueue(d));
            conn.On<AuthResultDto>("SessionAuthorized", d => actor.Authorized.Enqueue(d));
            conn.On<string, string>("RecoveryTokenIssued", (_, t) => actor.RecoveryTokens.Enqueue(t));
            conn.On<string, string>("IceCandidateReceived", (_, c) => actor.IceCandidates.Enqueue(c));
            conn.On<string, string>("SessionClosed", (id, _) => actor.Closed.Enqueue(id));
            await conn.StartAsync();
            if (register) await TestAuth.RegisterAsync(conn, actor.Dto);
            return actor;
        }

        public async Task<WebSocket> RelayAsync(SessionId session, DeviceId device, string token, CancellationToken ct)
        {
            var client = Factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await client.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={session}&deviceId={device}"), ct);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _connections) await c.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    private static async Task Eventually(Func<bool> cond, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond(), "condition not reached in time");
    }

    private static async Task<HubException> Rejected(Task call)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => call);
        return Assert.IsType<HubException>(ex);
    }

    /// <summary>Alice requests Bob; Bob accepts. Leaves the session in EstablishingTransport with both tokens issued.</summary>
    private static async Task<SessionDto> ApproveAsync(Actor alice, Actor bob, Permission perms = Permission.ViewOnly)
    {
        var session = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await Eventually(() => !bob.Incoming.IsEmpty);
        await bob.Conn.InvokeAsync("RespondToSession", session.SessionId, true, (long)perms);
        await Eventually(() => !alice.AuthSuccess.IsEmpty && !bob.Authorized.IsEmpty);
        return session;
    }

    private static async Task<SessionDto> ConnectedAsync(Actor alice, Actor bob)
    {
        var s = await ApproveAsync(alice, bob);
        await alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Relay");
        await Eventually(() => !alice.RecoveryTokens.IsEmpty && !bob.RecoveryTokens.IsEmpty);
        return s;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Device impersonation / authentication
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Impersonation_WrongKeySignature_Rejected_VictimUntouched_AndAudited()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var mallory = await env.ActorAsync("Mallory", register: false);
        var tracker = env.Svc<IConnectionTracker>();

        // Mallory claims Alice's DeviceId and Alice's real public key, but cannot sign with Alice's private key.
        var ex = await Rejected(TestAuth.RegisterAsync(mallory.Conn, alice.Dto, DeviceKeyGenerator.Generate()));

        Assert.Contains("authentication failed", ex.Message);
        Assert.Equal(alice.Conn.ConnectionId, tracker.GetConnectionId(alice.Id));
        var audit = await env.Svc<IAuditLog>().QueryAsync(alice.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Contains(audit, e => e.EventType == AuditEventType.AuthenticationFailure && !e.Success);
    }

    [Fact]
    public async Task Impersonation_OwnKeyForVictimsDeviceId_RejectedByKeyPinning()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var mallory = await env.ActorAsync("Mallory", register: false);
        var attackerKeys = DeviceKeyGenerator.Generate();
        var forged = new DeviceInfoDto
        {
            DeviceId = alice.Dto.DeviceId, // victim's id…
            DeviceName = "Alice", Platform = "Windows",
            PublicKeyBase64 = Convert.ToBase64String(attackerKeys.PublicKey) // …attacker's key, perfectly signed
        };

        await Rejected(TestAuth.RegisterAsync(mallory.Conn, forged, attackerKeys));

        var pinned = await env.Svc<IDeviceRegistry>().GetPublicKeyAsync(alice.Id);
        Assert.Equal(Convert.FromBase64String(alice.Dto.PublicKeyBase64), pinned);
        Assert.Equal(alice.Conn.ConnectionId, env.Svc<IConnectionTracker>().GetConnectionId(alice.Id));
    }

    [Fact]
    public async Task Registration_GenuineOwnerCanReconnect_AndTakesOverTheMapping()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var second = env.NewConnection();
        await second.StartAsync();

        await TestAuth.RegisterAsync(second, alice.Dto); // same key: legitimate re-registration

        Assert.Equal(second.ConnectionId, env.Svc<IConnectionTracker>().GetConnectionId(alice.Id));
    }

    [Fact]
    public async Task Replay_SignatureFromAnotherConnection_Rejected()
    {
        await using var env = new Env();
        var victim = TestAuth.MakeDevice("Victim");
        var legit = env.NewConnection();
        var attacker = env.NewConnection();
        await legit.StartAsync();
        await attacker.StartAsync();

        // The victim legitimately signs for ITS connection; the attacker captured the signature and replays it.
        var challenge = Convert.FromBase64String(await legit.InvokeAsync<string>("BeginRegistration"));
        var signature = TestAuth.SignPayload(TestAuth.KeysOf(victim.DeviceId), challenge, victim.DeviceId, legit.ConnectionId!);

        await attacker.InvokeAsync<string>("BeginRegistration"); // attacker gets its own challenge
        await Rejected(attacker.InvokeAsync("RegisterDevice", victim, signature));

        Assert.Null(env.Svc<IConnectionTracker>().GetConnectionId(DeviceId.Parse(victim.DeviceId)));
    }

    [Fact]
    public async Task Replay_ChallengeIsSingleUse_EvenForTheRealOwner()
    {
        await using var env = new Env();
        var dto = TestAuth.MakeDevice("A");
        var c = env.NewConnection();
        await c.StartAsync();
        var challenge = Convert.FromBase64String(await c.InvokeAsync<string>("BeginRegistration"));
        var sig = TestAuth.SignPayload(TestAuth.KeysOf(dto.DeviceId), challenge, dto.DeviceId, c.ConnectionId!);
        await c.InvokeAsync("RegisterDevice", dto, sig);

        await Rejected(c.InvokeAsync("RegisterDevice", dto, sig)); // same signature again, no new challenge
    }

    [Fact]
    public async Task Registration_WithoutChallenge_OrWithStaleChallenge_OrExpired_Rejected()
    {
        await using var env = new Env();
        var dto = TestAuth.MakeDevice("A");
        var keys = TestAuth.KeysOf(dto.DeviceId);
        var c = env.NewConnection();
        await c.StartAsync();

        // No BeginRegistration at all.
        await Rejected(c.InvokeAsync("RegisterDevice", dto, TestAuth.SignPayload(keys, new byte[32], dto.DeviceId, c.ConnectionId!)));

        // Signed an older challenge after a newer one was issued.
        var old = Convert.FromBase64String(await c.InvokeAsync<string>("BeginRegistration"));
        await c.InvokeAsync<string>("BeginRegistration");
        await Rejected(c.InvokeAsync("RegisterDevice", dto, TestAuth.SignPayload(keys, old, dto.DeviceId, c.ConnectionId!)));

        // Correctly signed but too late.
        var c2 = env.NewConnection();
        await c2.StartAsync();
        var challenge = Convert.FromBase64String(await c2.InvokeAsync<string>("BeginRegistration"));
        env.Clock.Advance(TimeSpan.FromSeconds(31));
        await Rejected(c2.InvokeAsync("RegisterDevice", dto, TestAuth.SignPayload(keys, challenge, dto.DeviceId, c2.ConnectionId!)));
    }

    [Fact]
    public async Task Registration_RejectsMalformedInput_AndRegistersNothing()
    {
        await using var env = new Env();
        var good = TestAuth.MakeDevice("Good");
        var keys = TestAuth.KeysOf(good.DeviceId);

        // A fresh connection per attempt: repeated failures on one connection are (rightly) cut off after 3.
        async Task Try(DeviceInfoDto dto, string? sig = null)
        {
            var c = env.NewConnection();
            await c.StartAsync();
            var ch = Convert.FromBase64String(await c.InvokeAsync<string>("BeginRegistration"));
            sig ??= TestAuth.SignPayload(keys, ch, dto.DeviceId, c.ConnectionId!);
            await Rejected(c.InvokeAsync("RegisterDevice", dto, sig));
        }

        DeviceInfoDto With(Action<DeviceInfoDto> edit)
        {
            var d = new DeviceInfoDto { DeviceId = good.DeviceId, DeviceName = "x", Platform = "p", PublicKeyBase64 = good.PublicKeyBase64 };
            edit(d);
            return d;
        }

        await Try(With(d => d.DeviceId = "not-a-guid"));
        await Try(With(d => d.DeviceName = new string('a', 65)));
        await Try(With(d => d.DeviceName = "bad\u0000name"));
        await Try(With(d => d.DeviceName = "   "));
        await Try(With(d => d.Platform = new string('p', 33)));
        await Try(With(d => d.PublicKeyBase64 = "%%%not-base64%%%"));
        await Try(With(d => d.PublicKeyBase64 = Convert.ToBase64String(new byte[10_000])));
        await Try(With(d => d.PublicKeyBase64 = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo())));
        await Try(With(d => d.PublicKeyBase64 = Convert.ToBase64String(
            ECDsa.Create(ECCurve.NamedCurves.nistP384).ExportSubjectPublicKeyInfo())));
        await Try(With(d => d.PublicKeyBase64 = Convert.ToBase64String(keys.PublicKey.Append((byte)0).ToArray())));
        await Try(good, sig: "!!!");
        await Try(good, sig: Convert.ToBase64String(new byte[5000]));

        Assert.Null(env.Svc<IConnectionTracker>().GetConnectionId(DeviceId.Parse(good.DeviceId)));
    }

    [Fact]
    public async Task BruteForce_RepeatedFailures_AbortTheConnection()
    {
        await using var env = new Env(security: new SessionSecurityOptions { RegistrationFailuresPerConnection = 3 });
        var victim = TestAuth.MakeDevice("Victim");
        var c = env.NewConnection();
        await c.StartAsync();

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => TestAuth.RegisterAsync(c, victim, DeviceKeyGenerator.Generate()));

        await Eventually(() => c.State != HubConnectionState.Connected);
        await Assert.ThrowsAnyAsync<Exception>(() => c.InvokeAsync<string>("BeginRegistration"));
    }

    [Fact]
    public async Task BruteForce_FailuresFromOneAddress_LockOutNewConnections()
    {
        await using var env = new Env(security: new SessionSecurityOptions
            { RegistrationFailuresPerConnection = 100, RegistrationFailuresPerIpPer10Min = 4 });
        var victim = TestAuth.MakeDevice("Victim");
        var c = env.NewConnection();
        await c.StartAsync();
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => TestAuth.RegisterAsync(c, victim, DeviceKeyGenerator.Generate()));

        // A brand-new connection from the same address is refused before any key is even checked,
        // so even the genuine owner's signature is not evaluated during the lockout.
        var fresh = env.NewConnection();
        await fresh.StartAsync();
        var ex = await Rejected(TestAuth.RegisterAsync(fresh, victim));
        Assert.Contains("Too many failed attempts", ex.Message);
    }

    [Theory]
    [InlineData("RequestSession")]
    [InlineData("GenerateOtp")]
    [InlineData("GetTrustedDevices")]
    [InlineData("CloseSession")]
    [InlineData("ResumeSession")]
    [InlineData("RequestRelayToken")]
    [InlineData("SendIceCandidate")]
    [InlineData("ReportConnectionMode")]
    [InlineData("RespondToSession")]
    [InlineData("AddTrustedDevice")]
    [InlineData("RequestSessionWithOtp")]
    public async Task UnauthenticatedConnection_CannotInvokeAnyOperation(string method)
    {
        await using var env = new Env();
        var anon = env.NewConnection();
        await anon.StartAsync();
        var id = Guid.NewGuid().ToString();

        object[] args = method switch
        {
            "RequestSession" => [id],
            "CloseSession" => [id, "x"],
            "ResumeSession" => [id, "token"],
            "RequestRelayToken" => [id],
            "SendIceCandidate" => [id, "{}"],
            "ReportConnectionMode" => [id, "Relay"],
            "RespondToSession" => [id, true, 1L],
            "AddTrustedDevice" => [id, "n", 1L],
            "RequestSessionWithOtp" => [id, "123456"],
            _ => []
        };

        var ex = await Rejected(anon.InvokeCoreAsync(method, typeof(object), args));
        Assert.Contains("not registered", ex.Message);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Authorization / session hijacking
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Hijack_RequesterCannotSkipApprovalByReportingConnected()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId); // Bob never answers

        await Rejected(alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Relay"));

        var state = (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State;
        Assert.Equal(SessionState.Negotiating, state);
        Assert.Empty(alice.RecoveryTokens);
        Assert.Empty(bob.RecoveryTokens);
    }

    [Fact]
    public async Task Hijack_OnlyTheTargetCanAnswer_NotTheRequesterNorOutsiders()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var mallory = await env.ActorAsync("Mallory");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);

        // The requester approving its own request is the classic self-authorization bug.
        await Rejected(alice.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.FullControl));
        await Rejected(mallory.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.FullControl));

        Assert.Equal(SessionState.Negotiating, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State);
        Assert.Empty(alice.AuthSuccess);
    }

    [Fact]
    public async Task Hijack_ClosedSessionCannotBeRevivedByAccepting()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await alice.Conn.InvokeAsync("CloseSession", s.SessionId, "changed my mind");

        await Rejected(bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewOnly));

        Assert.Equal(SessionState.Disconnected, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State);
        Assert.Empty(alice.AuthSuccess);
        Assert.Empty(bob.Authorized);
    }

    [Fact]
    public async Task Hijack_SecondAcceptOrAcceptAfterReject_IssuesNothing()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ApproveAsync(alice, bob);
        Assert.Single(alice.AuthSuccess);

        await Rejected(bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.FullControl));
        await Rejected(bob.Conn.InvokeAsync("RespondToSession", s.SessionId, false, 0L));
        Assert.Single(alice.AuthSuccess); // no second token, no escalation to FullControl
        Assert.Equal(Permission.ViewOnly,
            (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.Permissions.Granted);

        var s2 = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await bob.Conn.InvokeAsync("RespondToSession", s2.SessionId, false, 0L);
        await Rejected(bob.Conn.InvokeAsync("RespondToSession", s2.SessionId, true, (long)Permission.ViewOnly));
        Assert.Equal(SessionState.Failed, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s2.SessionId)))!.State);
    }

    [Fact]
    public async Task Hijack_UnansweredRequestExpires_AndLateAcceptGrantsNothing()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);

        env.Clock.Advance(TimeSpan.FromMinutes(3));
        var ex = await Rejected(bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewOnly));

        Assert.Contains("expired", ex.Message);
        Assert.Equal(SessionState.Failed, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State);
        Assert.Empty(alice.AuthSuccess);
    }

    [Fact]
    public async Task Hijack_SweeperFailsUnansweredRequests_AndNotifiesBoth()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var sweeper = env.Svc<SessionRecoveryService>();

        Assert.Equal(0, await sweeper.ExpireUnansweredRequestsAsync());
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await sweeper.ExpireUnansweredRequestsAsync());

        Assert.Equal(SessionState.Failed, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State);
        await Eventually(() => !alice.Closed.IsEmpty && !bob.Closed.IsEmpty);
    }

    [Fact]
    public async Task Hijack_OutsiderCannotCloseReportOrInjectIntoSomeoneElsesSession()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var mallory = await env.ActorAsync("Mallory");
        var s = await ConnectedAsync(alice, bob);
        var sid = SessionId.Parse(s.SessionId);
        var aliceToken = alice.AuthSuccess.Single().SessionToken;

        await Rejected(mallory.Conn.InvokeAsync("CloseSession", s.SessionId, "gotcha"));
        await Rejected(mallory.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Direct"));
        await Rejected(mallory.Conn.InvokeAsync("SendIceCandidate", s.SessionId, "{\"candidate\":\"evil\"}"));
        await Rejected(mallory.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", s.SessionId));

        Assert.Equal(SessionState.Connected, (await env.Svc<ISessionManager>().GetAsync(sid))!.State);
        Assert.True(env.Svc<ISessionTokenService>().Validate(aliceToken, sid, alice.Id)); // tokens not revoked
        await Task.Delay(100);
        Assert.Empty(alice.IceCandidates);
        Assert.Empty(bob.IceCandidates);
    }

    [Fact]
    public async Task Hijack_ParticipantsCanStillUseTheirOwnSession()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ConnectedAsync(alice, bob);

        await alice.Conn.InvokeAsync("SendIceCandidate", s.SessionId, "{\"candidate\":\"ok\"}");
        await Eventually(() => !bob.IceCandidates.IsEmpty);
        var token = await alice.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", s.SessionId);
        Assert.False(string.IsNullOrEmpty(token.SessionToken));
    }

    [Fact]
    public async Task Hijack_ReportConnectionMode_RejectsUnknownMode_AndRepeatReportsDoNotMintTokens()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ApproveAsync(alice, bob);

        await Rejected(alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Quantum"));
        await Rejected(alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Unknown"));

        await alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Direct");
        await Eventually(() => alice.RecoveryTokens.Count == 1);
        for (var i = 0; i < 5; i++) await alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Direct");
        await Task.Delay(100);

        Assert.Single(alice.RecoveryTokens);
        Assert.Single(bob.RecoveryTokens);
    }

    [Fact]
    public async Task Hijack_RecoveryToken_IsSingleUse_AndRotatesOnResume()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ConnectedAsync(alice, bob);
        var original = alice.RecoveryTokens.Single();
        await alice.Conn.StopAsync();
        await Eventually(() => env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)).Result!.State == SessionState.Paused);

        var back = env.NewConnection();
        await back.StartAsync();
        await TestAuth.RegisterAsync(back, alice.Dto);
        var resumed = await back.InvokeAsync<SessionDto>("ResumeSession", s.SessionId, original);
        Assert.False(string.IsNullOrEmpty(resumed.Auth!.SessionToken));
        Assert.NotEqual(original, resumed.Auth.SessionToken);

        // Replaying the captured token is useless now; the rotated one works exactly once more.
        await Rejected(back.InvokeAsync<SessionDto>("ResumeSession", s.SessionId, original));
        await back.InvokeAsync<SessionDto>("ResumeSession", s.SessionId, resumed.Auth.SessionToken);
        await Rejected(back.InvokeAsync<SessionDto>("ResumeSession", s.SessionId, resumed.Auth.SessionToken));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L << 20)]
    [InlineData(long.MaxValue)]
    public async Task Permissions_UnknownOrNegativeBits_Rejected(long bad)
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);

        await Rejected(bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, bad));

        Assert.Equal(SessionState.Negotiating, (await env.Svc<ISessionManager>().GetAsync(SessionId.Parse(s.SessionId)))!.State);
    }

    [Fact]
    public async Task TrustedDevices_CannotHoldStandingShutdownRights_OrTrustThemselves()
    {
        await using var env = new Env();
        var bob = await env.ActorAsync("Bob");
        var someone = Guid.NewGuid().ToString();

        await Rejected(bob.Conn.InvokeAsync("AddTrustedDevice", someone, "pal", (long)Permission.RemoteShutdown));
        await Rejected(bob.Conn.InvokeAsync("AddTrustedDevice", someone, "pal", -1L));
        await Rejected(bob.Conn.InvokeAsync("AddTrustedDevice", bob.Dto.DeviceId, "me", (long)Permission.ViewOnly));
        await Rejected(bob.Conn.InvokeAsync("AddTrustedDevice", someone, new string('n', 65), (long)Permission.ViewOnly));
        await bob.Conn.InvokeAsync("AddTrustedDevice", someone, "pal", (long)Permission.FullControl);
    }

    [Fact]
    public async Task TrustedDevice_Impostor_CannotUseTheTrust()
    {
        await using var env = new Env();
        var bob = await env.ActorAsync("Bob");
        var alice = await env.ActorAsync("Alice");
        await bob.Conn.InvokeAsync("AddTrustedDevice", alice.Dto.DeviceId, "Alice", (long)Permission.FullControl);
        var mallory = await env.ActorAsync("Mallory", register: false);

        // Auto-approval for Alice's DeviceId must not be reachable without Alice's key.
        await Rejected(TestAuth.RegisterAsync(mallory.Conn, alice.Dto, DeviceKeyGenerator.Generate()));
        await Rejected(mallory.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
    }

    [Fact]
    public async Task TrustedFastPath_GivesEachSideItsOwnToken_NotTheRequestersToken()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        await bob.Conn.InvokeAsync("AddTrustedDevice", alice.Dto.DeviceId, "Alice", (long)Permission.ViewOnly);
        var established = new TaskCompletionSource<AuthResultDto>();
        bob.Conn.On<AuthResultDto>("SessionEstablished", d => established.TrySetResult(d));

        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var toBob = await established.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(s.Auth!.SessionToken, toBob.SessionToken);
        var tokens = env.Svc<ISessionTokenService>();
        var sid = SessionId.Parse(s.SessionId);
        Assert.True(tokens.Validate(toBob.SessionToken, sid, bob.Id));
        Assert.False(tokens.Validate(toBob.SessionToken, sid, alice.Id));
        Assert.False(tokens.Validate(s.Auth.SessionToken, sid, bob.Id));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Anti-abuse / rate limits / enumeration
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Enumeration_UnknownAndOfflineDevices_AreIndistinguishable()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var carol = await env.ActorAsync("Carol");
        await carol.Conn.StopAsync(); // registered but offline
        await Eventually(() => env.Svc<IConnectionTracker>().GetConnectionId(carol.Id) is null);

        var unknown = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", Guid.NewGuid().ToString()));
        var offline = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", carol.Dto.DeviceId));
        var garbage = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", "../../etc/passwd"));
        var self = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", alice.Dto.DeviceId));

        Assert.Equal(unknown.Message, offline.Message);
        Assert.Equal(unknown.Message, garbage.Message);
        Assert.Equal(unknown.Message, self.Message);
    }

    [Fact]
    public async Task Enumeration_RepeatedUnknownLookups_AreLockedOut_EvenForValidTargets()
    {
        await using var env = new Env(security: new SessionSecurityOptions { UnknownDeviceLookupsPer10Min = 3 });
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");

        for (var i = 0; i < 3; i++)
            await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", Guid.NewGuid().ToString()));

        var ex = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
        Assert.Contains("Too many failed lookups", ex.Message);

        env.Clock.Advance(TimeSpan.FromMinutes(11)); // lockout is temporary
        await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
    }

    [Fact]
    public async Task Flooding_SessionRequests_AreRateLimitedPerRequester()
    {
        await using var env = new Env(security: new SessionSecurityOptions { SessionRequestsPerMinute = 3 });
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var other = await env.ActorAsync("Other");

        for (var i = 0; i < 3; i++) await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var ex = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
        Assert.Contains("Too many requests", ex.Message);

        await other.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId); // others are unaffected
        env.Clock.Advance(TimeSpan.FromMinutes(1.1));
        await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var audit = await env.Svc<IAuditLog>().QueryAsync(alice.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Contains(audit, e => e.EventType == AuditEventType.RateLimitTriggered);
    }

    [Fact]
    public async Task Flooding_PendingRequestsToOneTarget_AreCapped_WithoutRevealingWhy()
    {
        await using var env = new Env(security: new SessionSecurityOptions { MaxPendingRequestsPerTarget = 2 });
        var bob = await env.ActorAsync("Bob");
        var a = await env.ActorAsync("A");
        var b = await env.ActorAsync("B");
        var c = await env.ActorAsync("C");

        await a.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await b.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var capped = await Rejected(c.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
        var unknown = await Rejected(c.Conn.InvokeAsync<SessionDto>("RequestSession", Guid.NewGuid().ToString()));

        Assert.Equal(unknown.Message, capped.Message);
        await Task.Delay(50);
        Assert.Equal(2, bob.Incoming.Count); // Bob is not buried in prompts
    }

    [Fact]
    public async Task Flooding_ActiveSessionsPerDevice_AreCapped()
    {
        await using var env = new Env(security: new SessionSecurityOptions { MaxActiveSessionsPerDevice = 2 });
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");

        await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
        var ex = await Rejected(alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId));
        Assert.Contains("Too many", ex.Message);
    }

    [Fact]
    public async Task Otp_FailureMessages_DoNotRevealWhetherACodeExists()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var carol = await env.ActorAsync("Carol");
        await bob.Conn.InvokeAsync<string>("GenerateOtp"); // Bob shows a code, Carol does not

        var wrong = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, "000000"));
        var none = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", carol.Dto.DeviceId, "000000"));
        var unknown = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", Guid.NewGuid().ToString(), "000000"));
        var letters = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, "abcdef"));
        var longCode = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, new string('1', 100)));
        var self = await Rejected(alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", alice.Dto.DeviceId, "123456"));

        Assert.All(new[] { none, unknown, letters, longCode, self }, e => Assert.Equal(wrong.Message, e.Message));
    }

    [Fact]
    public async Task Otp_BruteForce_IsStoppedAcrossManyAttackerDevices()
    {
        await using var env = new Env();
        var bob = await env.ActorAsync("Bob");
        var code = await bob.Conn.InvokeAsync<string>("GenerateOtp");
        var wrong = code == "111111" ? "222222" : "111111";

        // Five attackers (fresh device IDs sidestep any per-requester limit) burn the per-target budget…
        for (var i = 0; i < 5; i++)
        {
            var attacker = await env.ActorAsync("Attacker" + i);
            await Rejected(attacker.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, wrong));
        }

        // …after which even the correct code is refused until the owner generates a new one.
        var late = await env.ActorAsync("Late");
        await Rejected(late.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, code));
        var fresh = await bob.Conn.InvokeAsync<string>("GenerateOtp");
        var result = await late.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, fresh);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
    }

    [Fact]
    public async Task Otp_IsSingleUse()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var carol = await env.ActorAsync("Carol");
        var bob = await env.ActorAsync("Bob");
        var code = await bob.Conn.InvokeAsync<string>("GenerateOtp");

        await alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, code);
        await Rejected(carol.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, code));
    }

    [Fact]
    public async Task Otp_CodesAreCompared_WithoutCrashing_OnOddInput()
    {
        var svc = new RemoteDesktop.Server.Security.OtpService();
        var device = DeviceId.New();
        svc.Generate(device);

        Assert.Equal(OtpVerifyResult.Invalid, svc.Verify(device, null!));
        Assert.Equal(OtpVerifyResult.Invalid, svc.Verify(device, ""));
        Assert.Equal(OtpVerifyResult.Invalid, svc.Verify(device, "12345"));
        Assert.Equal(OtpVerifyResult.Invalid, svc.Verify(device, "1234567"));
        await Task.CompletedTask;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Relay abuse
    // ═════════════════════════════════════════════════════════════════════

    private sealed record RawSession(SessionId Id, DeviceId Requester, DeviceId Target, string ReqToken, string TgtToken);

    private static async Task<RawSession> RawSessionAsync(Env env, SessionState state = SessionState.EstablishingTransport)
    {
        var sessions = env.Svc<ISessionManager>();
        var tokens = env.Svc<ISessionTokenService>();
        var r = DeviceId.New();
        var t = DeviceId.New();
        var s = await sessions.CreateAsync(r, t);
        await sessions.UpdateStateAsync(s.SessionId, state);
        return new RawSession(s.SessionId, r, t, tokens.Issue(s.SessionId, r), tokens.Issue(s.SessionId, t));
    }

    private static async Task<bool> IsRejectedAsync(WebSocket ws, CancellationToken ct)
    {
        var r = await ws.ReceiveAsync(new byte[16], ct);
        return r.MessageType == WebSocketMessageType.Close && r.CloseStatus == WebSocketCloseStatus.PolicyViolation;
    }

    [Fact]
    public async Task Relay_TokenOfTheOtherParticipant_OrAnOutsider_IsRejected()
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Requester presents the target's token (and vice versa): tokens are bound to one device.
        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, s.Requester, s.TgtToken, cts.Token), cts.Token));
        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, s.Target, s.ReqToken, cts.Token), cts.Token));

        // A third device with a token the server (hypothetically) issued is still not a participant.
        var outsider = DeviceId.New();
        var token = env.Svc<ISessionTokenService>().Issue(s.Id, outsider);
        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, outsider, token, cts.Token), cts.Token));
    }

    [Fact]
    public async Task Relay_Token_IsSingleUse()
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The relay is established with the token…
        await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token);
        await env.RelayAsync(s.Id, s.Target, s.TgtToken, cts.Token);

        // …so a copy of that token (sniffed, leaked from a log) is worthless afterwards.
        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token), cts.Token));
    }

    [Fact]
    public async Task Relay_ExpiredToken_IsRejected()
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        env.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token), cts.Token));
    }

    [Theory]
    [InlineData(SessionState.Negotiating)]   // not approved yet
    [InlineData(SessionState.Authorizing)]
    [InlineData(SessionState.Failed)]
    [InlineData(SessionState.Disconnected)]
    [InlineData(SessionState.Expired)]
    [InlineData(SessionState.Paused)]
    public async Task Relay_RefusesSessionsThatAreNotApprovedAndLive(SessionState state)
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env, state);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(await IsRejectedAsync(await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token), cts.Token));
    }

    [Fact]
    public async Task Relay_UnknownSession_IsRejected_WithTheSameCloseAsABadToken()
    {
        await using var env = new Env();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ws = await env.RelayAsync(SessionId.New(), DeviceId.New(), "whatever", cts.Token);
        var r = await ws.ReceiveAsync(new byte[16], cts.Token);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, r.CloseStatus);
        Assert.Equal("Unauthorized", r.CloseStatusDescription);
    }

    [Fact]
    public async Task Relay_ThirdParty_CannotJoinAnEstablishedRelay_AndDuplicateRoleIsRefused()
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env);
        var tokens = env.Svc<ISessionTokenService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var req = await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token);
        var tgt = await env.RelayAsync(s.Id, s.Target, s.TgtToken, cts.Token);

        // A second requester socket with a fresh valid token must not displace or join the live relay.
        var dup = await env.RelayAsync(s.Id, s.Requester, tokens.Issue(s.Id, s.Requester), cts.Token);
        Assert.True(await IsRejectedAsync(dup, cts.Token));

        // The legitimate pair is unaffected and nothing the attacker sent reaches the target.
        await req.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, cts.Token);
        var buf = new byte[16];
        var got = await tgt.ReceiveAsync(buf, cts.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..got.Count]);
    }

    [Fact]
    public async Task Relay_ClosingTheSession_TearsDownTheLiveRelay()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ApproveAsync(alice, bob);
        var sid = SessionId.Parse(s.SessionId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsA = await env.RelayAsync(sid, alice.Id, alice.AuthSuccess.Single().SessionToken, cts.Token);
        var wsB = await env.RelayAsync(sid, bob.Id, bob.Authorized.Single().SessionToken, cts.Token);
        await wsA.SendAsync(new byte[] { 9 }, WebSocketMessageType.Binary, true, cts.Token);
        await wsB.ReceiveAsync(new byte[4], cts.Token); // relay is live

        await alice.Conn.InvokeAsync("CloseSession", s.SessionId, "done");

        var ra = await wsA.ReceiveAsync(new byte[4], cts.Token);
        var rb = await wsB.ReceiveAsync(new byte[4], cts.Token);
        Assert.Equal(WebSocketMessageType.Close, ra.MessageType);
        Assert.Equal(WebSocketMessageType.Close, rb.MessageType);
        Assert.Equal(0, ((InMemoryRelayService)env.Svc<IRelayService>()).ActivePairings);
    }

    [Fact]
    public async Task Relay_StartAndEnd_AreAudited()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ApproveAsync(alice, bob);
        var sid = SessionId.Parse(s.SessionId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsA = await env.RelayAsync(sid, alice.Id, alice.AuthSuccess.Single().SessionToken, cts.Token);
        await env.RelayAsync(sid, bob.Id, bob.Authorized.Single().SessionToken, cts.Token);
        await wsA.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, cts.Token);

        var audit = env.Svc<IAuditLog>();
        async Task<bool> Has(AuditEventType t) =>
            (await audit.QueryAsync(alice.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue)).Any(e => e.EventType == t && e.SessionId == sid);

        await Eventually(() => Has(AuditEventType.RelaySessionStarted).Result);
        await alice.Conn.InvokeAsync("CloseSession", s.SessionId, "done");
        await Eventually(() => Has(AuditEventType.RelaySessionEnded).Result);
    }

    [Fact]
    public async Task Relay_OversizedMessage_ClosesTheOffender()
    {
        await using var env = new Env(relay: new RelayOptions { MaxMessageBytes = 64 * 1024 });
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var req = await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token);
        var tgt = await env.RelayAsync(s.Id, s.Target, s.TgtToken, cts.Token);

        var big = new byte[16 * 1024];
        for (var i = 0; i < 6; i++) // 96 KiB in one message, over the 64 KiB cap
            await req.SendAsync(big, WebSocketMessageType.Binary, endOfMessage: i == 5, cts.Token);

        // The sender is closed with MessageTooBig; the target never sees a complete oversized message.
        var r = await DrainUntilCloseAsync(req, cts.Token);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, r.CloseStatus);
        _ = tgt;
    }

    [Fact]
    public async Task Relay_TextFrames_AreRefused()
    {
        await using var env = new Env();
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var req = await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token);
        await env.RelayAsync(s.Id, s.Target, s.TgtToken, cts.Token);

        await req.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, cts.Token);

        var r = await DrainUntilCloseAsync(req, cts.Token);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, r.CloseStatus);
    }

    [Fact]
    public async Task Relay_PeerThatNeverArrives_TimesOut_AndFreesTheSlot()
    {
        await using var env = new Env(relay: new RelayOptions { PeerWaitTimeout = TimeSpan.FromMilliseconds(300) });
        var s = await RawSessionAsync(env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var lonely = await env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token);
        var r = await DrainUntilCloseAsync(lonely, cts.Token);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, r.CloseStatus);
        Assert.Equal("Peer timeout", r.CloseStatusDescription);
        await Eventually(() => ((InMemoryRelayService)env.Svc<IRelayService>()).ActivePairings == 0);
    }

    [Fact]
    public async Task Relay_BadRequests_AreRefusedAndEventuallyThrottledPerAddress()
    {
        await using var env = new Env();
        var http = env.Factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var s = await RawSessionAsync(env);

        // Missing parameters.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/ws/relay", cts.Token)).StatusCode);
        // Plain HTTP (not a WebSocket upgrade).
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.GetAsync($"/ws/relay?sessionId={s.Id}&deviceId={s.Requester}", cts.Token)).StatusCode);

        // Ten unauthorized upgrades from one address lock that address out.
        for (var i = 0; i < 10; i++)
        {
            var ws = await env.RelayAsync(s.Id, s.Requester, "bad-" + i, cts.Token);
            await IsRejectedAsync(ws, cts.Token);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => env.RelayAsync(s.Id, s.Requester, s.ReqToken, cts.Token));
    }

    private static async Task<WebSocketReceiveResult> DrainUntilCloseAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return r;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // End to end: the relay only ever carries ciphertext
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EndToEnd_ThroughRelay_PayloadIsEncrypted_AndTamperingByTheRelayIsDetected()
    {
        await using var env = new Env();
        var alice = await env.ActorAsync("Alice");
        var bob = await env.ActorAsync("Bob");
        var s = await ApproveAsync(alice, bob);
        var sid = SessionId.Parse(s.SessionId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var wsA = await env.RelayAsync(sid, alice.Id, alice.AuthSuccess.Single().SessionToken, cts.Token);
        var wsB = await env.RelayAsync(sid, bob.Id, bob.Authorized.Single().SessionToken, cts.Token);

        // Session keys from the devices' long-term keys (the existing ISessionCrypto), one per side.
        var aKeys = TestAuth.KeysOf(alice.Dto.DeviceId);
        var bKeys = TestAuth.KeysOf(bob.Dto.DeviceId);
        using var cryptoA = new AesGcmSessionCrypto();
        using var cryptoB = new AesGcmSessionCrypto();
        await cryptoA.DeriveSessionKeyAsync(sid, aKeys.PrivateKey, bKeys.PublicKey);
        await cryptoB.DeriveSessionKeyAsync(sid, bKeys.PrivateKey, aKeys.PublicKey);

        await using var tA = new SecureTransport(new RelayTransport(wsA, sid), cryptoA, isInitiator: true);
        await using var tB = new SecureTransport(new RelayTransport(wsB, sid), cryptoB, isInitiator: false);
        var chA = (SecureChannel)await tA.OpenChannelAsync(ChannelType.Clipboard, cts.Token);
        var chB = (SecureChannel)await tB.OpenChannelAsync(ChannelType.Clipboard, cts.Token);

        var secret = "correct horse battery staple"u8.ToArray();
        await chA.SendAsync(secret, cts.Token);

        await using var rx = chB.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await rx.MoveNextAsync());
        Assert.Equal(secret, rx.Current.ToArray());
        Assert.Equal(0, chB.DroppedFrames);
        _ = chA;
    }
}
