using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;

namespace RemoteDesktop.Client;

public enum PeerPinResult
{
    /// <summary>Never seen this device before; its key is remembered from now on.</summary>
    NewPeer,
    /// <summary>Seen before, same key.</summary>
    Known,
    /// <summary>Seen before with a DIFFERENT key. The session must not go ahead.</summary>
    KeyChanged
}

/// <summary>
/// The keys of the devices this one has talked to, by device ID, kept on disk (trust on first use). The server is
/// asked for a peer's key every time, so without this a compromised server could hand out its own key the next time
/// and sit in the middle; with it, a changed key is noticed and the session refused. Entries can also be marked
/// as verified once the two people have compared the security code.
/// </summary>
public sealed class PeerPinStore
{
    /// <summary>A flood of one-off peers cannot grow the file without bound.</summary>
    public const int MaxEntries = 10_000;

    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <param name="path">The file to keep the pins in; null keeps them in memory only.</param>
    public PeerPinStore(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (loaded is not null)
                    foreach (var (k, v) in loaded.Take(MaxEntries))
                        if (v?.KeyHash is { Length: 64 }) _entries[k] = v;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable: start empty. Peers are then seen as new again (and pinned again); nothing is trusted more.
            }
        }
    }

    public PeerPinResult Check(DeviceId peer, byte[] publicKey)
    {
        var hash = Convert.ToHexString(KeyFingerprint.Of(publicKey));
        lock (_lock)
        {
            if (_entries.TryGetValue(peer.ToString(), out var known))
                return CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(known.KeyHash), System.Text.Encoding.ASCII.GetBytes(hash))
                    ? PeerPinResult.Known : PeerPinResult.KeyChanged;

            if (_entries.Count >= MaxEntries)
            {
                // Make room by dropping the oldest unverified entry; verified ones are kept.
                var oldest = _entries.Where(e => !e.Value.Verified).OrderBy(e => e.Value.FirstSeen).Select(e => e.Key).FirstOrDefault();
                if (oldest is null) return PeerPinResult.NewPeer; // full of verified peers: connect, but do not remember
                _entries.Remove(oldest);
            }
            _entries[peer.ToString()] = new Entry { KeyHash = hash, FirstSeen = DateTimeOffset.UtcNow };
            Save();
            return PeerPinResult.NewPeer;
        }
    }

    /// <summary>True once the person has compared the security code with the other person's and found it equal.</summary>
    public bool IsVerified(DeviceId peer, byte[] publicKey)
    {
        var hash = Convert.ToHexString(KeyFingerprint.Of(publicKey));
        lock (_lock) return _entries.TryGetValue(peer.ToString(), out var e) && e.Verified && e.KeyHash == hash;
    }

    /// <summary>Marks the pinned key of this peer as checked by the person. Does nothing if the key is not the pinned one.</summary>
    public bool MarkVerified(DeviceId peer, byte[] publicKey)
    {
        var hash = Convert.ToHexString(KeyFingerprint.Of(publicKey));
        lock (_lock)
        {
            if (!_entries.TryGetValue(peer.ToString(), out var e) || e.KeyHash != hash) return false;
            e.Verified = true;
            Save();
            return true;
        }
    }

    /// <summary>
    /// A peer presents another key than the pinned one. If the server's key history leads from the pinned key to
    /// the presented one, every step signed by both keys of that step, the device itself moved to the new key: the
    /// pin follows (and a verified peer stays verified, because it is cryptographically the same device). A history
    /// the server made up cannot pass: it would need a signature by a key the server does not have.
    /// </summary>
    public bool TryFollowRotations(DeviceId peer, byte[] presentedKey, IReadOnlyList<KeyRotation> history)
    {
        if (history.Count is 0 or > KeyRotation.MaxPerDevice) return false;
        var presentedHash = Convert.ToHexString(KeyFingerprint.Of(presentedKey));
        lock (_lock)
        {
            if (!_entries.TryGetValue(peer.ToString(), out var pinned)) return false;
            var steps = history.OrderBy(r => r.Sequence).ToList();
            var start = steps.FindIndex(r => Convert.ToHexString(KeyFingerprint.Of(r.OldKey)) == pinned.KeyHash);
            if (start < 0) return false;

            var current = steps[start].OldKey;
            for (var i = start; i < steps.Count; i++)
            {
                var step = steps[i];
                if (!step.OldKey.AsSpan().SequenceEqual(current)) return false;
                var statement = KeyRotationProof.BuildPayload(peer.ToString(), step.OldKey, step.NewKey);
                if (!KeyChecks.IsValidP256PublicKey(step.NewKey)
                    || !KeyChecks.Verifies(step.OldKey, statement, step.OldKeySignature)
                    || !KeyChecks.Verifies(step.NewKey, statement, step.NewKeySignature)) return false;
                current = step.NewKey;
            }
            if (!current.AsSpan().SequenceEqual(presentedKey)) return false;

            pinned.KeyHash = presentedHash;
            Save();
            return true;
        }
    }

    /// <summary>Forgets a peer, for example after the person decided that its new key is legitimate.</summary>
    public bool Forget(DeviceId peer)
    {
        lock (_lock)
        {
            var removed = _entries.Remove(peer.ToString());
            if (removed) Save();
            return removed;
        }
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _path, overwrite: true); // a crash leaves the old file or the new one, never half of each
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pins that cannot be saved still protect this run; the next start just sees the peer as new again.
        }
    }

    private sealed class Entry
    {
        public string KeyHash { get; set; } = "";
        public bool Verified { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
    }
}

/// <summary>The server presented a different key for a device than the one this device saw before.</summary>
public sealed class PeerKeyChangedException(DeviceId peer)
    : InvalidOperationException("The other device's security key has changed since the last connection. The connection was stopped.")
{
    public DeviceId Peer { get; } = peer;
}
