using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Identity;

public sealed class InMemoryShortIdDirectory : IShortIdDirectory
{
    /// <summary>More than this many numbers handed out means something is wrong (or the server needs a real database).</summary>
    public const int MaxEntries = 1_000_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceId> _byShort = new();
    private readonly Dictionary<DeviceId, string> _byDevice = new();

    public Task<string> AssignAsync(DeviceId device, string? preferred, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_byDevice.TryGetValue(device, out var existing)) return Task.FromResult(existing);
            if (_byDevice.Count >= MaxEntries) throw new InvalidOperationException("No short IDs left.");

            string chosen;
            if (ShortId.TryNormalize(preferred, out var wanted) && !_byShort.ContainsKey(wanted))
            {
                chosen = wanted;
            }
            else
            {
                // 900 million numbers and at most a million taken: a free one is found at once.
                do chosen = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
                while (_byShort.ContainsKey(chosen));
            }

            _byShort[chosen] = device;
            _byDevice[device] = chosen;
            return Task.FromResult(chosen);
        }
    }

    public Task<DeviceId?> ResolveAsync(string shortId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<DeviceId?>(_byShort.TryGetValue(shortId, out var id) ? id : null);
    }

    public Task<string?> GetAsync(DeviceId device, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_byDevice.TryGetValue(device, out var s) ? s : null);
    }
}
