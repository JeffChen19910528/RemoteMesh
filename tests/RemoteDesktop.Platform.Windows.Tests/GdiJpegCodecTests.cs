using System.Diagnostics;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Platform.Windows;
using Xunit.Abstractions;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The lossy codec with the real Windows JPEG implementation: how it looks, how small it is, how fast it is and
/// whether it survives hostile input. Pixels from the real screen are only measured in memory, never saved.
/// </summary>
public sealed class GdiJpegCodecTests(ITestOutputHelper output)
{
    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>A stand-in for a desktop: white page, dark "text", coloured panels, a gradient, a few photo-like noisy blocks.</summary>
    private static byte[] Desktop(int w, int h, int shift = 0)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte r = 250, g = 250, b = 250;
                if (y > 40 && y < h - 40 && x > 40 && x < w / 2 && ((x / 3 + (y + shift) / 5) % 7 < 2)) { r = g = b = 30; } // text-like strokes
                if (x >= w / 2 + 20 && x < w - 40 && y >= 60 && y < 200) { r = (byte)(x * 255 / w); g = (byte)(y * 255 / h); b = 160; } // gradient panel
                if (x >= w / 2 + 20 && x < w / 2 + 160 && y >= 260 && y < 400) { r = (byte)rnd.Next(256); g = (byte)rnd.Next(256); b = (byte)rnd.Next(256); } // noisy "photo"
                if (y < 32) { r = 30; g = 90; b = 200; } // title bar
                var i = (y * w + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        return px;
    }

    private static CaptureFrame Frame(int w, int h, byte[] px) => new() { Width = w, Height = h, PixelData = px };

    /// <summary>Peak signal-to-noise ratio in dB over the colour channels (higher is closer; ≥ 30 is visually clean).</summary>
    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        long n = 0;
        for (var i = 0; i < a.Length; i += 4)
            for (var c = 0; c < 3; c++) { var d = a[i + c] - b[i + c]; sum += d * d; n++; }
        var mse = sum / n;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    // ── The codec itself ─────────────────────────────────────────────────

    [Fact]
    public void ARegion_RoundTripsWithGoodFidelity_AndTheHeaderStatesItsSize()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
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
    public void ASubRectangle_IsReadWithTheFrameStride_NotCopiedAround()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        const int w = 400, h = 300;
        var px = Desktop(w, h);

        // The 64x64 tile at (128, 64) of a 400-wide frame.
        var jpeg = codec.Encode(px.AsSpan((64 * w + 128) * 4), 64, 64, w * 4, 90);
        var tile = new byte[64 * 64 * 4];
        codec.Decode(jpeg, 64, 64, tile, 64 * 4);

        var expected = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++) Array.Copy(px, ((64 + y) * w + 128) * 4, expected, y * 64 * 4, 64 * 4);
        Assert.True(Psnr(expected, tile) > 28);
    }

    [Theory]
    [InlineData(0, 10, 40, 40)]
    [InlineData(10, 0, 40, 40)]
    [InlineData(10, 10, 20, 40)]  // stride smaller than a row
    [InlineData(10, 10, 40, 399)] // buffer too small for the rectangle
    public void Encode_RefusesInconsistentArguments(int w, int h, int stride, int bufferLength)
    {
        if (!Windows) return;
        Assert.ThrowsAny<ArgumentException>(() => new GdiJpegCodec().Encode(new byte[bufferLength], w, h, stride, 80));
    }

    [Fact]
    public void Decode_RefusesADestinationThatIsTooSmall()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        var jpeg = codec.Encode(Desktop(64, 64), 64, 64, 64 * 4, 80);
        Assert.Throws<ArgumentException>(() => codec.Decode(jpeg, 64, 64, new byte[64 * 63 * 4], 64 * 4));
    }

    [Fact]
    public void Decode_RefusesAPictureWhoseRealSizeIsNotTheExpectedOne()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        var jpeg = codec.Encode(Desktop(100, 100), 100, 100, 400, 80);
        Assert.Throws<InvalidDataException>(() => codec.Decode(jpeg, 64, 64, new byte[64 * 64 * 4], 64 * 4));
    }

    [Theory]
    [InlineData("random bytes")]
    [InlineData("png")]
    [InlineData("truncated jpeg")]
    [InlineData("jpeg header then garbage")]
    public void Decode_TurnsBadPictures_IntoInvalidData_NeverACrash(string kind)
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        var good = codec.Encode(Desktop(64, 64), 64, 64, 256, 80);
        byte[] bad = kind switch
        {
            "random bytes" => new Random(1).GetBytes(500),
            "png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0],
            "truncated jpeg" => good[..(good.Length / 3)],
            _ => good[..40].Concat(new Random(2).GetBytes(300)).ToArray()
        };

        try { codec.Decode(bad, 64, 64, new byte[64 * 64 * 4], 256); }
        catch (InvalidDataException) { /* the expected refusal */ }
        // (a lenient decoder may also render a partial picture; the point is that nothing else escapes)
    }

    [Fact]
    public void ManyThreads_CanEncodeAndDecodeAtOnce()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        var px = Desktop(160, 96);
        Parallel.For(0, 64, _ =>
        {
            var jpeg = codec.Encode(px, 160, 96, 640, 80);
            var back = new byte[160 * 96 * 4];
            codec.Decode(jpeg, 160, 96, back, 640);
            Assert.True(Psnr(px, back) > 25);
        });
    }

    // ── The whole lossy pipeline ─────────────────────────────────────────

    [Fact]
    public async Task LossyPipeline_StaysCloseToTheSource_ThroughKeyframeAndDeltas()
    {
        if (!Windows) return;
        var codec = new GdiJpegCodec();
        var enc = new TileDeltaJpegEncoder(codec) { Quality = new VideoQuality { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 } };
        var dec = new TileDeltaJpegDecoder(codec);
        const int w = 1024, h = 640;

        for (var step = 0; step < 6; step++)
        {
            var px = Desktop(w, h, shift: step * 3); // the "text" scrolls a little each frame
            using var decoded = await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)));
            Assert.True(Psnr(px, decoded.PixelData.ToArray()) > 26, $"frame {step}: {Psnr(px, decoded.PixelData.ToArray()):F1} dB");
        }
    }

    // ── Real screen, measured in memory only ─────────────────────────────

    [Fact]
    public async Task RealDesktop_Measurements()
    {
        if (!Windows) return;
        await using var capture = new GdiScreenCapture(fps: 30);
        await capture.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var codec = new GdiJpegCodec();
        var enc = new TileDeltaJpegEncoder(codec) { Quality = new VideoQuality { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 } };
        var dec = new TileDeltaJpegDecoder(codec);

        var keyBytes = 0L; var deltaBytes = 0L; var deltas = 0;
        double keyMs = 0, deltaMs = 0, decodeMs = 0;
        int frames = 0, width = 0, height = 0;
        await foreach (var f in capture.GetFramesAsync(cts.Token))
        {
            using (f)
            {
                width = f.Width; height = f.Height;
                var sw = Stopwatch.StartNew();
                var encoded = await enc.EncodeAsync(f);
                var encMs = sw.Elapsed.TotalMilliseconds;
                if (encoded.IsEmpty) { deltaMs += encMs; deltas++; frames++; if (frames == 40) break; continue; } // nothing changed: nothing is sent, nothing to decode
                sw.Restart();
                using var decoded = await dec.DecodeAsync(encoded);
                decodeMs += sw.Elapsed.TotalMilliseconds;

                if (enc.LastFrameWasKeyframe) { keyBytes += encoded.Length; keyMs += encMs; }
                else { deltaBytes += encoded.Length; deltaMs += encMs; deltas++; }
                Assert.Equal(f.Width, decoded.Width);
            }
            if (++frames == 40) break;
        }
        await capture.StopAsync();

        output.WriteLine($"real screen {width}x{height}: keyframe {keyBytes / 1024} KB in {keyMs:F0} ms; " +
                         $"{deltas} deltas avg {(deltas == 0 ? 0 : deltaBytes / deltas / 1024)} KB, encode {(deltas == 0 ? 0 : deltaMs / deltas):F1} ms; " +
                         $"decode avg {decodeMs / frames:F1} ms/frame");
        Assert.True(keyBytes > 0);
    }

    /// <summary>
    /// The case that decides whether the codec is worth having: the whole screen changes every frame (scrolling a
    /// page, playing a video). Uses a real screen picture, shifted a few pixels per frame. Measured in memory only.
    /// </summary>
    [Fact]
    public async Task FullScreenMotion_RealPicture_LossyVersusLossless()
    {
        if (!Windows) return;
        await using var capture = new GdiScreenCapture(fps: 30);
        await capture.StartAsync();
        CaptureFrame? first = null;
        await foreach (var f in capture.GetFramesAsync()) { first = f; break; }
        await capture.StopAsync();
        Assert.NotNull(first);
        using var source = first;

        int w = source.Width, h = source.Height, stride = w * 4;
        var original = source.PixelData.ToArray();
        var q = new VideoQuality { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 };
        var lossy = new TileDeltaJpegEncoder(new GdiJpegCodec()) { Quality = q };
        var lossless = new TileDeltaVideoEncoder { Quality = q };

        long lossyBytes = 0, losslessBytes = 0;
        double lossyMs = 0, losslessMs = 0;
        const int frames = 12;
        for (var i = 0; i < frames; i++)
        {
            // Scroll the picture up by 7 px per frame (wrapping), so every tile changes.
            var shifted = new byte[original.Length];
            var shift = (i * 7) % h;
            Array.Copy(original, shift * stride, shifted, 0, (h - shift) * stride);
            Array.Copy(original, 0, shifted, (h - shift) * stride, shift * stride);
            var frame = Frame(w, h, shifted);

            var sw = Stopwatch.StartNew();
            var a = await lossy.EncodeAsync(frame);
            lossyMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var b = await lossless.EncodeAsync(frame);
            losslessMs += sw.Elapsed.TotalMilliseconds;
            if (i > 0) { lossyBytes += a.Length; losslessBytes += b.Length; } // frame 0 is the keyframe
        }

        var n = frames - 1;
        double Mbit(long bytes) => bytes / (double)n * 20 * 8 / 1_000_000; // at 20 fps
        output.WriteLine($"full-screen motion {w}x{h}: lossless {losslessBytes / n / 1024} KB/frame = {Mbit(losslessBytes):F1} Mbit/s ({losslessMs / frames:F0} ms/frame); " +
                         $"lossy {lossyBytes / n / 1024} KB/frame = {Mbit(lossyBytes):F1} Mbit/s ({lossyMs / frames:F0} ms/frame)");

        Assert.True(lossyBytes < losslessBytes, "lossy must beat lossless when the whole screen changes");
    }

    [Fact]
    public async Task Sweep_QualityAndScale_OnARealPicture()
    {
        if (!Windows) return;
        await using var capture = new GdiScreenCapture(fps: 30);
        await capture.StartAsync();
        CaptureFrame? first = null;
        await foreach (var f in capture.GetFramesAsync()) { first = f; break; }
        await capture.StopAsync();
        using var source = first!;
        var codec = new GdiJpegCodec();
        var original = source.PixelData.ToArray(); // once, not per pixel
        foreach (var scale in new[] { 100, 75, 50 })
            foreach (var quality in new[] { 30, 50, 70, 80 })
            {
                int w = source.Width * scale / 100, h = source.Height * scale / 100;
                var px = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        Array.Copy(original, ((y * 100 / scale) * source.Width + x * 100 / scale) * 4, px, (y * w + x) * 4, 4);
                var sw = Stopwatch.StartNew();
                var jpeg = codec.Encode(px, w, h, w * 4, quality);
                output.WriteLine($"scale {scale,3}% q{quality}: {jpeg.Length / 1024} KB/frame = {jpeg.Length * 20 * 8 / 1e6:F1} Mbit/s at 20 fps ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            }
    }
}

internal static class RandomExtensions
{
    public static byte[] GetBytes(this Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }
}
