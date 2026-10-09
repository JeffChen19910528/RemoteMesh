using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Client;

/// <summary>One device this computer refuses to be controlled by.</summary>
public sealed record BlockedPeer(DeviceId Device, string? ShortId, DateTimeOffset Since);

/// <summary>
/// The devices this computer will not let in, kept on disk. A blocked device is declined without a prompt, and it
/// stays refused even if the server says it is trusted or it knows a one-time code: the decision is made here, on the
/// computer being controlled, where nobody else can overrule it. (There is no account behind a device, so nobody else
/// can speak for a computer; this is how its owner revokes a device for good.)
/// </summary>
public sealed class PeerBlockList
{
    public const int MaxEntries = 10_000;

    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <param name="path">The file to keep the list in; null keeps it in memory only.</param>
    public PeerBlockList(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            if (loaded is null) return;
            foreach (var (k, v) in loaded.Take(MaxEntries))
                if (DeviceId.TryParse(k, out _) && v is not null) _entries[k] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: start empty. (A damaged list loses blocks; the file is rewritten at the next change.)
        }
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    public bool IsBlocked(DeviceId device)
    {
        lock (_lock) return _entries.ContainsKey(device.ToString());
    }

    /// <summary>Adds the device. False if it was already blocked, or the list is full.</summary>
    public bool Block(DeviceId device, string? shortId = null)
    {
        lock (_lock)
        {
            var key = device.ToString();
            if (_entries.ContainsKey(key) || _entries.Count >= MaxEntries) return false;
            _entries[key] = new Entry { ShortId = shortId, Since = DateTimeOffset.UtcNow };
            Save();
            return true;
        }
    }

    public bool Unblock(DeviceId device)
    {
        lock (_lock)
        {
            var removed = _entries.Remove(device.ToString());
            if (removed) Save();
            return removed;
        }
    }

    public IReadOnlyList<BlockedPeer> List()
    {
        lock (_lock)
            return _entries.Select(e => new BlockedPeer(DeviceId.Parse(e.Key), e.Value.ShortId, e.Value.Since))
                .OrderBy(b => b.Since).ToList();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still blocked for this run; the next start may not remember it.
        }
    }

    private sealed class Entry
    {
        public string? ShortId { get; set; }
        public DateTimeOffset Since { get; set; }
    }
}
