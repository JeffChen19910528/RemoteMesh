using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface IDeviceRegistry
{
    Task RegisterAsync(DeviceInfo device, byte[] publicKey, CancellationToken ct = default);
    Task<DeviceInfo?> FindAsync(DeviceId deviceId, CancellationToken ct = default);
    Task<byte[]?> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default);
    Task UnregisterAsync(DeviceId deviceId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the device's key with <see cref="KeyRotation.NewKey"/> and records the step, in one atomic action,
    /// only if its current key is <see cref="KeyRotation.OldKey"/> and the sequence is the next free one. False
    /// otherwise (unknown device, key already changed, too many rotations, a racing rotation won).
    /// </summary>
    Task<bool> TryRotateKeyAsync(KeyRotation rotation, CancellationToken ct = default);

    /// <summary>The device's key history, oldest first.</summary>
    Task<IReadOnlyList<KeyRotation>> GetRotationsAsync(DeviceId deviceId, CancellationToken ct = default);
}
