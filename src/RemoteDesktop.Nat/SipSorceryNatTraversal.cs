using SIPSorcery.Net;

namespace RemoteDesktop.Nat;

// Production ICE/STUN implementation using SIPSorcery.
// Requires external STUN server access — not exercised in unit tests (use LoopbackNatTraversal).
// Full ICE connectivity checks (TryEstablishDirectAsync) not implemented yet.
public sealed class SipSorceryNatTraversal : INatTraversal, IDisposable
{
    private readonly NatTraversalConfig _config;
    private RTCPeerConnection? _pc;

    public SipSorceryNatTraversal(NatTraversalConfig? config = null)
        => _config = config ?? new NatTraversalConfig();

    public async Task<IReadOnlyList<IceCandidate>> GatherCandidatesAsync(CancellationToken ct = default)
    {
        var gathered = new List<IceCandidate>();
        var gatherDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var iceConfig = new RTCConfiguration();
        if (!string.IsNullOrEmpty(_config.StunServerUri))
        {
            iceConfig.iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = _config.StunServerUri }
            };
        }

        _pc = new RTCPeerConnection(iceConfig);

        _pc.onicegatheringstatechange += state =>
        {
            if (state == RTCIceGatheringState.complete)
                gatherDone.TrySetResult(true);
        };

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            gathered.Add(new IceCandidate(
                Foundation: candidate.foundation ?? "1",
                Component: (int)candidate.component,
                Protocol: candidate.protocol.ToString().ToLower(),
                Priority: unchecked((int)candidate.priority),
                Address: candidate.address ?? "0.0.0.0",
                Port: candidate.port,
                CandidateType: candidate.type switch
                {
                    RTCIceCandidateType.srflx => IceCandidateType.ServerReflexive,
                    RTCIceCandidateType.relay => IceCandidateType.Relayed,
                    _ => IceCandidateType.Host
                }));
        };

        // Data channel triggers ICE gathering without requiring media codecs.
#pragma warning disable CS4014
        _pc.createDataChannel("natcheck");
#pragma warning restore CS4014
        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(_config.CandidateGatheringTimeout);

        try { await gatherDone.Task.WaitAsync(linkedCts.Token); }
        catch (OperationCanceledException) { }

        return gathered;
    }

    // Not implemented yet: full SIPSorcery ICE connectivity checks belong here.
    // Requires SDP exchange with the remote peer via the signaling layer.
    public Task<bool> TryEstablishDirectAsync(
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        CancellationToken ct = default)
        => Task.FromResult(false);

    public void Dispose() => _pc?.Dispose();
}
