using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Platform.Skia;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Platform.Tests;

/// <summary>The cross-platform JPEG codec: fidelity, size checks and hostile input. Runs on every operating system.</summary>
public sealed class SkiaJpegCodecTests
{
    /// <summary>A stand-in for a desktop: white page, dark "text", coloured panels, a gradient, a few photo-like noisy blocks.</summary>
    private static byte[] Desktop(int w, int h, int shift = 0)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte r = 250, g = 250, b = 250;
                if (y > 40 && y < h - 40 && x > 40 && x < w / 2 && ((x / 3 + (y + shift) / 5) % 7 < 2)) { r = g = b = 30; }
                if (x >= w / 2 + 20 && x < w - 40 && y >= 60 && y < 200) { r = (byte)(x * 255 / w); g = (byte)(y * 255 / h); b = 160; }
                if (x >= w / 2 + 20 && x < w / 2 + 160 && y >= 260 && y < 400) { r = (byte)rnd.Next(256); g = (byte)rnd.Next(256); b = (byte)rnd.Next(256); }
                if (y < 32) { r = 30; g = 90; b = 200; }
                var i = (y * w + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        return px;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        long n = 0;
        for (var i = 0; i < a.Length; i += 4)
            for (var c = 0; c < 3; c++) { var d = a[i + c] - b[i + c]; sum += d * d; n++; }
        var mse = sum / n;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Fact]
    public void ARegion_RoundTripsWithGoodFidelity_AndTheHeaderStatesItsSize()
    {
        var codec = new SkiaJpegCodec();
        const int w = 200, h = 120;
        var px = Desktop(w, h);

        var jpeg = codec.Encode(px, w, h, w * 4, 85);
        Assert.Equal((w, h), JpegHeader.ReadSize(jpeg));

        var back = new byte[w * h * 4];
        codec.Decode(jpeg, w, h, back, w * 4);
        Assert.True(Psnr(px, back) > 28, $"PSNR {Psnr(px, back):F1} dB");
        for (var i = 3; i < back.Length; i += 4) Assert.Equal(255, back[i]);
    }

    [Fact]
    public void AColourIsStillTheSameColour_NotRedAndBlueSwapped()
    {
        var codec = new SkiaJpegCodec();
        var px = new byte[64 * 64 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; } // BGRA: pure red
        var back = new byte[px.Length];
        codec.Decode(codec.Encode(px, 64, 64, 256, 95), 64, 64, back, 256);

        Assert.True(back[2] > 240 && back[0] < 20 && back[1] < 20, $"B={back[0]} G={back[1]} R={back[2]}");
    }

    [Fact]
    public void ASubRectangle_IsReadWithTheFrameStride_NotCopiedAround()
    {
        var codec = new SkiaJpegCodec();
        const int w = 400, h = 300;
        var px = Desktop(w, h);

        var jpeg = codec.Encode(px.AsSpan((64 * w + 128) * 4), 64, 64, w * 4, 90);
        var tile = new byte[64 * 64 * 4];
        codec.Decode(jpeg, 64, 64, tile, 64 * 4);

        var expected = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++) Array.Copy(px, ((64 + y) * w + 128) * 4, expected, y * 64 * 4, 64 * 4);
        Assert.True(Psnr(expected, tile) > 28);
    }

    [Fact]
    public void DecodingIntoALargerFrame_HonoursTheDestinationStride()
    {
        var codec = new SkiaJpegCodec();
        var px = Desktop(64, 64);
        var jpeg = codec.Encode(px, 64, 64, 256, 90);
        var frame = new byte[200 * 100 * 4];
        codec.Decode(jpeg, 64, 64, frame.AsSpan((10 * 200 + 20) * 4), 200 * 4);

        var expected = new byte[64 * 64 * 4];
        var got = new byte[64 * 64 * 4];
        codec.Decode(jpeg, 64, 64, expected, 256);
        for (var y = 0; y < 64; y++) Array.Copy(frame, ((10 + y) * 200 + 20) * 4, got, y * 256, 256);
        Assert.Equal(expected, got);
        Assert.Equal(0, frame[0]); // nothing was written outside the rectangle
    }

    [Theory]
    [InlineData(0, 10, 40, 40)]
    [InlineData(10, 0, 40, 40)]
    [InlineData(10, 10, 20, 40)]
    [InlineData(10, 10, 40, 399)]
    public void Encode_RefusesInconsistentArguments(int w, int h, int stride, int bufferLength) =>
        Assert.ThrowsAny<ArgumentException>(() => new SkiaJpegCodec().Encode(new byte[bufferLength], w, h, stride, 80));

    [Fact]
    public void Decode_RefusesADestinationThatIsTooSmall()
    {
        var codec = new SkiaJpegCodec();
        var jpeg = codec.Encode(Desktop(64, 64), 64, 64, 64 * 4, 80);
        Assert.Throws<ArgumentException>(() => codec.Decode(jpeg, 64, 64, new byte[64 * 63 * 4], 64 * 4));
    }

    [Fact]
    public void Decode_RefusesAPictureWhoseRealSizeIsNotTheExpectedOne()
    {
        var codec = new SkiaJpegCodec();
        var jpeg = codec.Encode(Desktop(100, 100), 100, 100, 400, 80);
        Assert.Throws<InvalidDataException>(() => codec.Decode(jpeg, 64, 64, new byte[64 * 64 * 4], 64 * 4));
    }

    [Theory]
    [InlineData("random bytes")]
    [InlineData("png")]
    [InlineData("truncated jpeg")]
    [InlineData("jpeg header then garbage")]
    [InlineData("empty")]
    public void Decode_TurnsBadPictures_IntoInvalidData_NeverACrash(string kind)
    {
        var codec = new SkiaJpegCodec();
        var good = codec.Encode(Desktop(64, 64), 64, 64, 256, 80);
        var noise = new byte[500];
        new Random(1).NextBytes(noise);
        byte[] bad = kind switch
        {
            "random bytes" => noise,
            "png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0],
            "truncated jpeg" => good[..(good.Length / 3)],
            "empty" => [],
            _ => good[..40].Concat(noise).ToArray()
        };

        try { codec.Decode(bad, 64, 64, new byte[64 * 64 * 4], 256); }
        catch (InvalidDataException) { /* the expected refusal */ }
        // (a lenient decoder may also render a partial picture; the point is that nothing else escapes)
    }

    [Fact]
    public void ManyThreads_CanEncodeAndDecodeAtOnce()
    {
        var codec = new SkiaJpegCodec();
        var px = Desktop(160, 96);
        Parallel.For(0, 64, _ =>
        {
            var jpeg = codec.Encode(px, 160, 96, 640, 80);
            var back = new byte[160 * 96 * 4];
            codec.Decode(jpeg, 160, 96, back, 640);
            Assert.True(Psnr(px, back) > 25);
        });
    }

    [Fact]
    public async Task LossyPipeline_StaysCloseToTheSource_ThroughKeyframeAndDeltas()
    {
        var codec = new SkiaJpegCodec();
        var enc = new TileDeltaJpegEncoder(codec) { Quality = new VideoQuality { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 } };
        var dec = new TileDeltaJpegDecoder(codec);
        const int w = 1024, h = 640;

        for (var step = 0; step < 6; step++)
        {
            var px = Desktop(w, h, shift: step * 3);
            using var decoded = await dec.DecodeAsync(await enc.EncodeAsync(new CaptureFrame { Width = w, Height = h, PixelData = px }));
            Assert.True(Psnr(px, decoded.PixelData.ToArray()) > 26, $"frame {step}: {Psnr(px, decoded.PixelData.ToArray()):F1} dB");
        }
    }
}

public sealed class CodecSelfTestTests
{
    private sealed class BrokenCodec(Exception failure) : RemoteDesktop.Encoding.IJpegCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality) => throw failure;
        public void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride) => throw failure;
    }

    [Fact]
    public void AWorkingCodec_IsKept() =>
        Assert.NotNull(RemoteDesktop.Platform.PlatformHost.SelfTested(new SkiaJpegCodec()));

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(TypeInitializationException))]
    [InlineData(typeof(BadImageFormatException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    public void ACodecWhoseNativeLibraryIsMissing_IsLeftOut_SoVideoFallsBackToLossless(Type failure)
    {
        var ex = failure == typeof(TypeInitializationException) ? new TypeInitializationException("x", null) : (Exception)Activator.CreateInstance(failure)!;
        Assert.Null(RemoteDesktop.Platform.PlatformHost.SelfTested(new BrokenCodec(ex)));
    }

    [Fact]
    public void TheCodecChosenForThisSystem_ActuallyWorks()
    {
        var codec = RemoteDesktop.Platform.PlatformHost.CreateJpegCodec();
        Assert.NotNull(codec);
        var px = new byte[32 * 32 * 4];
        Array.Fill(px, (byte)200);
        var back = new byte[px.Length];
        codec.Decode(codec.Encode(px, 32, 32, 128, 90), 32, 32, back, 128);
        Assert.InRange(back[0], 190, 210);
    }
}
