namespace RemoteDesktop.Nat;

public sealed class NatTraversalConfig
{
    public string StunServerUri { get; init; } = "stun:stun.l.google.com:19302";
    public TimeSpan DirectConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CandidateGatheringTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
