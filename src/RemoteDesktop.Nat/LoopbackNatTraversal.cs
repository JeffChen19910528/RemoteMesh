namespace RemoteDesktop.Nat;

public sealed class LoopbackNatTraversal : INatTraversal
{
    private readonly bool _directSucceeds;
    private readonly int _localPort;

    public LoopbackNatTraversal(bool directSucceeds = true, int localPort = 0)
    {
        _directSucceeds = directSucceeds;
        _localPort = localPort;
    }

    public Task<IReadOnlyList<IceCandidate>> GatherCandidatesAsync(CancellationToken ct = default)
    {
        var port = _localPort > 0 ? _localPort : Random.Shared.Next(20000, 29999);
        IReadOnlyList<IceCandidate> candidates = new List<IceCandidate>
        {
            new("1", 1, "udp", 2130706431, "127.0.0.1", port, IceCandidateType.Host)
        };
        return Task.FromResult(candidates);
    }

    public Task<bool> TryEstablishDirectAsync(
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        CancellationToken ct = default)
        => Task.FromResult(_directSucceeds);
}
