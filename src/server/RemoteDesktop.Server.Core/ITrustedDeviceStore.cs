using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface ITrustedDeviceStore
{
    Task AddAsync(TrustedDevice entry, CancellationToken ct = default);
    Task RemoveAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default);
    Task<bool> IsTrustedAsync(DeviceId owner, DeviceId requester, CancellationToken ct = default);
    Task<TrustedDevice?> GetAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default);
    Task<IReadOnlyList<TrustedDevice>> ListAsync(DeviceId owner, CancellationToken ct = default);
}
