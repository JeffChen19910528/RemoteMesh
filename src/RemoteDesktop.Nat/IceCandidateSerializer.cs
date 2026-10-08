using System.Text.Json;

namespace RemoteDesktop.Nat;

public static class IceCandidateSerializer
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    public static string Serialize(IceCandidate candidate) =>
        JsonSerializer.Serialize(candidate, _opts);

    public static IceCandidate? Deserialize(string json) =>
        JsonSerializer.Deserialize<IceCandidate>(json, _opts);

    public static string SerializeList(IReadOnlyList<IceCandidate> candidates) =>
        JsonSerializer.Serialize(candidates, _opts);

    public static IReadOnlyList<IceCandidate> DeserializeList(string json) =>
        JsonSerializer.Deserialize<List<IceCandidate>>(json, _opts)
        ?? new List<IceCandidate>();
}
