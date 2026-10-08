namespace RemoteDesktop.Nat;

public enum IceCandidateType { Host, ServerReflexive, Relayed }

public sealed record IceCandidate(
    string Foundation,
    int Component,
    string Protocol,
    int Priority,
    string Address,
    int Port,
    IceCandidateType CandidateType);
