using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Encoding;

/// <summary>
/// Software reference codec (intra-only, Deflate over BGRA). Never ships raw bitmaps, needs no native
/// libraries, and lets the whole pipeline be tested. H.264/VP9/AV1 backends implement the same interfaces.
/// </summary>
public sealed class DeflateVideoEncoder : IVideoEncoder
{
    public CodecType Codec => CodecType.DeflateReference;
    public VideoQuality Quality { get; set; } = VideoQuality.Good;

    public Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default)
    {
        using var scaled = FrameScaler.Fit(frame, Quality.Width, Quality.Height);
        var pixels = scaled.Pixels;

        // Header goes straight into the output stream and the stream starts near its final size, so the
        // compressed frame is built once and copied out once (no re-growth, no prepend copy).
        var ms = new MemoryStream(capacity: 4 + Math.Max(4096, pixels.Length / 4));
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)scaled.Width);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)scaled.Height);
        ms.Write(header);
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(pixels.Span);
        return Task.FromResult<ReadOnlyMemory<byte>>(ms.ToArray());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DeflateVideoDecoder : IVideoDecoder
{
    public const int MaxDimension = 8192;
    public CodecType Codec => CodecType.DeflateReference;

    /// <remarks>The returned frame's pixels are pooled: dispose the frame when done with them.</remarks>
    public Task<CaptureFrame> DecodeAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default)
    {
        if (encodedData.Length < 4) throw new InvalidDataException("Truncated frame.");
        var w = BinaryPrimitives.ReadUInt16BigEndian(encodedData.Span);
        var h = BinaryPrimitives.ReadUInt16BigEndian(encodedData.Span[2..]);
        if (w == 0 || h == 0 || w > MaxDimension || h > MaxDimension)
            throw new InvalidDataException("Invalid frame dimensions.");

        var expected = w * h * 4;
        var buffer = new PooledBuffer(expected);
        try
        {
            var body = encodedData[4..];
            // Read straight from the caller's array when possible instead of copying the whole frame first.
            using var input = MemoryMarshal.TryGetArray(body, out var seg)
                ? new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false)
                : new MemoryStream(body.ToArray());
            using var z = new DeflateStream(input, CompressionMode.Decompress);

            var read = 0;
            while (read < expected)
            {
                var n = z.Read(buffer.Array, read, expected - read);
                if (n == 0) throw new InvalidDataException("Frame data shorter than declared size.");
                read += n;
            }
            // Bound decompression: refuse payloads that inflate beyond the declared size.
            if (z.ReadByte() != -1) throw new InvalidDataException("Frame data longer than declared size.");

            return Task.FromResult(new CaptureFrame
            {
                Width = w, Height = h, PixelData = buffer.Array.AsMemory(0, expected), Owner = buffer
            });
        }
        catch (InvalidDataException) { buffer.Dispose(); throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            buffer.Dispose();
            throw new InvalidDataException("Corrupt frame data.", ex);
        }
        catch { buffer.Dispose(); throw; }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A (possibly pooled) scaled copy of a frame; dispose to return its buffer.</summary>
internal sealed class ScaledFrame(int width, int height, ReadOnlyMemory<byte> pixels, PooledBuffer? owner) : IDisposable
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public ReadOnlyMemory<byte> Pixels { get; } = pixels;
    public void Dispose() => owner?.Dispose();
}

internal static class FrameScaler
{
    /// <summary>Nearest-neighbour downscale (never upscales) preserving aspect ratio.</summary>
    public static ScaledFrame Fit(CaptureFrame f, int maxW, int maxH)
    {
        if (maxW <= 0 || maxH <= 0 || (f.Width <= maxW && f.Height <= maxH))
            return new ScaledFrame(f.Width, f.Height, f.PixelData, null);

        var scale = Math.Min((double)maxW / f.Width, (double)maxH / f.Height);
        var w = Math.Max(1, (int)(f.Width * scale));
        var h = Math.Max(1, (int)(f.Height * scale));

        var buffer = new PooledBuffer(w * h * 4);
        var src = MemoryMarshal.Cast<byte, uint>(f.PixelData.Span);
        var dst = MemoryMarshal.Cast<byte, uint>(buffer.Array.AsSpan(0, w * h * 4));

        // Source column for each destination column, computed once per frame instead of a division per pixel.
        var columns = new int[w];
        for (var x = 0; x < w; x++) columns[x] = (int)((long)x * f.Width / w);

        for (var y = 0; y < h; y++)
        {
            var srcRow = src.Slice((int)((long)y * f.Height / h) * f.Width, f.Width);
            var dstRow = dst.Slice(y * w, w);
            for (var x = 0; x < dstRow.Length; x++) dstRow[x] = srcRow[columns[x]];
        }
        return new ScaledFrame(w, h, buffer.Array.AsMemory(0, w * h * 4), buffer);
    }
}
