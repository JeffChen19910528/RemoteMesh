using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Identity;

public sealed class InMemoryTrustedDeviceStore : ITrustedDeviceStore
{
    private readonly ConcurrentDictionary<(DeviceId Owner, DeviceId Trusted), TrustedDevice> _store = new();

    public Task AddAsync(TrustedDevice entry, CancellationToken ct = default)
    {
        _store[(entry.OwnerDeviceId, entry.TrustedDeviceId)] = entry;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default)
    {
        _store.TryRemove((owner, trusted), out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsTrustedAsync(DeviceId owner, DeviceId requester, CancellationToken ct = default)
        => Task.FromResult(_store.ContainsKey((owner, requester)));

    public Task<TrustedDevice?> GetAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default)
    {
        _store.TryGetValue((owner, trusted), out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<TrustedDevice>> ListAsync(DeviceId owner, CancellationToken ct = default)
    {
        var list = _store.Values
            .Where(e => e.OwnerDeviceId == owner)
            .ToList();
        return Task.FromResult<IReadOnlyList<TrustedDevice>>(list);
    }
}
