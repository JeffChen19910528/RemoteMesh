using RemoteDesktop.Core;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Nat;

public interface ITransportManager
{
    Task<ITransport> ConnectAsync(
        SessionId sessionId,
        string sessionToken,
        DeviceId localDeviceId,
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        Uri relayUri,
        CancellationToken ct = default);
}
