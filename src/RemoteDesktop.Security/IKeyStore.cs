using RemoteDesktop.Core;

namespace RemoteDesktop.Security;

public interface IKeyStore
{
    Task<DeviceKeyPair> LoadOrCreateAsync(DeviceId deviceId, CancellationToken ct = default);
    Task<byte[]> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default);
    Task DeleteAsync(DeviceId deviceId, CancellationToken ct = default);
}
