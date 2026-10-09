using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Identity;

public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<DeviceId, Entry> _entries = new();

    public Task RegisterAsync(DeviceInfo device, byte[] publicKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Registering again keeps the key history (the authenticator already insisted on the pinned key).
            _entries[device.DeviceId] = new Entry(device, publicKey, _entries.TryGetValue(device.DeviceId, out var old) ? old.Rotations : new());
        }
        return Task.CompletedTask;
    }

    public Task<DeviceInfo?> FindAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_entries.TryGetValue(deviceId, out var entry) ? entry.Device : null);
    }

    public Task<byte[]?> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_entries.TryGetValue(deviceId, out var entry) ? entry.PublicKey : null);
    }

    public Task UnregisterAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        lock (_lock) _entries.Remove(deviceId);
        return Task.CompletedTask;
    }

    public Task<bool> TryRotateKeyAsync(KeyRotation rotation, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(rotation.DeviceId, out var entry)) return Task.FromResult(false);
            if (!entry.PublicKey.AsSpan().SequenceEqual(rotation.OldKey)) return Task.FromResult(false);
            if (rotation.Sequence != entry.Rotations.Count || entry.Rotations.Count >= KeyRotation.MaxPerDevice) return Task.FromResult(false);

            entry.Rotations.Add(rotation);
            _entries[rotation.DeviceId] = entry with { PublicKey = rotation.NewKey };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<KeyRotation>> GetRotationsAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<KeyRotation>>(
                _entries.TryGetValue(deviceId, out var entry) ? entry.Rotations.ToList() : new List<KeyRotation>());
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    private sealed record Entry(DeviceInfo Device, byte[] PublicKey, List<KeyRotation> Rotations);
}
