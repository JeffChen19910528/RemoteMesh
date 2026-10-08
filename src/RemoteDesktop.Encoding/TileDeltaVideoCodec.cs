using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Encoding;

/// <summary>A delta frame cannot be applied: no reference frame, or a frame in the chain was lost.</summary>
public sealed class KeyframeRequiredException(string message) : Exception(message);

/// <summary>
/// Wire format shared by the tile-delta encoder and decoder:
/// <c>[width:2][height:2][flags:1][seq:4][tileCount:2][tileIndex:2 × tileCount][deflate(tile pixels…)]</c>.
/// The screen is cut into 64×64 tiles; a keyframe lists every tile, a delta frame only the tiles that changed.
/// </summary>
internal static class TileLayout
{
    public const int TileSize = 64;
    public const int HeaderSize = 2 + 2 + 1 + 4 + 2;
    public const byte KeyframeFlag = 1;

    public static int TilesX(int width) => (width + TileSize - 1) / TileSize;
    public static int TilesY(int height) => (height + TileSize - 1) / TileSize;
    public static int TileWidth(int width, int tileX) => Math.Min(TileSize, width - tileX * TileSize);
    public static int TileHeight(int height, int tileY) => Math.Min(TileSize, height - tileY * TileSize);
}

/// <summary>
/// Reference codec that sends only what changed. A mostly static desktop costs a few kilobytes per frame
/// instead of the whole screen; the lossless pixels are still compressed with Deflate. Keyframes are sent first,
/// on resolution change, on request, and periodically, so a viewer that lost a frame recovers within one interval.
/// </summary>
public sealed class TileDeltaVideoEncoder : IVideoEncoder, IKeyframeControl
{
    private PooledBuffer? _previous;
    private int _prevWidth, _prevHeight;
    private uint _sequence;
    private int _sinceKeyframe;
    private volatile bool _forceKeyframe;

    public CodecType Codec => CodecType.TileDeltaReference;
    public VideoQuality Quality { get; set; } = VideoQuality.Good;
    public bool LastFrameWasKeyframe { get; private set; } = true;

    public void ForceKeyframe() => _forceKeyframe = true;

    public Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default)
    {
        using var scaled = FrameScaler.Fit(frame, Quality.Width, Quality.Height);
        int w = scaled.Width, h = scaled.Height;
        var current = scaled.Pixels.Span;
        var stride = w * 4;

        var interval = Math.Max(1, Quality.KeyframeIntervalSeconds * Math.Max(1, Quality.Fps));
        var key = _forceKeyframe || _previous is null || _prevWidth != w || _prevHeight != h || _sinceKeyframe + 1 >= interval; // a keyframe at least every `interval` frames
        _forceKeyframe = false;

        int tilesX = TileLayout.TilesX(w), tilesY = TileLayout.TilesY(h);
        var changed = new List<ushort>();
        if (_previous is not null && !key)
        {
            var prev = _previous.Array;
            for (var ty = 0; ty < tilesY; ty++)
                for (var tx = 0; tx < tilesX; tx++)
                    if (TileDiffers(current, prev, stride, tx, ty, w, h)) changed.Add((ushort)(ty * tilesX + tx));
        }
        else
        {
            for (var i = 0; i < tilesX * tilesY; i++) changed.Add((ushort)i);
            if (_previous is null || _prevWidth != w || _prevHeight != h)
            {
                _previous?.Dispose();
                _previous = new PooledBuffer(w * h * 4);
                _prevWidth = w;
                _prevHeight = h;
            }
        }

        // Gather the changed tiles' pixels row by row (also refreshing the reference frame), then compress once.
        var rawLength = 0;
        foreach (var idx in changed)
            rawLength += TileLayout.TileWidth(w, idx % tilesX) * 4 * TileLayout.TileHeight(h, idx / tilesX);

        var raw = new PooledBuffer(Math.Max(1, rawLength));
        try
        {
            var o = 0;
            var prevBuf = _previous!.Array;
            foreach (var idx in changed)
            {
                int tx = idx % tilesX, ty = idx / tilesX;
                int tw = TileLayout.TileWidth(w, tx) * 4, th = TileLayout.TileHeight(h, ty);
                for (var row = 0; row < th; row++)
                {
                    var src = current.Slice(((ty * TileLayout.TileSize + row) * w + tx * TileLayout.TileSize) * 4, tw);
                    src.CopyTo(raw.Array.AsSpan(o, tw));
                    src.CopyTo(prevBuf.AsSpan(((ty * TileLayout.TileSize + row) * w + tx * TileLayout.TileSize) * 4, tw));
                    o += tw;
                }
            }

            var ms = new MemoryStream(capacity: TileLayout.HeaderSize + changed.Count * 2 + Math.Max(256, rawLength / 4));
            Span<byte> header = stackalloc byte[TileLayout.HeaderSize];
            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)h);
            header[4] = key ? TileLayout.KeyframeFlag : (byte)0;
            BinaryPrimitives.WriteUInt32BigEndian(header[5..], _sequence);
            BinaryPrimitives.WriteUInt16BigEndian(header[9..], (ushort)changed.Count);
            ms.Write(header);

            Span<byte> idxBytes = stackalloc byte[2];
            foreach (var idx in changed)
            {
                BinaryPrimitives.WriteUInt16BigEndian(idxBytes, idx);
                ms.Write(idxBytes);
            }
            if (rawLength > 0)
                using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(raw.Array, 0, rawLength);

            _sequence++;
            _sinceKeyframe = key ? 0 : _sinceKeyframe + 1;
            LastFrameWasKeyframe = key;
            return Task.FromResult<ReadOnlyMemory<byte>>(ms.ToArray());
        }
        finally { raw.Dispose(); }
    }

    private static bool TileDiffers(ReadOnlySpan<byte> current, byte[] previous, int stride, int tx, int ty, int w, int h)
    {
        int tw = TileLayout.TileWidth(w, tx) * 4, th = TileLayout.TileHeight(h, ty);
        for (var row = 0; row < th; row++)
        {
            var offset = (ty * TileLayout.TileSize + row) * stride + tx * TileLayout.TileSize * 4;
            if (!current.Slice(offset, tw).SequenceEqual(previous.AsSpan(offset, tw))) return true;
        }
        return false;
    }

    public ValueTask DisposeAsync()
    {
        _previous?.Dispose();
        _previous = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Applies tile-delta frames to a canvas. One instance per stream, used from one thread (the receive loop).
/// Every input is untrusted: sizes, tile lists and the inflated length are validated before anything is written,
/// and any failure leaves the decoder refusing delta frames until the next keyframe.
/// </summary>
public sealed class TileDeltaVideoDecoder : IVideoDecoder
{
    private PooledBuffer? _canvas;
    private int _width, _height;
    private uint _lastSequence;
    private bool _needKeyframe = true;

    public CodecType Codec => CodecType.TileDeltaReference;

    public Task<CaptureFrame> DecodeAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default)
    {
        try { return Task.FromResult(Decode(encodedData.Span, encodedData)); }
        catch (KeyframeRequiredException) { throw; }
        catch (InvalidDataException) { _needKeyframe = true; throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            _needKeyframe = true;
            throw new InvalidDataException("Corrupt frame data.", ex);
        }
    }

    private CaptureFrame Decode(ReadOnlySpan<byte> data, ReadOnlyMemory<byte> memory)
    {
        if (data.Length < TileLayout.HeaderSize) throw new InvalidDataException("Truncated frame.");
        int w = BinaryPrimitives.ReadUInt16BigEndian(data);
        int h = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var flags = data[4];
        var seq = BinaryPrimitives.ReadUInt32BigEndian(data[5..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[9..]);

        if (w == 0 || h == 0 || w > DeflateVideoDecoder.MaxDimension || h > DeflateVideoDecoder.MaxDimension)
            throw new InvalidDataException("Invalid frame dimensions.");
        if ((flags & ~TileLayout.KeyframeFlag) != 0) throw new InvalidDataException("Unknown frame flags.");

        var key = (flags & TileLayout.KeyframeFlag) != 0;
        int tilesX = TileLayout.TilesX(w), tilesY = TileLayout.TilesY(h), totalTiles = tilesX * tilesY;
        if (count > totalTiles || (key && count != totalTiles)) throw new InvalidDataException("Invalid tile count.");

        if (!key)
        {
            if (_needKeyframe || _canvas is null || _width != w || _height != h)
                throw new KeyframeRequiredException("Delta frame without a matching reference frame.");
            if (seq != _lastSequence + 1)
                throw new KeyframeRequiredException("Frame sequence gap: a frame was lost.");
        }

        var indexBytes = count * 2;
        if (data.Length < TileLayout.HeaderSize + indexBytes) throw new InvalidDataException("Truncated tile list.");

        // Validate the tile list (strictly increasing, in range) and compute exactly how many bytes must inflate.
        long expected = 0;
        var last = -1;
        for (var i = 0; i < count; i++)
        {
            int idx = BinaryPrimitives.ReadUInt16BigEndian(data[(TileLayout.HeaderSize + i * 2)..]);
            if (idx <= last || idx >= totalTiles) throw new InvalidDataException("Invalid tile list.");
            last = idx;
            expected += TileLayout.TileWidth(w, idx % tilesX) * 4L * TileLayout.TileHeight(h, idx / tilesX);
        }

        // From here on the canvas is modified: if anything fails, deltas must not be applied on top of it.
        _needKeyframe = true;
        if (key && (_canvas is null || _width != w || _height != h))
        {
            _canvas?.Dispose();
            _canvas = new PooledBuffer(w * h * 4);
            _width = w;
            _height = h;
        }

        if (expected > 0)
        {
            var body = memory[(TileLayout.HeaderSize + indexBytes)..];
            using var input = MemoryMarshal.TryGetArray(body, out var seg)
                ? new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false)
                : new MemoryStream(body.ToArray());
            using var z = new DeflateStream(input, CompressionMode.Decompress);

            var canvas = _canvas!.Array;
            for (var i = 0; i < count; i++)
            {
                int idx = BinaryPrimitives.ReadUInt16BigEndian(data[(TileLayout.HeaderSize + i * 2)..]);
                int tx = idx % tilesX, ty = idx / tilesX;
                int tw = TileLayout.TileWidth(w, tx) * 4, th = TileLayout.TileHeight(h, ty);
                for (var row = 0; row < th; row++)
                {
                    var dst = canvas.AsSpan(((ty * TileLayout.TileSize + row) * w + tx * TileLayout.TileSize) * 4, tw);
                    try { z.ReadExactly(dst); }
                    catch (EndOfStreamException) { throw new InvalidDataException("Frame data shorter than declared size."); }
                }
            }
            if (z.ReadByte() != -1) throw new InvalidDataException("Frame data longer than declared size.");
        }
        else if (HasTrailingData(data, TileLayout.HeaderSize + indexBytes))
        {
            throw new InvalidDataException("Unexpected data after an empty tile list.");
        }

        _lastSequence = seq;
        _needKeyframe = false;

        // Hand out a copy: the canvas keeps accumulating, the renderer needs a stable frame.
        var output = new PooledBuffer(w * h * 4);
        _canvas!.Array.AsSpan(0, w * h * 4).CopyTo(output.Array);
        return new CaptureFrame { Width = w, Height = h, PixelData = output.Array.AsMemory(0, w * h * 4), Owner = output };
    }

    private static bool HasTrailingData(ReadOnlySpan<byte> data, int offset) => data.Length > offset;

    public ValueTask DisposeAsync()
    {
        _canvas?.Dispose();
        _canvas = null;
        return ValueTask.CompletedTask;
    }
}
