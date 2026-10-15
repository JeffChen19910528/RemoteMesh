using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

/// <summary>
/// Hands out and resolves short IDs. A short ID is assigned by the server (random, so nobody can compute the one
/// they want) and points at exactly one device.
/// </summary>
public interface IShortIdDirectory
{
    /// <summary>
    /// The device's short ID: the one it already has; otherwise <paramref name="preferred"/> if that number is free
    /// (so a device keeps its number across reconnects); otherwise a new random free one.
    /// </summary>
    Task<string> AssignAsync(DeviceId device, string? preferred, CancellationToken ct = default);

    /// <summary>The device a short ID belongs to, or null.</summary>
    Task<DeviceId?> ResolveAsync(string shortId, CancellationToken ct = default);

    Task<string?> GetAsync(DeviceId device, CancellationToken ct = default);
}
