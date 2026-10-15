using RemoteDesktop.Core;

namespace RemoteDesktop.Nat;

public interface INatTraversal
{
    Task<IReadOnlyList<IceCandidate>> GatherCandidatesAsync(CancellationToken ct = default);
    Task<bool> TryEstablishDirectAsync(
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        CancellationToken ct = default);
}
