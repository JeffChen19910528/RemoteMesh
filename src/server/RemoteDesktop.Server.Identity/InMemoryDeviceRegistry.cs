using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Identity;

public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<DeviceId, Entry> _entries = new();

    public Task RegisterAsync(DeviceInfo device, byte[] publicKey, CancellationToken ct = default)
    {
        _entries[device.DeviceId] = new Entry(device, publicKey);
        return Task.CompletedTask;
    }

    public Task<DeviceInfo?> FindAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        _entries.TryGetValue(deviceId, out var entry);
        return Task.FromResult(entry?.Device);
    }

    public Task<byte[]?> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        _entries.TryGetValue(deviceId, out var entry);
        return Task.FromResult(entry?.PublicKey);
    }

    public Task UnregisterAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        _entries.TryRemove(deviceId, out _);
        return Task.CompletedTask;
    }

    public int Count => _entries.Count;

    private sealed record Entry(DeviceInfo Device, byte[] PublicKey);
}
