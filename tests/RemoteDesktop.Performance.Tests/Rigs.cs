using System.Diagnostics;
using System.Runtime.CompilerServices;
using RemoteDesktop.Capture;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Streaming;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Performance.Tests;

/// <summary>Desktop-like test image: flat areas, window-like rectangles, text-like noise and a gradient.</summary>
public static class DesktopFrame
{
    public static CaptureFrame Make(int w, int h, int seed = 1)
    {
        var rng = new Random(seed);
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 235; px[i + 1] = 235; px[i + 2] = 235; px[i + 3] = 255; }

        for (var r = 0; r < 40; r++) // "windows"
        {
            var rw = rng.Next(w / 8, w / 2);
            var rh = rng.Next(h / 8, h / 2);
            var rx = rng.Next(0, w - rw);
            var ry = rng.Next(0, h - rh);
            var (b, g, rr) = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            for (var y = ry; y < ry + rh; y++)
                for (var x = rx; x < rx + rw; x++)
                {
                    var i = (y * w + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = rr;
                }
        }

        for (var line = 0; line < h / 12; line++) // "text": short high-contrast noisy runs
        {
            var y = line * 12 + 3;
            for (var x = 20; x < w - 20; x++)
                if (rng.Next(3) != 0)
                {
                    var v = (byte)(rng.Next(2) == 0 ? 20 : 240);
                    for (var dy = 0; dy < 6 && y + dy < h; dy++)
                    {
                        var i = ((y + dy) * w + x) * 4;
                        px[i] = v; px[i + 1] = v; px[i + 2] = v;
                    }
                }
        }

        for (var y = 0; y < h / 10; y++) // gradient bar (photo-like)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / w); px[i + 1] = (byte)(y * 255 / (h / 10)); px[i + 2] = (byte)((x + y) & 255);
            }

        return new CaptureFrame { Width = w, Height = h, PixelData = px };
    }
}

/// <summary>Yields one pre-rendered frame at a fixed rate, stamping when each frame was "captured".</summary>
public sealed class PacedCapture(IReadOnlyList<CaptureFrame> frames, int fps) : IScreenCapture
{
    public List<long> Stamps { get; } = new();
    public bool IsCapturing { get; private set; }
    public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; return Task.CompletedTask; }

    public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        long n = 0;
        while (IsCapturing && !ct.IsCancellationRequested)
        {
            Stamps.Add(Stopwatch.GetTimestamp());
            var src = frames[(int)(n % frames.Count)];
            yield return new CaptureFrame { Width = src.Width, Height = src.Height, PixelData = src.PixelData, FrameNumber = n++ };
            if (!await timer.WaitForNextTickAsync(ct)) yield break;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class TimingRenderer : IFrameRenderer
{
    public List<long> Stamps { get; } = new();
    public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
    {
        Stamps.Add(Stopwatch.GetTimestamp());
        return Task.CompletedTask;
    }
}

/// <summary>Helpers to build end-to-end encrypted channel pairs over in-memory loopback.</summary>
public static class Rig
{
    public sealed class Link : IAsyncDisposable
    {
        public required SecureChannel A { get; init; }
        public required SecureChannel B { get; init; }
        public required LoopbackChannel InnerA { get; init; }
        public required LoopbackChannel InnerB { get; init; }
        public required AesGcmSessionCrypto CryptoA { get; init; }
        public required AesGcmSessionCrypto CryptoB { get; init; }

        public async ValueTask DisposeAsync()
        {
            await InnerA.DisposeAsync();
            await InnerB.DisposeAsync();
            CryptoA.Dispose();
            CryptoB.Dispose();
        }
    }

    public static async Task<Link> SecureLinkAsync(ChannelType type)
    {
        var session = SessionId.New();
        var ka = DeviceKeyGenerator.Generate();
        var kb = DeviceKeyGenerator.Generate();
        var ca = new AesGcmSessionCrypto();
        var cb = new AesGcmSessionCrypto();
        await ca.DeriveSessionKeyAsync(session, ka.PrivateKey, kb.PublicKey);
        await cb.DeriveSessionKeyAsync(session, kb.PrivateKey, ka.PublicKey);

        var ia = new LoopbackChannel(type);
        var ib = new LoopbackChannel(type);
        ia.PeerWriter = ib.Writer;
        ib.PeerWriter = ia.Writer;
        return new Link
        {
            A = new SecureChannel(ia, ca, session, isInitiator: true),
            B = new SecureChannel(ib, cb, session, isInitiator: false),
            InnerA = ia, InnerB = ib, CryptoA = ca, CryptoB = cb
        };
    }

    public sealed record StreamResult(int Captured, int Rendered, Samples EndToEndMs, long WireBytes);

    /// <summary>Runs capture → encode → packetize → encrypt → loopback → decrypt → reassemble → decode → render in real time.</summary>
    public static async Task<StreamResult> StreamAsync(IReadOnlyList<CaptureFrame> frames, int fps, VideoQuality quality,
        TimeSpan duration, bool tileDelta = true)
    {
        await using var link = await SecureLinkAsync(ChannelType.Desktop);
        var capture = new PacedCapture(frames, fps);
        var renderer = new TimingRenderer();
        IVideoEncoder encoder = tileDelta ? new TileDeltaVideoEncoder { Quality = quality } : new DeflateVideoEncoder { Quality = quality };
        IVideoDecoder decoder = tileDelta ? new TileDeltaVideoDecoder() : new DeflateVideoDecoder();
        var sender = new DesktopStreamSender(capture, encoder, link.A);
        var receiver = new DesktopStreamReceiver(link.B, decoder, renderer);

        using var cts = new CancellationTokenSource(duration);
        var rx = receiver.RunAsync();
        await sender.RunAsync(cts.Token);

        // Let the receiver drain what is in flight, then end its stream.
        var drain = Stopwatch.StartNew();
        while (renderer.Stamps.Count < capture.Stamps.Count - 1 && drain.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        await link.InnerB.DisposeAsync();
        await rx;

        var lat = new Samples();
        var n = Math.Min(renderer.Stamps.Count, capture.Stamps.Count);
        for (var i = 0; i < n; i++) lat.AddTicks(renderer.Stamps[i] - capture.Stamps[i]);
        return new StreamResult(capture.Stamps.Count, renderer.Stamps.Count, lat, sender.BytesSent);
    }
}

/// <summary>Realistic desktop activity: a window region changes each frame, the rest stays put.</summary>
public static class Scenes
{
    private static readonly Lazy<IReadOnlyList<CaptureFrame>> InteractiveFrames = new(() =>
    {
        var desk = DesktopFrame.Make(1920, 1080);
        return Enumerable.Range(0, 20).Select(i =>
        {
            var px = desk.PixelData.ToArray();
            for (var y = 0; y < 240; y++)
                for (var x = 0; x < 700; x++)
                {
                    var o = ((200 + y) * 1920 + 300 + x) * 4;
                    var v = (byte)((x * 3 + y * 5 + i * 17) & 0xFF);
                    px[o] = v; px[o + 1] = v; px[o + 2] = v;
                }
            return new CaptureFrame { Width = 1920, Height = 1080, PixelData = px };
        }).ToList();
    });

    public static IReadOnlyList<CaptureFrame> Interactive => InteractiveFrames.Value;
}
