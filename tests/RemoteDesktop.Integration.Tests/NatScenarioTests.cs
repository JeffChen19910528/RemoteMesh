using RemoteDesktop.Core;
using RemoteDesktop.Nat;
using RemoteDesktop.Relay;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Integration.Tests;

// NAT scenario simulations using LoopbackNatTraversal and a fake relay client.
// These verify the TransportManager selects the correct transport mode for each NAT scenario.
public sealed class NatScenarioTests
{
    private static readonly Uri FakeRelayUri = new("ws://localhost/ws/relay");

    // ── Scenarios where direct connection succeeds ────────────────────────

    [Fact]
    public async Task OpenNat_BothHostCandidates_UsesDirectTransport()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new LoopbackDirectTransportFactory());

        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(), local, remote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Direct, transport.Mode);
        Assert.False(relay.IsConnected);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task FullConeNat_SrflxCandidates_DirectSucceeds_UsesDirectTransport()
    {
        var sessionId = SessionId.New();
        var srflxNat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(srflxNat, relay, direct: new LoopbackDirectTransportFactory());

        // Simulate server-reflexive candidates
        var srflxLocal = new List<IceCandidate>
        {
            new("1", 1, "udp", 1694498815, "203.0.113.10", 54321, IceCandidateType.ServerReflexive)
        };
        var srflxRemote = new List<IceCandidate>
        {
            new("2", 1, "udp", 1694498814, "198.51.100.5", 40000, IceCandidateType.ServerReflexive)
        };

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(), srflxLocal, srflxRemote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Direct, transport.Mode);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task RestrictedNat_MixedCandidates_AtLeastOnePairSucceeds_UsesDirect()
    {
        var sessionId = SessionId.New();
        // Restricted NAT: host candidate works but reflexive may not — direct still possible
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new LoopbackDirectTransportFactory());

        var local = new List<IceCandidate>
        {
            new("h1", 1, "udp", 2130706431, "192.168.1.5", 40000, IceCandidateType.Host),
            new("s1", 1, "udp", 1694498815, "203.0.113.5", 40001, IceCandidateType.ServerReflexive)
        };
        var remote = new List<IceCandidate>
        {
            new("h2", 1, "udp", 2130706430, "10.0.0.2", 50000, IceCandidateType.Host),
            new("s2", 1, "udp", 1694498814, "198.51.100.3", 50001, IceCandidateType.ServerReflexive)
        };

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(), local, remote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Direct, transport.Mode);
        await transport.DisposeAsync();
    }

    // ── Scenarios where direct connection fails — relay fallback ──────────

    [Fact]
    public async Task SymmetricNat_AllDirectAttemptsFail_FallsBackToRelay()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: false);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new LoopbackDirectTransportFactory());

        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(), local, remote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Relay, transport.Mode);
        Assert.True(relay.IsConnected);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task RelayFallback_DirectTimesOut_FallsBackToRelay()
    {
        var sessionId = SessionId.New();
        // SlowDirectNat always times out (will be cancelled by the direct timeout)
        var nat = new SlowDirectNatTraversal(delay: TimeSpan.FromSeconds(30));
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, directTimeout: TimeSpan.FromMilliseconds(50), direct: new LoopbackDirectTransportFactory());

        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(), local, remote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Relay, transport.Mode);
        Assert.True(relay.IsConnected);
        await transport.DisposeAsync();
    }

    // ── Transport mode on session ─────────────────────────────────────────

    [Fact]
    public async Task DirectTransport_HasCorrectSessionId()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new LoopbackDirectTransportFactory());

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(),
            await nat.GatherCandidatesAsync(), await nat.GatherCandidatesAsync(), FakeRelayUri);

        Assert.Equal(sessionId, transport.SessionId);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task RelayTransport_HasCorrectSessionId()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: false);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new LoopbackDirectTransportFactory());

        var transport = await manager.ConnectAsync(
            sessionId, "tok", DeviceId.New(),
            await nat.GatherCandidatesAsync(), await nat.GatherCandidatesAsync(), FakeRelayUri);

        Assert.Equal(sessionId, transport.SessionId);
        await transport.DisposeAsync();
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class FakeRelayClient : IRelayClient
    {
        private readonly SessionId _sessionId;
        public bool IsConnected { get; private set; }

        public FakeRelayClient(SessionId sessionId) => _sessionId = sessionId;

        public Task ConnectAsync(Uri relayUri, SessionId sessionId, string sessionToken,
            DeviceId deviceId, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<ITransport> GetTransportAsync(CancellationToken ct = default) =>
            Task.FromResult<ITransport>(new LoopbackTransport(_sessionId, ConnectionMode.Relay));

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    // ── Safety: a direct path counts only if a real transport exists ─────

    [Fact]
    public async Task IceSucceeds_ButNoDirectTransportExists_UsesTheRelay_NeverAFakeTransport()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay); // no IDirectTransportFactory

        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();
        var transport = await manager.ConnectAsync(sessionId, "tok", DeviceId.New(), local, remote, FakeRelayUri);

        Assert.Equal(ConnectionMode.Relay, transport.Mode);
        Assert.True(relay.IsConnected);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ProductionTraversal_DoesNotClaimDirectConnections_UntilIceChecksAreImplemented()
    {
        // Documents the current limit: candidates can be gathered but connectivity checks are not implemented,
        // so the production traversal must never report a working direct path.
        using var nat = new SipSorceryNatTraversal();
        var candidate = new IceCandidate("1", 1, "udp", 1, "192.0.2.1", 4000, IceCandidateType.Host);

        Assert.False(await nat.TryEstablishDirectAsync([candidate], [candidate]));
    }

    [Fact]
    public async Task DirectTransportFactoryFailure_FallsBackToTheRelay()
    {
        var sessionId = SessionId.New();
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(nat, relay, direct: new ThrowingFactory());
        var c = await nat.GatherCandidatesAsync();

        var transport = await manager.ConnectAsync(sessionId, "tok", DeviceId.New(), c, c, FakeRelayUri);

        Assert.Equal(ConnectionMode.Relay, transport.Mode); // a broken direct path never breaks connectivity
        Assert.True(relay.IsConnected);
    }

    [Fact]
    public async Task CallerCancellation_StopsConnecting_InsteadOfFallingBack()
    {
        var sessionId = SessionId.New();
        var relay = new FakeRelayClient(sessionId);
        var manager = new TransportManager(new SlowDirectNatTraversal(TimeSpan.FromSeconds(30)), relay,
            directTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var c = new List<IceCandidate> { new("1", 1, "udp", 1, "127.0.0.1", 1, IceCandidateType.Host) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ConnectAsync(sessionId, "tok", DeviceId.New(), c, c, FakeRelayUri, cts.Token));
        Assert.False(relay.IsConnected);
    }

    private sealed class ThrowingFactory : IDirectTransportFactory
    {
        public Task<ITransport> CreateAsync(SessionId sessionId, IReadOnlyList<IceCandidate> local,
            IReadOnlyList<IceCandidate> remote, CancellationToken ct = default) =>
            throw new InvalidOperationException("direct transport failed");
    }

    // NAT traversal that simulates a long-running direct check (always times out).
    private sealed class SlowDirectNatTraversal : INatTraversal
    {
        private readonly TimeSpan _delay;
        public SlowDirectNatTraversal(TimeSpan delay) => _delay = delay;

        public Task<IReadOnlyList<IceCandidate>> GatherCandidatesAsync(CancellationToken ct = default)
        {
            IReadOnlyList<IceCandidate> list = new List<IceCandidate>
            {
                new("1", 1, "udp", 2130706431, "127.0.0.1", 20000, IceCandidateType.Host)
            };
            return Task.FromResult(list);
        }

        public async Task<bool> TryEstablishDirectAsync(
            IReadOnlyList<IceCandidate> localCandidates,
            IReadOnlyList<IceCandidate> remoteCandidates,
            CancellationToken ct = default)
        {
            await Task.Delay(_delay, ct); // cancelled by TransportManager's timeout
            return false;
        }
    }
}
