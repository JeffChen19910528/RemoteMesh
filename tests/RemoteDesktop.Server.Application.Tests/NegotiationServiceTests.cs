using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
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
        public readonly InMemoryShortIdDirectory ShortIds = new();
        public readonly DeviceRegistrationService Registration;
        public readonly OtpService Otp = new();
        public readonly InMemoryAuditLog Audit = new();
        public readonly SessionNegotiationService Negotiation;
        public readonly SessionLifecycleService Lifecycle;

        public World()
        {
            var security = new SessionSecurityOptions();
            var tokens = new SessionTokenService();
            var support = new SessionSupport(Connections, Sessions, tokens, security);
            var limiter = new InMemoryRateLimiter();
            Negotiation = new SessionNegotiationService(support, Connections, Devices, ShortIds, Sessions,
                new InMemoryTrustedDeviceStore(), Otp, limiter, Audit, Notifier, security, TimeProvider.System);
            Lifecycle = new SessionLifecycleService(support, Connections, Devices, Sessions, tokens, new NullRelay(),
                limiter, Audit, Notifier, new SessionRecoveryOptions(), security, TimeProvider.System);
            Registration = new DeviceRegistrationService(new DeviceAuthenticator(Devices), Devices, new InMemoryPresenceService(), ShortIds,
                Connections, Otp, limiter, Audit, Lifecycle, security, TimeProvider.System);
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

    [Fact]
    public async Task RequesterDisconnecting_WithdrawsItsPendingRequest_AndTellsTheTarget()
    {
        var w = new World();
        var (aliceId, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, bob) = await w.OnlineDeviceAsync("bob");
        var dto = await w.Negotiation.RequestSessionAsync(alice, bobId.ToString());
        w.Notifier.Sent.Clear();

        await w.Lifecycle.PauseConnectedSessionsAsync(aliceId); // what the hub does when alice's connection drops

        var closed = Assert.Single(w.Notifier.Sent);
        Assert.Equal(bobId, closed.To);
        Assert.Equal("SessionClosed", closed.Event);
        Assert.Equal("requester-disconnected", closed.Args[1]);
        Assert.Equal(SessionState.Failed, (await w.Sessions.GetAsync(SessionId.Parse(dto.SessionId)))!.State);

        // Too late to approve it now: nothing can be granted for a request that no longer exists.
        await Assert.ThrowsAsync<SignalingException>(() =>
            w.Negotiation.RespondToSessionAsync(bob, dto.SessionId, true, (long)Permission.FullControl));
    }

    [Fact]
    public async Task TargetDisconnecting_DoesNotKillTheRequestersPendingRequest()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, _) = await w.OnlineDeviceAsync("bob");
        var dto = await w.Negotiation.RequestSessionAsync(alice, bobId.ToString());

        await w.Lifecycle.PauseConnectedSessionsAsync(bobId); // bob blinks offline; the request is still bob's to answer

        Assert.Equal(SessionState.Negotiating, (await w.Sessions.GetAsync(SessionId.Parse(dto.SessionId)))!.State);
    }

    // ── Short IDs ────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryDevice_GetsItsOwnNineDigitShortId_AndKeepsIt()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (_, bob) = await w.OnlineDeviceAsync("bob");

        var a = await w.Registration.ClaimShortIdAsync(alice, null);
        var b = await w.Registration.ClaimShortIdAsync(bob, null);

        Assert.Matches("^[1-9][0-9]{8}$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, await w.Registration.ClaimShortIdAsync(alice, "999999999")); // asking again changes nothing
    }

    [Fact]
    public async Task ADeviceCanKeepItsOldNumber_ButOnlyIfNobodyHasItNow()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (_, bob) = await w.OnlineDeviceAsync("bob");

        Assert.Equal("123456789", await w.Registration.ClaimShortIdAsync(alice, "123 456 789")); // free: granted (spaces are fine)
        var bobs = await w.Registration.ClaimShortIdAsync(bob, "123456789");                     // taken: a different number
        Assert.NotEqual("123456789", bobs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("012345678")]
    [InlineData("1234567890")]
    [InlineData("12345678a")]
    [InlineData("not a number")]
    public async Task ANonsensePreferredNumber_IsIgnored_NotTrusted(string preferred)
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var granted = await w.Registration.ClaimShortIdAsync(alice, preferred);
        Assert.Matches("^[1-9][0-9]{8}$", granted);
        Assert.NotEqual(preferred.Trim(), granted);
    }

    [Fact]
    public async Task ShortIds_CanOnlyBeClaimedByARegisteredConnection()
    {
        var w = new World();
        await Assert.ThrowsAsync<SignalingException>(() => w.Registration.ClaimShortIdAsync(new FakeCaller("nobody"), null));
    }

    [Fact]
    public async Task ASessionCanBeRequested_ByShortId_WithOrWithoutSeparators()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, bob) = await w.OnlineDeviceAsync("bob");
        var shortId = await w.Registration.ClaimShortIdAsync(bob, null);

        var dto = await w.Negotiation.RequestSessionAsync(alice, ShortId.Format(shortId)); // "482 913 057"
        Assert.Equal(bobId.ToString(), dto.TargetDeviceId);

        var again = await w.Negotiation.RequestSessionAsync(alice, shortId);
        Assert.Equal(bobId.ToString(), again.TargetDeviceId);
    }

    [Fact]
    public async Task AnUnknownShortId_IsRefusedExactlyLikeAnUnknownFullId()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");

        var shortMiss = await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, "123456789"));
        var fullMiss = await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, DeviceId.New().ToString()));

        Assert.Equal(fullMiss.Message, shortMiss.Message); // no way to tell which kind of guess it was
    }

    [Fact]
    public async Task GuessingShortIds_HitsTheSameLockoutAsGuessingFullIds()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");

        // The default allows 10 failed lookups per 10 minutes: the cheap attack (walking nine-digit numbers) is capped.
        var refused = 0;
        for (var i = 0; i < 25; i++)
        {
            try { await w.Negotiation.RequestSessionAsync(alice, (123456780 + i).ToString()); }
            catch (SignalingException) { refused++; }
        }
        Assert.Equal(25, refused);

        // Now even a correct short ID is refused for this requester: the lockout holds.
        var (_, bob) = await w.OnlineDeviceAsync("bob");
        var real = await w.Registration.ClaimShortIdAsync(bob, null);
        await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, real));
    }

    [Fact]
    public async Task TheOneTimeCode_CanBeUsedWithAShortId()
    {
        var w = new World();
        var (_, alice) = await w.OnlineDeviceAsync("alice");
        var (bobId, bob) = await w.OnlineDeviceAsync("bob");
        var shortId = await w.Registration.ClaimShortIdAsync(bob, null);
        var code = w.Negotiation.GenerateOtp(bob);

        var auth = await w.Negotiation.RequestSessionWithOtpAsync(alice, shortId, code);

        Assert.Equal((long)Permission.ViewOnly, auth.GrantedPermissions);
        Assert.Contains(w.Notifier.Sent, s => s.To == bobId && s.Event == "SessionEstablished");
    }

    [Fact]
    public async Task ShortIdAndFullId_ReachTheSameDevice_AndAskingForYourselfIsRefused()
    {
        var w = new World();
        var (aliceId, alice) = await w.OnlineDeviceAsync("alice");
        var mine = await w.Registration.ClaimShortIdAsync(alice, null);

        await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, mine));
        await Assert.ThrowsAsync<SignalingException>(() => w.Negotiation.RequestSessionAsync(alice, aliceId.ToString()));
    }
}

public sealed class ShortIdDirectoryTests
{
    [Fact]
    public async Task ParallelAssignments_NeverHandOutTheSameNumberTwice()
    {
        var dir = new InMemoryShortIdDirectory();
        var devices = Enumerable.Range(0, 5000).Select(_ => DeviceId.New()).ToArray();
        var numbers = new string[devices.Length];

        await Parallel.ForAsync(0, devices.Length, async (i, _) => numbers[i] = await dir.AssignAsync(devices[i], null));

        Assert.Equal(devices.Length, numbers.Distinct().Count());
        for (var i = 0; i < devices.Length; i++)
        {
            Assert.Equal(devices[i], await dir.ResolveAsync(numbers[i]));
            Assert.Equal(numbers[i], await dir.GetAsync(devices[i]));
        }
    }

    [Fact]
    public async Task ManyDevicesAskingForTheSameNumber_OnlyOneGetsIt()
    {
        var dir = new InMemoryShortIdDirectory();
        var devices = Enumerable.Range(0, 200).Select(_ => DeviceId.New()).ToArray();
        var numbers = new string[devices.Length];

        await Parallel.ForAsync(0, devices.Length, async (i, _) => numbers[i] = await dir.AssignAsync(devices[i], "555000111"));

        Assert.Single(numbers, "555000111");
        Assert.Equal(devices.Length, numbers.Distinct().Count());
    }

    [Fact]
    public async Task UnknownNumbers_ResolveToNothing()
    {
        var dir = new InMemoryShortIdDirectory();
        Assert.Null(await dir.ResolveAsync("123456789"));
        Assert.Null(await dir.GetAsync(DeviceId.New()));
    }
}
