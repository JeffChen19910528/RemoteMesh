using RemoteDesktop.Nat;

namespace RemoteDesktop.Nat.Tests;

public sealed class NatTraversalTests
{
    // ── IceCandidateSerializer ────────────────────────────────────────────

    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var original = new IceCandidate("abc1", 1, "udp", 12345, "192.168.1.50", 54321, IceCandidateType.Host);

        var json = IceCandidateSerializer.Serialize(original);
        var restored = IceCandidateSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(original.Foundation, restored!.Foundation);
        Assert.Equal(original.Address, restored.Address);
        Assert.Equal(original.Port, restored.Port);
        Assert.Equal(original.CandidateType, restored.CandidateType);
        Assert.Equal(original.Priority, restored.Priority);
    }

    [Fact]
    public void SerializeList_DeserializeList_RoundTrip()
    {
        var candidates = new List<IceCandidate>
        {
            new("1", 1, "udp", 2130706431, "192.168.0.1", 40000, IceCandidateType.Host),
            new("2", 1, "udp", 1694498815, "203.0.113.5", 40001, IceCandidateType.ServerReflexive)
        };

        var json = IceCandidateSerializer.SerializeList(candidates);
        var restored = IceCandidateSerializer.DeserializeList(json);

        Assert.Equal(2, restored.Count);
        Assert.Equal("192.168.0.1", restored[0].Address);
        Assert.Equal("203.0.113.5", restored[1].Address);
        Assert.Equal(IceCandidateType.ServerReflexive, restored[1].CandidateType);
    }

    [Fact]
    public void DeserializeList_EmptyJson_ReturnsEmpty()
    {
        var result = IceCandidateSerializer.DeserializeList("[]");
        Assert.Empty(result);
    }

    // ── LoopbackNatTraversal ──────────────────────────────────────────────

    [Fact]
    public async Task GatherCandidates_ReturnsLocalhostHostCandidate()
    {
        var nat = new LoopbackNatTraversal();

        var candidates = await nat.GatherCandidatesAsync();

        Assert.Single(candidates);
        Assert.Equal("127.0.0.1", candidates[0].Address);
        Assert.Equal(IceCandidateType.Host, candidates[0].CandidateType);
        Assert.Equal("udp", candidates[0].Protocol);
        Assert.True(candidates[0].Port is >= 20000 and <= 29999);
    }

    [Fact]
    public async Task GatherCandidates_SpecificPort_UsesGivenPort()
    {
        var nat = new LoopbackNatTraversal(localPort: 25000);

        var candidates = await nat.GatherCandidatesAsync();

        Assert.Equal(25000, candidates[0].Port);
    }

    [Fact]
    public async Task TryEstablishDirect_WhenConfiguredToSucceed_ReturnsTrue()
    {
        var nat = new LoopbackNatTraversal(directSucceeds: true);
        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();

        var result = await nat.TryEstablishDirectAsync(local, remote);

        Assert.True(result);
    }

    [Fact]
    public async Task TryEstablishDirect_WhenConfiguredToFail_ReturnsFalse()
    {
        var nat = new LoopbackNatTraversal(directSucceeds: false);
        var local = await nat.GatherCandidatesAsync();
        var remote = await nat.GatherCandidatesAsync();

        var result = await nat.TryEstablishDirectAsync(local, remote);

        Assert.False(result);
    }

    // ── IceCandidate priority ordering ───────────────────────────────────

    [Fact]
    public void IceCandidate_HostPriority_GreaterThanSrflx()
    {
        // ICE RFC 8445: host candidates have highest priority (type preference 126),
        // server-reflexive 100, relay 0.
        const int hostPriority = 2130706431;    // (126 << 24) | (65535 << 8) | 255
        const int srflxPriority = 1694498815;   // (100 << 24) | (65535 << 8) | 255

        var host = new IceCandidate("1", 1, "udp", hostPriority, "192.168.0.1", 1234, IceCandidateType.Host);
        var srflx = new IceCandidate("2", 1, "udp", srflxPriority, "203.0.113.1", 5678, IceCandidateType.ServerReflexive);

        Assert.True(host.Priority > srflx.Priority);
    }
}
