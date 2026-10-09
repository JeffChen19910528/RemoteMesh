namespace RemoteDesktop.Encoding;

/// <summary>
/// A JPEG implementation supplied by the platform (Windows: GDI+). The encoding layer never links an image library
/// itself, so it stays portable and a platform without a JPEG codec simply falls back to lossless frames.
/// </summary>
public interface IJpegCodec
{
    /// <summary>
    /// Compresses a rectangle of BGRA pixels. <paramref name="pixels"/> starts at the rectangle's top-left corner and
    /// consecutive rows are <paramref name="stride"/> bytes apart. Quality is 1–100.
    /// </summary>
    byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality);

    /// <summary>
    /// Decodes <paramref name="jpeg"/> into BGRA rows of <paramref name="destinationStride"/> bytes starting at
    /// <paramref name="destination"/>. The caller has already verified the picture is exactly
    /// <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable JPEG.</exception>
    void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride);
}

/// <summary>
/// Reads the picture size out of a JPEG header without decoding it. Pictures come from the other computer, so the
/// size is checked against what the frame header promised <em>before</em> any decoder allocates memory for it.
/// </summary>
public static class JpegHeader
{
    /// <summary>The (width, height) declared by the first start-of-frame marker, or null if the data is not a plausible JPEG.</summary>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null; // SOI

        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }            // fill byte
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; } // no payload

            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) return null;

            // SOF0..SOF15 except DHT(C4), JPG(C8), DAC(CC): baseline, extended, progressive, ...
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) return null;
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }
            if (marker == 0xDA) return null; // start of scan before any frame header: malformed
            i += 2 + length;
        }
        return null;
    }
}
