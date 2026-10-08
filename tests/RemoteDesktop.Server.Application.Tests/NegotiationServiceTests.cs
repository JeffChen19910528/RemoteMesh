using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Application;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Identity;
using RemoteDesktop.Server.Infrastructure;
using RemoteDesktop.Server.Presence;
using RemoteDesktop.Server.Security;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Server.Application.Tests;

/// <summary>
/// The signaling rules run here with no hub, no HTTP and no SignalR: the only seams are the notifier (recorded)
/// and the caller context (a fake connection). If these tests need a server, the layering has regressed.
/// </summary>
public sealed class NegotiationServiceTests
{
    private sealed record Sent(DeviceId To, string Event, object?[] Args);

    private sealed class RecordingNotifier : IClientNotifier
    {
        public ConcurrentQueue<Sent> Sent { get; } = new();
        public Task NotifyAsync(DeviceId device, string eventName, object?[] args, CancellationToken ct = default)
        {
            Sent.Enqueue(new Sent(device, eventName, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCaller(string connectionId) : ICallerContext
    {
        public string ConnectionId { get; } = connectionId;
        public string IpAddress => "127.0.0.1";
        public int RegistrationFailures { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class NullRelay : IRelayService
    {
        public Task<RelayOutcome> HandleConnectionAsync(System.Net.WebSockets.WebSocket socket, SessionId sessionId, DeviceId deviceId,
            string sessionToken, CancellationToken ct = default) => Task.FromResult(RelayOutcome.Closed);
        public void CloseSession(SessionId sessionId) { }
    }

    private sealed class World
    {
        public readonly RecordingNotifier Notifier = new();
        public readonly InMemoryConnectionTracker Connections = new();
        public readonly InMemorySessionManager Sessions = new();
        public readonly InMemoryDeviceRegistry Devices = new();
        public readonly InMemoryAuditLog Audit = new();
        public readonly SessionNegotiationService Negotiation;
        public readonly SessionLifecycleService Lifecycle;

        public World()
        {
            var security = new SessionSecurityOptions();
            var tokens = new SessionTokenService();
            var support = new SessionSupport(Connections, Sessions, tokens, security);
            var limiter = new InMemoryRateLimiter();
            Negotiation = new SessionNegotiationService(support, Connections, Devices, Sessions,
                new InMemoryTrustedDeviceStore(), new OtpService(), limiter, Audit, Notifier, security, TimeProvider.System);
            Lifecycle = new SessionLifecycleService(support, Connections, Devices, Sessions, tokens, new NullRelay(),
                limiter, Audit, Notifier, new SessionRecoveryOptions(), security, TimeProvider.System);
            Support = support;
        }

        public SessionSupport Support { get; }

        public async Task<(DeviceId Id, FakeCaller Caller)> OnlineDeviceAsync(string name)
        {
            var id = DeviceId.New();
            await Devices.RegisterAsync(new DeviceInfo { DeviceId = id, DeviceName = name, Platform = "Windows", CreatedAt = DateTimeOffset.UtcNow },
                new byte[] { 1, 2, 3 });
            var caller = new FakeCaller("conn-" + name);
            Connections.Register(caller.ConnectionId, id);
            return (id, caller);
        }
    }

    [Fact]
    public async Task Request_NotifiesTheTarget_AndNobodyElse()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, _) = await w.OnlineDeviceAsync("bob");

        var dto = await w.Negotiation.RequestSessionAsync(alice, bobId.ToString());

        var sent = Assert.Single(w.Notifier.Sent);
        Assert.Equal(bobId, sent.To);
        Assert.Equal("IncomingSessionRequest", sent.Event);
        Assert.Equal(dto.SessionId, ((RemoteDesktop.Protocol.Dtos.SessionDto)sent.Args[0]!).SessionId);
    }

    [Fact]
    public async Task Approval_GivesEachSideItsOwnToken_AndOnlyTheTargetCanApprove()
    {
        var w = new World();
        var (aliceId, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, bob) = await w.OnlineDeviceAsync("bob");
        var dto = await w.Negotiation.RequestSessionAsync(alice, bobId.ToString());

        // The requester cannot approve its own request.
        await Assert.ThrowsAsync<SignalingException>(() =>
            w.Negotiation.RespondToSessionAsync(alice, dto.SessionId, true, (long)Permission.ViewScreen));

        await w.Negotiation.RespondToSessionAsync(bob, dto.SessionId, true, (long)Permission.ViewScreen);

        var auth = w.Notifier.Sent.Where(s => s.Event is "AuthSuccess" or "SessionAuthorized").ToList();
        Assert.Equal(2, auth.Count);
        var toAlice = (RemoteDesktop.Protocol.Dtos.AuthResultDto)auth.Single(s => s.To == aliceId).Args[0]!;
        var toBob = (RemoteDesktop.Protocol.Dtos.AuthResultDto)auth.Single(s => s.To == bobId).Args[0]!;
        Assert.NotEqual(toAlice.SessionToken, toBob.SessionToken);
        Assert.Equal((long)Permission.ViewScreen, toAlice.GrantedPermissions);
    }

    [Fact]
    public async Task UnknownAndOfflineTargets_ReturnTheSameRefusal()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var offline = DeviceId.New();
        await w.Devices.RegisterAsync(new DeviceInfo { DeviceId = offline, DeviceName = "off", Platform = "Windows", CreatedAt = DateTimeOffset.UtcNow }, new byte[] { 9 });

        var unknown = await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, DeviceId.New().ToString()));
        var notConnected = await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, offline.ToString()));
        Assert.Equal(unknown.Message, notConnected.Message); // no existence oracle
        Assert.Empty(w.Notifier.Sent);
    }

    [Fact]
    public async Task PeerKey_IsOnlyForParticipantsOfAnApprovedSession()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, bob) = await w.OnlineDeviceAsync("bob");
        var (_, mallory) = await w.OnlineDeviceAsync("mallory");
        var dto = await w.Negotiation.RequestSessionAsync(alice, bobId.ToString());

        await Assert.ThrowsAsync<SignalingException>(() => w.Lifecycle.GetPeerPublicKeyAsync(alice, dto.SessionId)); // not approved yet
        await w.Negotiation.RespondToSessionAsync(bob, dto.SessionId, true, (long)Permission.ViewScreen);

        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), await w.Lifecycle.GetPeerPublicKeyAsync(alice, dto.SessionId));
        await Assert.ThrowsAsync<SignalingException>(() => w.Lifecycle.GetPeerPublicKeyAsync(mallory, dto.SessionId));
    }

    [Fact]
    public async Task UnregisteredConnection_CannotDoAnything()
    {
        var w = new World();
        var stranger = new FakeCaller("nobody");
        await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(stranger, DeviceId.New().ToString()));
        Assert.Throws<SignalingException>(() => w.Negotiation.GenerateOtp(stranger));
    }
}
