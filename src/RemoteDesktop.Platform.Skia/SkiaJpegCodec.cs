using SkiaSharp;
using RemoteDesktop.Encoding;

namespace RemoteDesktop.Platform.Skia;

/// <summary>
/// JPEG through Skia (Linux, macOS, and Windows too): an established image library, no image code of our own.
/// <see cref="Decode"/> receives data from the other computer, so the caller checks the declared size first
/// (<see cref="JpegHeader"/>) and this class refuses a picture whose real size differs.
/// </summary>
public sealed class SkiaJpegCodec : IJpegCodec
{
    public unsafe byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if ((long)(height - 1) * stride + width * 4L > pixels.Length)
            throw new ArgumentException("The pixel buffer is smaller than the rectangle it should hold.", nameof(pixels));

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* p = pixels)
        {
            // Wraps the frame buffer without copying it; the image is gone again before this method returns.
            using var image = SKImage.FromPixels(info, (IntPtr)p, stride);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            if (data is null) throw new InvalidOperationException("The JPEG encoder produced nothing.");
            return data.ToArray();
        }
    }

    public unsafe void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride)
    {
        if ((long)(height - 1) * destinationStride + width * 4L > destination.Length || destinationStride < width * 4)
            throw new ArgumentException("The destination is smaller than the picture.", nameof(destination));

        // Size first, from the header, before the decoder allocates anything for a picture of that size.
        if (JpegHeader.ReadSize(jpeg) is not { } size || size.Width != width || size.Height != height)
            throw new InvalidDataException("Picture size differs from its header.");

        SKBitmap? bitmap;
        try { bitmap = SKBitmap.Decode(jpeg.ToArray(), new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            throw new InvalidDataException("The picture could not be decoded.", ex);
        }
        if (bitmap is null) throw new InvalidDataException("The picture could not be decoded.");

        using (bitmap)
        {
            if (bitmap.Width != width || bitmap.Height != height)
                throw new InvalidDataException("Picture size differs from its header.");
            if (bitmap.ColorType != SKColorType.Bgra8888) throw new InvalidDataException("The picture could not be decoded.");
            var source = bitmap.GetPixelSpan();
            var rowBytes = bitmap.RowBytes;
            for (var y = 0; y < height; y++)
            {
                var row = source.Slice(y * rowBytes, width * 4);
                row.CopyTo(destination.Slice(y * destinationStride, width * 4));
                var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination.Slice(y * destinationStride, width * 4));
                for (var x = 0; x < pixels.Length; x++) pixels[x] |= 0xFF000000u; // the pipeline treats frames as opaque
            }
        }
    }
}
