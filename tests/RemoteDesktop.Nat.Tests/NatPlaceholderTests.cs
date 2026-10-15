using RemoteDesktop.Nat;

namespace RemoteDesktop.Nat.Tests;

public sealed class NatPlaceholderTests
{
    [Fact]
    public void INatTraversal_InterfaceExists()
    {
        Assert.NotNull(typeof(INatTraversal));
    }

    [Fact]
    public void IceCandidate_RecordExists()
    {
        var candidate = new IceCandidate(
            Foundation: "1",
            Component: 1,
            Protocol: "udp",
            Priority: 100,
            Address: "192.168.1.1",
            Port: 54321,
            CandidateType: IceCandidateType.Host);

        Assert.Equal("192.168.1.1", candidate.Address);
        Assert.Equal(IceCandidateType.Host, candidate.CandidateType);
    }
}
