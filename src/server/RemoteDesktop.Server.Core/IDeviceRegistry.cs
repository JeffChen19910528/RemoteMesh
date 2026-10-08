using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface IDeviceRegistry
{
    Task RegisterAsync(DeviceInfo device, byte[] publicKey, CancellationToken ct = default);
    Task<DeviceInfo?> FindAsync(DeviceId deviceId, CancellationToken ct = default);
    Task<byte[]?> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default);
    Task UnregisterAsync(DeviceId deviceId, CancellationToken ct = default);
}
