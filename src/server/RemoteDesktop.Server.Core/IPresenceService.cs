using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface IPresenceService
{
    Task UpdateAsync(DeviceId deviceId, PresenceStatus status, CancellationToken ct = default);
    Task<PresenceStatus> GetAsync(DeviceId deviceId, CancellationToken ct = default);
    Task HeartbeatAsync(DeviceId deviceId, CancellationToken ct = default);
}
