namespace RemoteDesktop.Transport.Secure;

/// <summary>
/// 64-entry sliding anti-replay window (as in IPsec/DTLS). A sequence number is accepted at most once, and
/// numbers older than the window are rejected. <see cref="CanAccept"/> and <see cref="Commit"/> are separate so a
/// number is only recorded after the frame authenticated — otherwise a forged frame could burn a real number.
/// </summary>
public sealed class ReplayWindow
{
    private const int Size = 64;
    private readonly object _lock = new();
    private long _highest;
    private ulong _seen; // bit i set => sequence (_highest - i) was accepted

    public bool CanAccept(long seq)
    {
        lock (_lock)
        {
            if (seq <= 0) return false;
            if (seq > _highest) return true;
            var offset = _highest - seq;
            return offset < Size && (_seen & (1UL << (int)offset)) == 0;
        }
    }

    /// <returns>false if the number was already accepted or is outside the window (lost a race with another frame).</returns>
    public bool Commit(long seq)
    {
        lock (_lock)
        {
            if (seq <= 0) return false;
            if (seq > _highest)
            {
                var shift = seq - _highest;
                _seen = shift >= Size ? 0 : _seen << (int)shift;
                _seen |= 1;
                _highest = seq;
                return true;
            }
            var offset = _highest - seq;
            if (offset >= Size) return false;
            var bit = 1UL << (int)offset;
            if ((_seen & bit) != 0) return false;
            _seen |= bit;
            return true;
        }
    }
}
