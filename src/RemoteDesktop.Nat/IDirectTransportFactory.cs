using RemoteDesktop.Core;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Nat;

/// <summary>
/// Builds the real peer-to-peer transport once ICE has found a working candidate pair. Without a factory the
/// <see cref="TransportManager"/> never claims a direct connection — it uses the relay — so a missing or
/// half-finished direct path can never hand the application a transport that carries no data.
/// </summary>
public interface IDirectTransportFactory
{
    Task<ITransport> CreateAsync(SessionId sessionId, IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates, CancellationToken ct = default);
}

/// <summary>SIMULATION ONLY: in-memory "direct" transport for NAT scenario tests. Never register it in a product build.</summary>
public sealed class LoopbackDirectTransportFactory : IDirectTransportFactory
{
    public Task<ITransport> CreateAsync(SessionId sessionId, IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates, CancellationToken ct = default) =>
        Task.FromResult<ITransport>(new LoopbackTransport(sessionId, ConnectionMode.Direct));
}
