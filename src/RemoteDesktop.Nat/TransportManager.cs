using RemoteDesktop.Core;
using RemoteDesktop.Relay;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Nat;

public sealed class TransportManager : ITransportManager
{
    private readonly INatTraversal _nat;
    private readonly IRelayClient _relayClient;
    private readonly TimeSpan _directTimeout;
    private readonly IDirectTransportFactory? _direct;

    /// <param name="direct">Creates the real peer-to-peer transport. When null, direct connections are never used.</param>
    public TransportManager(INatTraversal nat, IRelayClient relayClient,
        TimeSpan? directTimeout = null, IDirectTransportFactory? direct = null)
    {
        _nat = nat;
        _relayClient = relayClient;
        _directTimeout = directTimeout ?? TimeSpan.FromSeconds(5);
        _direct = direct;
    }

    public async Task<ITransport> ConnectAsync(
        SessionId sessionId,
        string sessionToken,
        DeviceId localDeviceId,
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        Uri relayUri,
        CancellationToken ct = default)
    {
        // Try direct connection first (ICE connectivity checks).
        try
        {
            using var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            directCts.CancelAfter(_directTimeout);

            var isDirect = await _nat.TryEstablishDirectAsync(
                localCandidates, remoteCandidates, directCts.Token);

            // A direct path counts only if there is a real transport to carry it. ICE success without a factory
            // falls through to the relay instead of fabricating a transport that goes nowhere.
            if (isDirect && _direct is not null)
                return await _direct.CreateAsync(sessionId, localCandidates, remoteCandidates, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // direct attempt timed out
        catch (OperationCanceledException) { throw; }                            // the caller gave up
        catch (Exception)
        {
            // Any failure setting up the direct path must not break connectivity: the relay always works.
        }

        // Relay fallback: always works regardless of NAT type.
        await _relayClient.ConnectAsync(relayUri, sessionId, sessionToken, localDeviceId, ct);
        return await _relayClient.GetTransportAsync(ct);
    }
}
