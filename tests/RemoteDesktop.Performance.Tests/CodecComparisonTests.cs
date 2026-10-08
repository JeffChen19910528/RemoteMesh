using System.Diagnostics;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using Xunit.Abstractions;

namespace RemoteDesktop.Performance.Tests;

[Collection("perf")]
[Trait("Category", "Performance")]
public sealed class CodecComparisonTests(ITestOutputHelper output)
{
    private static CaptureFrame Paint(CaptureFrame baseFrame, Action<byte[], int> edit)
    {
        var px = baseFrame.PixelData.ToArray();
        edit(px, baseFrame.Width);
        return new CaptureFrame { Width = baseFrame.Width, Height = baseFrame.Height, PixelData = px };
    }

    private static void Rect(byte[] px, int w, int x0, int y0, int rw, int rh, Func<int, int, byte> value)
    {
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                var i = ((y0 + y) * w + x0 + x) * 4;
                var v = value(x, y);
                px[i] = v; px[i + 1] = v; px[i + 2] = v;
            }
    }

    /// <summary>name → frame generator (frame index → frame).</summary>
    private static IEnumerable<(string Name, Func<int, CaptureFrame> Make)> Scenarios()
    {
        var desk = DesktopFrame.Make(1920, 1080);
        var rng = new Random(11);

        // Idle desktop: only the mouse cursor moves.
        yield return ("idle desktop (cursor moves)", i => Paint(desk, (px, w) =>
            Rect(px, w, 200 + i * 7, 300 + i * 3, 12, 20, (_, _) => 0)));

        // Working: a window region of ~8% of the screen changes every frame (typing, scrolling a page).
        yield return ("interactive (≈8% of screen changes)", i => Paint(desk, (px, w) =>
            Rect(px, w, 300, 200, 700, 240, (x, y) => (byte)((x * 3 + y * 5 + i * 17) & 0xFF))));

        // Full-screen video: every pixel changes every frame (worst case for any delta codec).
        yield return ("full-screen video (everything changes)", i =>
        {
            var r = new Random(i);
            var f = Paint(desk, (px, _) => { });
            var span = ((byte[])MemoryMarshalGet(f)).AsSpan();
            r.NextBytes(span);
            return f;
        });
    }

    private static byte[] MemoryMarshalGet(CaptureFrame f) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(f.PixelData, out var seg) ? seg.Array! : throw new InvalidOperationException();

    [Fact]
    public async Task Codecs_BandwidthAndCpu_AcrossRealisticScenarios()
    {
        var quality = VideoQuality.Poor; // 1280x720 @ 20 fps
        const int frames = 30;

        foreach (var (name, make) in Scenarios())
        {
            var inputs = Enumerable.Range(0, frames + 1).Select(make).ToList();

            async Task<(double ms, double kib, double keyKib)> Run(IVideoEncoder enc)
            {
                var first = await enc.EncodeAsync(inputs[0]);
                var sw = new Stopwatch();
                long bytes = 0;
                for (var i = 1; i <= frames; i++)
                {
                    sw.Start();
                    var e = await enc.EncodeAsync(inputs[i]);
                    sw.Stop();
                    bytes += e.Length;
                }
                return (sw.Elapsed.TotalMilliseconds / frames, bytes / frames / 1024.0, first.Length / 1024.0);
            }

            await using var deflate = new DeflateVideoEncoder { Quality = quality };
            await using var tile = new TileDeltaVideoEncoder { Quality = quality };
            var d = await Run(deflate);
            var t = await Run(tile);

            double Mbit(double kib) => kib * 1024 * 8 * quality.Fps / 1e6;
            Perf.Report(output, "codec",
                $"{name} @720p/{quality.Fps}fps: Deflate {d.kib:F0} KiB/frame = {Mbit(d.kib):F1} Mbit/s, {d.ms:F1} ms/frame | " +
                $"TileDelta {t.kib:F0} KiB/frame = {Mbit(t.kib):F1} Mbit/s, {t.ms:F1} ms/frame (keyframe {t.keyKib:F0} KiB) | " +
                $"bandwidth {(1 - t.kib / d.kib) * 100:F0}% lower");

            if (name.StartsWith("idle"))
                Assert.True(t.kib < d.kib * 0.05, $"idle desktop should cost <5% of intra-only: {t.kib:F1} vs {d.kib:F1} KiB");
            if (name.StartsWith("interactive"))
                Assert.True(t.kib < d.kib * 0.5, $"interactive should cost <50%: {t.kib:F1} vs {d.kib:F1} KiB");
            if (name.StartsWith("full-screen"))
                Assert.True(t.kib < d.kib * 1.15, $"worst case must not blow up: {t.kib:F1} vs {d.kib:F1} KiB");
        }
    }
}
