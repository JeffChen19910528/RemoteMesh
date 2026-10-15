using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Infrastructure;

public sealed class InMemoryConnectionTracker : IConnectionTracker
{
    private readonly ConcurrentDictionary<string, DeviceId> _connToDevice = new();
    private readonly ConcurrentDictionary<DeviceId, string> _deviceToConn = new();

    public void Register(string connectionId, DeviceId deviceId)
    {
        // Remove stale mapping for this device if it reconnects
        if (_deviceToConn.TryGetValue(deviceId, out var oldConn) && oldConn != connectionId)
            _connToDevice.TryRemove(oldConn, out _);

        _connToDevice[connectionId] = deviceId;
        _deviceToConn[deviceId] = connectionId;
    }

    public void Unregister(string connectionId)
    {
        if (_connToDevice.TryRemove(connectionId, out var deviceId))
            _deviceToConn.TryRemove(deviceId, out _);
    }

    public DeviceId? GetDeviceId(string connectionId) =>
        _connToDevice.TryGetValue(connectionId, out var id) ? id : null;

    public string? GetConnectionId(DeviceId deviceId) =>
        _deviceToConn.TryGetValue(deviceId, out var connId) ? connId : null;

    public IReadOnlyList<DeviceId> ConnectedDevices =>
        [.. _deviceToConn.Keys];
}
