using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface IConnectionTracker
{
    void Register(string connectionId, DeviceId deviceId);
    void Unregister(string connectionId);
    DeviceId? GetDeviceId(string connectionId);
    string? GetConnectionId(DeviceId deviceId);
    IReadOnlyList<DeviceId> ConnectedDevices { get; }
}
