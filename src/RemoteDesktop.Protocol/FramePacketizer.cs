namespace RemoteDesktop.Protocol;

public static class FramePacketizer
{
    public const int DefaultMaxPayload = 16 * 1024;

    public static IEnumerable<byte[]> Split(long frameNumber, byte codec, bool isKeyframe,
        int width, int height, ReadOnlyMemory<byte> encoded, int maxPayload = DefaultMaxPayload)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPayload, 0);
        var count = Math.Max(1, (encoded.Length + maxPayload - 1) / maxPayload);
        if (count > FramePacket.MaxChunksPerFrame)
            throw new ArgumentException("Encoded frame too large to packetize.", nameof(encoded));

        for (var i = 0; i < count; i++)
        {
            var offset = i * maxPayload;
            var len = Math.Min(maxPayload, encoded.Length - offset);
            yield return new FramePacket
            {
                FrameNumber = frameNumber,
                Codec = codec,
                IsKeyframe = isKeyframe,
                ChunkIndex = (ushort)i,
                ChunkCount = (ushort)count,
                Width = (ushort)width,
                Height = (ushort)height,
                Payload = encoded.Slice(offset, len)
            }.ToBytes();
        }
    }
}

public sealed record ReassembledFrame(long FrameNumber, byte Codec, bool IsKeyframe,
    int Width, int Height, byte[] Data);

/// <summary>Collects chunks into frames; drops stale incomplete frames so loss never stalls the stream.</summary>
public sealed class FrameReassembler
{
    public const int DefaultMaxFrameBytes = 64 * 1024 * 1024;
    public const long DefaultMaxPendingBytes = 256L * 1024 * 1024;
    private const int MaxRememberedOversize = 64;

    private readonly int _maxPending;
    private readonly int _maxFrameBytes;
    private readonly long _maxPendingBytes;
    private readonly SortedDictionary<long, Pending> _pending = new();
    private readonly HashSet<long> _oversize = new();
    private long _pendingBytes;
    private long _lastDelivered = -1;

    /// <param name="maxFrameBytes">Maximum Frame Size: a frame whose payload grows past this is dropped.</param>
    /// <param name="maxPendingBytes">Total memory held for incomplete frames; the oldest are evicted beyond it.</param>
    public FrameReassembler(int maxPendingFrames = 8, int maxFrameBytes = DefaultMaxFrameBytes,
        long maxPendingBytes = DefaultMaxPendingBytes)
    {
        _maxPending = maxPendingFrames;
        _maxFrameBytes = maxFrameBytes;
        _maxPendingBytes = maxPendingBytes;
    }

    public long DroppedFrames { get; private set; }
    public long PendingBytes => _pendingBytes;

    public ReassembledFrame? Add(ReadOnlyMemory<byte> raw)
    {
        if (!FramePacket.TryParse(raw, out var p) || p is null) return null;
        if (p.FrameNumber <= _lastDelivered || _oversize.Contains(p.FrameNumber)) return null;

        if (!_pending.TryGetValue(p.FrameNumber, out var pending))
        {
            pending = new Pending(p);
            _pending[p.FrameNumber] = pending;
        }
        else if (pending.Chunks.Length != p.ChunkCount) return null;

        if (pending.Chunks[p.ChunkIndex] is null)
        {
            // The declared chunk count says nothing about size, so the limit is enforced on bytes actually received.
            if (pending.Bytes + p.Payload.Length > _maxFrameBytes)
            {
                Forget(p.FrameNumber, pending);
                if (_oversize.Count >= MaxRememberedOversize) _oversize.Clear();
                _oversize.Add(p.FrameNumber);
                DroppedFrames++;
                return null;
            }
            pending.Chunks[p.ChunkIndex] = p.Payload.ToArray();
            pending.Received++;
            pending.Bytes += p.Payload.Length;
            _pendingBytes += p.Payload.Length;
        }

        if (pending.Received == pending.Chunks.Length)
        {
            // Anything older that never completed is lost.
            foreach (var old in _pending.Keys.Where(k => k < p.FrameNumber).ToList())
            {
                Forget(old, _pending[old]);
                DroppedFrames++;
            }
            Forget(p.FrameNumber, pending);
            _lastDelivered = p.FrameNumber;
            return new ReassembledFrame(p.FrameNumber, p.Codec, p.IsKeyframe, p.Width, p.Height,
                Concat(pending.Chunks));
        }

        while (_pending.Count > _maxPending || (_pendingBytes > _maxPendingBytes && _pending.Count > 1))
        {
            var oldest = _pending.Keys.First();
            Forget(oldest, _pending[oldest]);
            DroppedFrames++;
        }
        return null;
    }

    private void Forget(long frameNumber, Pending pending)
    {
        if (_pending.Remove(frameNumber)) _pendingBytes -= pending.Bytes;
    }

    // One exact-size allocation and straight copies; the LINQ SelectMany this replaces enumerated byte by byte.
    private static byte[] Concat(byte[]?[] chunks)
    {
        var total = 0;
        foreach (var c in chunks) total += c!.Length;
        var result = GC.AllocateUninitializedArray<byte>(total);
        var offset = 0;
        foreach (var c in chunks)
        {
            c!.CopyTo(result, offset);
            offset += c.Length;
        }
        return result;
    }

    private sealed class Pending(FramePacket first)
    {
        public byte[]?[] Chunks { get; } = new byte[]?[first.ChunkCount];
        public int Received { get; set; }
        public long Bytes { get; set; }
    }
}
