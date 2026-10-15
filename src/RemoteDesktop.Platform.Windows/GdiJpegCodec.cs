using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Encoding;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// JPEG through the Windows imaging stack (GDI+): no image code of our own, and security fixes arrive with
/// Windows Update. <see cref="Decode"/> receives data from the other computer, so the caller checks the declared
/// size first (<see cref="JpegHeader"/>) and this class refuses a picture whose real size differs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiJpegCodec : IJpegCodec
{
    private static readonly ImageCodecInfo JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public unsafe byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if ((long)(height - 1) * stride + width * 4L > pixels.Length)
            throw new ArgumentException("The pixel buffer is smaller than the rectangle it should hold.", nameof(pixels));

        fixed (byte* p = pixels)
        {
            // The bitmap reads straight from the frame buffer: no copy of the region before compressing it.
            using var bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, (IntPtr)p);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            using var output = new MemoryStream(Math.Max(1024, width * height / 8));
            bitmap.Save(output, JpegEncoder, parameters);
            return output.ToArray();
        }
    }

    public unsafe void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride)
    {
        if ((long)(height - 1) * destinationStride + width * 4L > destination.Length || destinationStride < width * 4)
            throw new ArgumentException("The destination is smaller than the picture.", nameof(destination));

        try
        {
            using var input = new MemoryStream(jpeg.ToArray(), writable: false);
            using var image = new Bitmap(input);
            if (image.Width != width || image.Height != height)
                throw new InvalidDataException("Picture size differs from its header.");

            var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    var source = new ReadOnlySpan<byte>((byte*)data.Scan0 + (long)y * data.Stride, width * 4);
                    var row = destination.Slice(y * destinationStride, width * 4);
                    source.CopyTo(row);
                    // GDI+ leaves alpha undefined; the pipeline treats frames as opaque.
                    var pixels = MemoryMarshal.Cast<byte, uint>(row);
                    for (var x = 0; x < pixels.Length; x++) pixels[x] |= 0xFF000000u;
                }
            }
            finally { image.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports an undecodable picture in several unrelated ways; to the caller it is all one thing.
            throw new InvalidDataException("The picture could not be decoded.", ex);
        }
    }
}
