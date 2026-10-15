using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Presence;

public sealed class InMemoryPresenceService : IPresenceService
{
    private static readonly TimeSpan HeartbeatTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<DeviceId, Entry> _entries = new();

    public Task UpdateAsync(DeviceId deviceId, PresenceStatus status, CancellationToken ct = default)
    {
        _entries.AddOrUpdate(
            deviceId,
            _ => new Entry(status, DateTimeOffset.UtcNow),
            (_, e) => e with { Status = status, LastSeen = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    public Task<PresenceStatus> GetAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(deviceId, out var entry))
            return Task.FromResult(PresenceStatus.Offline);

        // TTL check — treat as Offline if heartbeat has expired
        if (entry.Status == PresenceStatus.Online
            && DateTimeOffset.UtcNow - entry.LastSeen > HeartbeatTtl)
            return Task.FromResult(PresenceStatus.Offline);

        return Task.FromResult(entry.Status);
    }

    public Task HeartbeatAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        _entries.AddOrUpdate(
            deviceId,
            _ => new Entry(PresenceStatus.Online, DateTimeOffset.UtcNow),
            (_, e) => e with { LastSeen = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    private sealed record Entry(PresenceStatus Status, DateTimeOffset LastSeen);
}
