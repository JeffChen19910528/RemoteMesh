using System.Diagnostics;
using RemoteDesktop.Capture;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.Input;
using RemoteDesktop.Protocol;
using Xunit.Abstractions;

namespace RemoteDesktop.Performance.Tests;

[Collection("perf")]
[Trait("Category", "Performance")]
public sealed class MediaAndLatencyTests(ITestOutputHelper output)
{
    private sealed class Recorder : IInputController
    {
        public Action<InputEvent>? OnInject { get; set; }
        public long Count;
        public Task InjectAsync(InputEvent e, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            OnInject?.Invoke(e);
            return Task.CompletedTask;
        }
    }

    private static SessionInfo Connected() => new()
    {
        SessionId = SessionId.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        State = SessionState.Connected,
        Permissions = PermissionSet.FullControl()
    };

    [Fact]
    public void Machine_IsRecorded() => Perf.Report(output, "machine", Perf.Machine);

    // ── Encoder latency / bandwidth ──────────────────────────────────────

    [Theory]
    [InlineData(nameof(VideoQuality.VeryPoor), 960, 540, 15)]
    [InlineData(nameof(VideoQuality.Poor), 1280, 720, 20)]
    [InlineData(nameof(VideoQuality.Good), 1600, 900, 30)]
    [InlineData(nameof(VideoQuality.Excellent), 1920, 1080, 60)]
    public async Task Encoder_LatencyAndBandwidth_PerQualityTier(string tier, int w, int h, int targetFps)
    {
        var quality = tier switch
        {
            nameof(VideoQuality.VeryPoor) => VideoQuality.VeryPoor,
            nameof(VideoQuality.Poor) => VideoQuality.Poor,
            nameof(VideoQuality.Good) => VideoQuality.Good,
            _ => VideoQuality.Excellent
        };
        var source = DesktopFrame.Make(1920, 1080); // always capture 1080p; the encoder scales to the tier
        await using var enc = new DeflateVideoEncoder { Quality = quality };
        await using var dec = new DeflateVideoDecoder();

        for (var i = 0; i < 3; i++) await enc.EncodeAsync(source); // warm-up (JIT, pools)

        var encode = new Samples();
        var decode = new Samples();
        long bytes = 0;
        const int frames = 20;
        for (var i = 0; i < frames; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var encoded = await enc.EncodeAsync(source);
            var t1 = Stopwatch.GetTimestamp();
            await dec.DecodeAsync(encoded);
            var t2 = Stopwatch.GetTimestamp();
            encode.AddTicks(t1 - t0);
            decode.AddTicks(t2 - t1);
            bytes += encoded.Length;
        }

        var perFrame = bytes / frames;
        var mbitAtTarget = perFrame * 8.0 * targetFps / 1e6;
        Perf.Report(output, "encoder",
            $"{tier} {w}x{h}@{targetFps}: encode {encode} | decode {decode} | {perFrame / 1024.0:F0} KiB/frame → " +
            $"{mbitAtTarget:F1} Mbit/s at {targetFps} fps (tier budget {quality.BitrateKbps / 1000.0:F1} Mbit/s) | " +
            $"encoder can sustain {1000 / encode.Mean:F0} fps/core");

        Assert.True(encode.P(.95) < 250, $"encode p95 {encode.P(.95):F1}ms");
    }

    // ── End-to-end streaming: FPS, latency, CPU, memory ──────────────────

    [Theory]
    [InlineData(960, 540, 15, false)]
    [InlineData(1280, 720, 20, false)]
    [InlineData(960, 540, 15, true)]
    [InlineData(1280, 720, 20, true)]
    [InlineData(1280, 720, 30, true)]
    public async Task Streaming_RealTime_AchievesTargetFps_WithBoundedLatency(int w, int h, int fps, bool tileDelta)
    {
        var quality = w == 960 ? VideoQuality.VeryPoor : VideoQuality.Poor;
        using var scope = new ResourceScope();

        var r = await Rig.StreamAsync(Scenes.Interactive, fps, quality, TimeSpan.FromSeconds(5), tileDelta);

        var achieved = r.Rendered / scope.WallSeconds;
        Perf.Report(output, "streaming",
            $"{(tileDelta ? "TileDelta" : "Deflate")} {w}x{h}: rendered {r.Rendered}/{r.Captured} frames ({achieved:F1} fps) | e2e latency {r.EndToEndMs} | " +
            $"CPU {scope.CpuPercentOfOneCore:F0}% of one core | alloc {scope.AllocatedBytes / Math.Max(1, r.Captured) / 1048576.0:F1} MB/frame | " +
            $"wire {r.WireBytes / 1048576.0 / scope.WallSeconds * 8:F1} Mbit/s | peak WS +{scope.PeakWorkingSetGrowthMb:F0} MB");

        Assert.True(r.Rendered >= r.Captured * 0.95, $"dropped frames: {r.Rendered}/{r.Captured}");
        Assert.True(achieved >= fps * 0.8, $"achieved {achieved:F1} fps of {fps}");
        Assert.True(r.EndToEndMs.P(.95) < 400, $"e2e p95 {r.EndToEndMs.P(.95):F0}ms");
    }

    [Fact]
    public async Task Streaming_LongRun_DoesNotLeakMemory()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);

        for (var round = 0; round < 3; round++)
            await Rig.StreamAsync(Scenes.Interactive, 30, VideoQuality.VeryPoor, TimeSpan.FromSeconds(2));

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var growth = GC.GetTotalMemory(true) - before;
        Perf.Report(output, "memory", $"3 streaming sessions of 2s each: retained heap growth {growth / 1048576.0:F1} MB");

        Assert.True(growth < 50 * 1048576, $"retained {growth / 1048576.0:F0} MB after sessions ended");
    }

    // ── Latency: secure channel and input ────────────────────────────────

    [Fact]
    public async Task SecureChannel_RoundTripLatency()
    {
        await using var link = await Rig.SecureLinkAsync(ChannelType.Control);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var payload = new byte[64];

        var echo = Task.Run(async () =>
        {
            await foreach (var m in link.B.ReceiveAsync(cts.Token))
                await link.B.SendAsync(m, cts.Token);
        });

        await using var rx = link.A.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        for (var i = 0; i < 200; i++) // warm-up
        {
            await link.A.SendAsync(payload, cts.Token);
            await rx.MoveNextAsync();
        }

        var rtt = new Samples();
        for (var i = 0; i < 3000; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            await link.A.SendAsync(payload, cts.Token);
            await rx.MoveNextAsync();
            rtt.AddTicks(Stopwatch.GetTimestamp() - t0);
        }
        cts.Cancel();
        try { await echo; } catch (OperationCanceledException) { }

        Perf.Report(output, "latency", $"encrypted channel round trip (64 B, in-process): {rtt}");
        Assert.True(rtt.P(.99) < 25, $"p99 {rtt.P(.99):F2}ms");
    }

    [Fact]
    public async Task InputLatency_SendToInjection_ThroughEncryptedChannel()
    {
        await using var link = await Rig.SecureLinkAsync(ChannelType.Input);
        var recorder = new Recorder();
        var gate = new InputGate(Connected(), recorder, new InputGateOptions { MaxEventsPerSecond = int.MaxValue });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rx = new InputReceiver(link.B, gate).RunAsync(cts.Token);
        var tx = new InputSender(link.A);

        var arrived = new SemaphoreSlim(0);
        recorder.OnInject = _ => arrived.Release();

        var lat = new Samples();
        for (var i = 0; i < 2200; i++)
        {
            var e = new InputEvent { EventType = InputEventType.MouseMove, X = i % 1000, Y = 5 };
            var t0 = Stopwatch.GetTimestamp();
            await tx.SendAsync(e, cts.Token);
            await arrived.WaitAsync(cts.Token);
            if (i >= 200) lat.AddTicks(Stopwatch.GetTimestamp() - t0); // first 200 are warm-up
        }
        cts.Cancel();
        await rx;

        Perf.Report(output, "latency", $"input event send → injected (encrypted, validated, in-process): {lat}");
        Assert.True(lat.P(.99) < 25, $"p99 {lat.P(.99):F2}ms");
    }

    [Fact]
    public async Task InputGate_Throughput()
    {
        var recorder = new Recorder();
        var gate = new InputGate(Connected(), recorder,
            new InputGateOptions { MaxEventsPerSecond = int.MaxValue, ReplayWindow = 4096 });
        const int n = 200_000;
        var events = Enumerable.Range(0, n).Select(i =>
            new InputEvent { EventType = InputEventType.MouseMove, X = i % 1000, Y = 7 }).ToArray();

        using var scope = new ResourceScope();
        foreach (var e in events) await gate.ProcessAsync(e);

        var perSec = n / scope.WallSeconds;
        Perf.Report(output, "input", $"InputGate validate+inject throughput: {perSec:N0} events/s ({scope.WallSeconds * 1e6 / n:F2} µs/event)");
        Assert.Equal(n, recorder.Count);
        Assert.True(perSec > 50_000, $"{perSec:N0} events/s");
    }
}
