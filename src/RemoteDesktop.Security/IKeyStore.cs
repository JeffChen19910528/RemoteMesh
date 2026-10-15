using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

public interface IKeyStore
{
    Task<DeviceKeyPair> LoadOrCreateAsync(DeviceId deviceId, CancellationToken ct = default);
    Task<byte[]> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default);
    Task DeleteAsync(DeviceId deviceId, CancellationToken ct = default);

    // A key change is staged first, then committed once the server accepted it, so a crash or a lost connection in
    // between can never leave the device holding a key the server does not know (or the reverse).

    /// <summary>Saves a new key pair next to the current one, without replacing it yet.</summary>
    Task StagePendingAsync(DeviceId deviceId, DeviceKeyPair next, CancellationToken ct = default);

    /// <summary>The staged key pair, or null when none is waiting.</summary>
    Task<DeviceKeyPair?> LoadPendingAsync(DeviceId deviceId, CancellationToken ct = default);

    /// <summary>The staged key pair becomes the device's key.</summary>
    Task CommitPendingAsync(DeviceId deviceId, CancellationToken ct = default);

    /// <summary>Forgets the staged key pair (the server did not take it).</summary>
    Task DiscardPendingAsync(DeviceId deviceId, CancellationToken ct = default);
}
