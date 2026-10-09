using System.Runtime.InteropServices;
using System.Buffers.Binary;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Encoding;

/// <summary>A rectangle of whole 64×64 tiles that is sent as one JPEG picture.</summary>
internal readonly record struct TileRect(int X, int Y, int Width, int Height);

/// <summary>How the encoder turns "these tiles changed" into as few, as compact pictures as possible.</summary>
internal static class RegionPlanner
{
    public const int MaxRegions = 128;

    /// <summary>
    /// A big change (scrolling, video) is one picture: a single JPEG compresses far better than hundreds of small
    /// ones, each with its own header. A small change becomes horizontal strips, merged downwards when consecutive
    /// rows change over the same columns (a window being dragged, a paragraph being typed).
    /// </summary>
    public static List<TileRect> Plan(bool[] changed, int tilesX, int tilesY)
    {
        var count = 0;
        foreach (var c in changed) if (c) count++;
        if (count == 0) return [];

        // More than ~a third of the screen changed: send the bounding box as one picture.
        if (count * 100L > changed.Length * 35L) return [BoundingBox(changed, tilesX, tilesY)];

        var done = new List<TileRect>();
        var open = new Dictionary<(int X, int W), TileRect>(); // rectangles that ended on the previous row
        for (var ty = 0; ty < tilesY; ty++)
        {
            var next = new Dictionary<(int X, int W), TileRect>();
            var tx = 0;
            while (tx < tilesX)
            {
                if (!changed[ty * tilesX + tx]) { tx++; continue; }
                var start = tx;
                while (tx < tilesX && changed[ty * tilesX + tx]) tx++;
                var key = (start, tx - start);
                next[key] = open.Remove(key, out var above)
                    ? above with { Height = above.Height + 1 }
                    : new TileRect(start, ty, tx - start, 1);
            }
            foreach (var finished in open.Values) done.Add(finished);
            open = next;
        }
        done.AddRange(open.Values);

        return done.Count > MaxRegions ? [BoundingBox(changed, tilesX, tilesY)] : done;
    }

    private static TileRect BoundingBox(bool[] changed, int tilesX, int tilesY)
    {
        int minX = tilesX, minY = tilesY, maxX = -1, maxY = -1;
        for (var i = 0; i < changed.Length; i++)
        {
            if (!changed[i]) continue;
            int x = i % tilesX, y = i / tilesX;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return new TileRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}

/// <summary>How hard to squeeze: a poorer connection (lower bitrate target) gets a lower JPEG quality.</summary>
public static class LossyQuality
{
    public static int For(VideoQuality q) => q.BitrateKbps switch
    {
        >= 8000 => 90,
        >= 4000 => 80,
        >= 2000 => 70,
        >= 1000 => 60,
        _ => 50
    };
}

/// <summary>
/// Lossy tile codec: like <see cref="TileDeltaVideoEncoder"/> it only sends what changed, but changed areas are JPEG
/// pictures instead of Deflate-compressed raw pixels, so even full-screen motion fits a normal connection.
/// Wire format: <c>[width:2][height:2][flags:1][seq:4][regionCount:2]</c> then per region
/// <c>[tileX:2][tileY:2][tilesWide:2][tilesHigh:2][length:4][jpeg]</c>.
/// Changes are detected on the source pixels (not on what the viewer reconstructed), so a still area stays as the
/// viewer last saw it until a periodic full refresh replaces it.
/// </summary>
public sealed class TileDeltaJpegEncoder(IJpegCodec codec, int refreshSeconds = 10) : IVideoEncoder, IKeyframeControl, IRateAdaptive
{
    private int _jpegQuality; // 0 = derive from Quality.BitrateKbps
    public const int RegionHeaderSize = 2 + 2 + 2 + 2 + 4;

    private PooledBuffer? _previous;
    private int _prevWidth, _prevHeight;
    private uint _sequence;
    private int _sinceKeyframe;
    private volatile bool _forceKeyframe;

    public CodecType Codec => CodecType.TileDeltaJpeg;

    /// <summary>Native resolution by default: lossy compression is the saving here, and downscaling would blur text.</summary>
    public VideoQuality Quality { get; set; } = new() { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 };

    public bool LastFrameWasKeyframe { get; private set; } = true;

    /// <summary>Applies one rung of the rate ladder: picture size and JPEG quality. A size change starts with a keyframe.</summary>
    public void Apply(RateStep step, int sourceWidth, int sourceHeight)
    {
        _jpegQuality = Math.Clamp(step.JpegQuality, 1, 100);
        var scale = Math.Clamp(step.ScalePercent, 10, 100);
        Quality = new VideoQuality
        {
            Width = scale >= 100 ? 0 : Math.Max(16, sourceWidth * scale / 100),
            Height = scale >= 100 ? 0 : Math.Max(16, sourceHeight * scale / 100),
            Fps = step.Fps,
            BitrateKbps = Quality.BitrateKbps,
            KeyframeIntervalSeconds = Quality.KeyframeIntervalSeconds
        };
    }

    public void ForceKeyframe() => _forceKeyframe = true;

    public Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default)
    {
        using var scaled = FrameScaler.Fit(frame, Quality.Width, Quality.Height);
        int w = scaled.Width, h = scaled.Height, stride = w * 4;
        var current = scaled.Pixels.Span;

        var refreshFrames = Math.Max(1, refreshSeconds * Math.Max(1, Quality.Fps));
        var key = _forceKeyframe || _previous is null || _prevWidth != w || _prevHeight != h || _sinceKeyframe + 1 >= refreshFrames;
        _forceKeyframe = false;

        int tilesX = TileLayout.TilesX(w), tilesY = TileLayout.TilesY(h);
        List<TileRect> regions;
        if (key)
        {
            regions = [new TileRect(0, 0, tilesX, tilesY)]; // the whole picture, in one piece
            if (_previous is null || _prevWidth != w || _prevHeight != h)
            {
                _previous?.Dispose();
                _previous = new PooledBuffer(w * h * 4);
                _prevWidth = w;
                _prevHeight = h;
            }
        }
        else
        {
            var changed = new bool[tilesX * tilesY];
            var prev = _previous!.Array;
            for (var ty = 0; ty < tilesY; ty++)
                for (var tx = 0; tx < tilesX; tx++)
                    changed[ty * tilesX + tx] = TileDiffers(current, prev, stride, tx, ty, w, h);
            regions = RegionPlanner.Plan(changed, tilesX, tilesY);
        }

        // Nothing changed: send nothing. The sequence number is not consumed, so the next real frame still follows
        // the last one the viewer got and no gap is seen.
        if (!key && regions.Count == 0)
        {
            _sinceKeyframe++;
            LastFrameWasKeyframe = false;
            return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        var quality = _jpegQuality > 0 ? _jpegQuality : LossyQuality.For(Quality);
        var pictures = new List<byte[]>(regions.Count);
        var total = TileLayout.HeaderSize;
        var prevBuf = _previous!.Array;
        foreach (var r in regions)
        {
            var (x0, y0, rw, rh) = PixelRect(r, w, h);
            var jpeg = codec.Encode(current[(y0 * stride + x0 * 4)..], rw, rh, stride, quality);
            pictures.Add(jpeg);
            total += RegionHeaderSize + jpeg.Length;

            // The reference frame follows what was sent, so the next frame diffs against it.
            for (var row = 0; row < rh; row++)
                current.Slice((y0 + row) * stride + x0 * 4, rw * 4).CopyTo(prevBuf.AsSpan((y0 + row) * stride + x0 * 4, rw * 4));
        }

        var output = new byte[total];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)h);
        span[4] = key ? TileLayout.KeyframeFlag : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], _sequence);
        BinaryPrimitives.WriteUInt16BigEndian(span[9..], (ushort)regions.Count);
        var o = TileLayout.HeaderSize;
        for (var i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            BinaryPrimitives.WriteUInt16BigEndian(span[o..], (ushort)r.X);
            BinaryPrimitives.WriteUInt16BigEndian(span[(o + 2)..], (ushort)r.Y);
            BinaryPrimitives.WriteUInt16BigEndian(span[(o + 4)..], (ushort)r.Width);
            BinaryPrimitives.WriteUInt16BigEndian(span[(o + 6)..], (ushort)r.Height);
            BinaryPrimitives.WriteUInt32BigEndian(span[(o + 8)..], (uint)pictures[i].Length);
            pictures[i].CopyTo(span[(o + RegionHeaderSize)..]);
            o += RegionHeaderSize + pictures[i].Length;
        }

        _sequence++;
        _sinceKeyframe = key ? 0 : _sinceKeyframe + 1;
        LastFrameWasKeyframe = key;
        return Task.FromResult<ReadOnlyMemory<byte>>(output);
    }

    /// <summary>Pixel position and size of a tile rectangle (the last column/row of tiles can be partial).</summary>
    internal static (int X, int Y, int Width, int Height) PixelRect(TileRect r, int w, int h)
    {
        int x0 = r.X * TileLayout.TileSize, y0 = r.Y * TileLayout.TileSize;
        return (x0, y0, Math.Min(w, (r.X + r.Width) * TileLayout.TileSize) - x0, Math.Min(h, (r.Y + r.Height) * TileLayout.TileSize) - y0);
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
/// Applies JPEG tile frames to a canvas. Everything in a frame comes from the other computer, so every number is
/// checked before it is used: region geometry, the total area (a frame cannot make the viewer decode more than one
/// screenful), and each picture's own declared size against the area it is supposed to fill.
/// </summary>
public sealed class TileDeltaJpegDecoder(IJpegCodec codec) : IVideoDecoder
{
    private PooledBuffer? _canvas;
    private int _width, _height;
    private uint _lastSequence;
    private bool _needKeyframe = true;

    public CodecType Codec => CodecType.TileDeltaJpeg;

    public Task<CaptureFrame> DecodeAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default)
    {
        try { return Task.FromResult(Decode(encodedData.Span)); }
        catch (KeyframeRequiredException) { throw; }
        catch (InvalidDataException) { _needKeyframe = true; throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or ExternalException)
        {
            _needKeyframe = true;
            throw new InvalidDataException("Corrupt frame data.", ex);
        }
    }

    private CaptureFrame Decode(ReadOnlySpan<byte> data)
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
        if (count > RegionPlanner.MaxRegions || count > totalTiles) throw new InvalidDataException("Too many regions.");
        if (key && count != 1) throw new InvalidDataException("A keyframe is one whole picture.");

        if (!key)
        {
            if (_needKeyframe || _canvas is null || _width != w || _height != h)
                throw new KeyframeRequiredException("Delta frame without a matching reference frame.");
            if (seq != _lastSequence + 1)
                throw new KeyframeRequiredException("Frame sequence gap: a frame was lost.");
        }

        // First pass: validate the whole frame before touching the canvas.
        var regions = new (TileRect Rect, int Offset, int Length)[count];
        var pos = TileLayout.HeaderSize;
        long area = 0;
        for (var i = 0; i < count; i++)
        {
            if (data.Length - pos < TileDeltaJpegEncoder.RegionHeaderSize) throw new InvalidDataException("Truncated region header.");
            int rx = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            int ry = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            int rw = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]);
            int rh = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 6)..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]);
            pos += TileDeltaJpegEncoder.RegionHeaderSize;

            if (rw == 0 || rh == 0 || rx + rw > tilesX || ry + rh > tilesY) throw new InvalidDataException("Region outside the picture.");
            if (key && (rx != 0 || ry != 0 || rw != tilesX || rh != tilesY)) throw new InvalidDataException("A keyframe must cover the whole picture.");
            if (length == 0 || length > (uint)(data.Length - pos)) throw new InvalidDataException("Region data length is wrong.");

            area += (long)rw * rh;
            if (area > totalTiles) throw new InvalidDataException("Regions cover more than the picture.");

            var rect = new TileRect(rx, ry, rw, rh);
            var (_, _, pw, ph) = TileDeltaJpegEncoder.PixelRect(rect, w, h);
            var declared = JpegHeader.ReadSize(data.Slice(pos, (int)length));
            if (declared is not { } size || size.Width != pw || size.Height != ph)
                throw new InvalidDataException("Picture size does not match its region.");

            regions[i] = (rect, pos, (int)length);
            pos += (int)length;
        }
        if (pos != data.Length) throw new InvalidDataException("Unexpected data after the last region.");

        // From here on the canvas changes: if anything fails, deltas must not be applied on top of it.
        _needKeyframe = true;
        if (key && (_canvas is null || _width != w || _height != h))
        {
            _canvas?.Dispose();
            _canvas = new PooledBuffer(w * h * 4);
            _width = w;
            _height = h;
        }

        var canvas = _canvas!.Array;
        var stride = w * 4;
        foreach (var (rect, offset, length) in regions)
        {
            var (x0, y0, pw, ph) = TileDeltaJpegEncoder.PixelRect(rect, w, h);
            codec.Decode(data.Slice(offset, length), pw, ph, canvas.AsSpan(y0 * stride + x0 * 4), stride);
        }

        _lastSequence = seq;
        _needKeyframe = false;

        var output = new PooledBuffer(w * h * 4);
        canvas.AsSpan(0, w * h * 4).CopyTo(output.Array);
        return new CaptureFrame { Width = w, Height = h, PixelData = output.Array.AsMemory(0, w * h * 4), Owner = output };
    }

    public ValueTask DisposeAsync()
    {
        _canvas?.Dispose();
        _canvas = null;
        return ValueTask.CompletedTask;
    }
}
