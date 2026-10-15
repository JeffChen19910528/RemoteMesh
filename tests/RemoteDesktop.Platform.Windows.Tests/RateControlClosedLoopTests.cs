using System.Diagnostics;
using System.Runtime.CompilerServices;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Streaming;
using RemoteDesktop.Transport;
using Xunit.Abstractions;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The real encoder + real JPEG + the rate controller against a slow link, in real time: does a screen that changes
/// completely every frame end up inside the bandwidth budget, with the picture still moving?
/// The picture is a real screen grab, held in memory only.
/// </summary>
public sealed class RateControlClosedLoopTests(ITestOutputHelper output)
{
    /// <summary>
    /// A link of fixed capacity, modelled with a virtual clock: every byte occupies the link for bytes / capacity
    /// seconds, and the sender only waits once it is more than a small buffer ahead. (One Task.Delay per small packet
    /// would be rounded up to the ~15 ms OS timer tick and silently throttle the link far below its capacity.)
    /// </summary>
    private sealed class SlowLink(int capacityBitsPerSecond) : IChannel
    {
        private static readonly TimeSpan Buffer = TimeSpan.FromMilliseconds(40);
        private readonly object _lock = new();
        private readonly List<(long Ms, int Bytes)> _log = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _linkFreeAt;

        public Guid ChannelId { get; } = Guid.NewGuid();
        public ChannelType Type => ChannelType.Desktop;
        public bool IsOpen => true;

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = _clock.Elapsed;
                var start = _linkFreeAt > now ? _linkFreeAt : now;
                _linkFreeAt = start + TimeSpan.FromSeconds(data.Length * 8.0 / capacityBitsPerSecond);
                _log.Add(((long)_linkFreeAt.TotalMilliseconds, data.Length)); // the bytes have crossed when the link frees up
                wait = _linkFreeAt - now - Buffer;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public double MbitPerSecond(long fromMs, long toMs)
        {
            lock (_lock) return _log.Where(e => e.Ms >= fromMs && e.Ms < toMs).Sum(e => (long)e.Bytes) * 8.0 / ((toMs - fromMs) / 1000.0) / 1e6;
        }

        public long ElapsedMs => _clock.ElapsedMilliseconds;
    }

    /// <summary>The whole picture scrolls a little every frame, so every tile changes.</summary>
    private sealed class ScrollingCapture(byte[] baseline, int width, int height, int fps) : IScreenCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; return Task.CompletedTask; }

        public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            var stride = width * 4;
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                var shifted = new byte[baseline.Length];
                var shift = (i * 7) % height;
                Array.Copy(baseline, shift * stride, shifted, 0, (height - shift) * stride);
                Array.Copy(baseline, 0, shifted, (height - shift) * stride, shift * stride);
                yield return new CaptureFrame { Width = width, Height = height, PixelData = shifted, FrameNumber = i };
                await Task.Delay(1000 / fps, ct);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(byte[] Pixels, int W, int H)> RealPicture()
    {
        await using var capture = new GdiScreenCapture(fps: 30);
        await capture.StartAsync();
        await foreach (var f in capture.GetFramesAsync())
        {
            using (f) { await capture.StopAsync(); return (f.PixelData.ToArray(), f.Width, f.Height); }
        }
        throw new InvalidOperationException("no frame");
    }

    [Fact]
    public async Task AFullScreenChange_OnASlowLink_SettlesInsideTheBudget_AndKeepsMoving()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (pixels, w, h) = await RealPicture();

        const int budgetKbps = 5000;
        var link = new SlowLink(capacityBitsPerSecond: 6_000_000); // a 6 Mbit/s uplink
        var rate = new RateController(budgetKbps);
        var sender = new DesktopStreamSender(new ScrollingCapture(pixels, w, h, fps: 30),
            new TileDeltaJpegEncoder(new GdiJpegCodec()), link, rate);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        await sender.RunAsync(cts.Token);

        var total = link.ElapsedMs;
        var early = link.MbitPerSecond(0, 3000);
        var settled = link.MbitPerSecond(total - 4000, total);
        output.WriteLine($"{w}x{h} full-screen motion over a 6 Mbit/s link, budget 5 Mbit/s: " +
                         $"first 3 s {early:F1} Mbit/s, last 4 s {settled:F1} Mbit/s; frames sent {sender.FramesSent}, skipped {sender.FramesSkipped}; " +
                         $"ended on rung {rate.Index} = {rate.Current}");

        Assert.True(rate.Index > 0, "the controller never reacted");
        Assert.True(settled <= 5.0 * 1.3, $"settled at {settled:F1} Mbit/s, above the budget");
        Assert.True(sender.FramesSent >= 40, "the picture practically stalled");
    }
}
