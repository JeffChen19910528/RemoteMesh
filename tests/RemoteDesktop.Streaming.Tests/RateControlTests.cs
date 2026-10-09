using System.Runtime.CompilerServices;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Streaming.Tests;

public sealed class RateControllerTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan d) => _ticks += d.Ticks;
    }

    private const int Budget = 4000; // kbit/s = 500,000 bytes/s

    /// <summary>Feeds one second of traffic: <paramref name="bytesPerSecond"/> sent in 20 frames, each taking <paramref name="sendMs"/>.</summary>
    private static bool Second(RateController c, ManualClock clock, long bytesPerSecond, double sendMs = 1)
    {
        var changed = false;
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            changed |= c.Report((int)(bytesPerSecond / 20), TimeSpan.FromMilliseconds(sendMs));
        }
        return changed;
    }

    [Fact]
    public void WithinBudget_NothingChanges()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, startIndex: 2, clock);
        for (var i = 0; i < 20; i++) Assert.False(Second(c, clock, 400_000));
        Assert.Equal(2, c.Index);
    }

    [Fact]
    public void OverBudget_StepsDown()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 0, clock);

        Assert.True(Second(c, clock, 650_000)); // 30 % over
        Assert.Equal(1, c.Index);
    }

    [Fact]
    public void FarOverBudget_SkipsRungs()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 0, clock);

        Assert.True(Second(c, clock, 1_500_000)); // 3x the budget
        Assert.Equal(2, c.Index);
    }

    [Fact]
    public void ASlowConnection_StepsDown_EvenIfTheBytesAreWithinBudget()
    {
        // Few bytes, but handing them over takes 45 of every 50 ms: the connection is saturated.
        var clock = new ManualClock();
        var c = new RateController(Budget, 0, clock);

        Assert.True(Second(c, clock, 300_000, sendMs: 45));
        Assert.True(c.Index >= 1);
    }

    [Fact]
    public void RightAfterAChange_TheNextWindowIsNotJudged()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 0, clock);
        Assert.True(Second(c, clock, 650_000));
        Assert.Equal(1, c.Index);

        Assert.False(Second(c, clock, 650_000)); // still mixed old/new settings: ignored
        Assert.Equal(1, c.Index);
        Assert.True(Second(c, clock, 650_000));  // now it counts
        Assert.Equal(2, c.Index);
    }

    [Fact]
    public void CalmForAWhile_StepsBackUp_ButSlowly()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 4, clock);

        for (var i = 0; i < 3; i++) Assert.False(Second(c, clock, 100_000)); // not yet
        Assert.True(Second(c, clock, 100_000));                              // the 4th calm second
        Assert.Equal(3, c.Index);

        Second(c, clock, 100_000); // cooldown window
        for (var i = 0; i < 3; i++) Assert.False(Second(c, clock, 100_000));
        Assert.True(Second(c, clock, 100_000));
        Assert.Equal(2, c.Index);
    }

    [Fact]
    public void ABurstInTheMiddle_ResetsTheCalmStreak()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 3, clock);
        for (var i = 0; i < 3; i++) Second(c, clock, 100_000);
        Second(c, clock, 520_000); // between 60 % and 115 % of the budget: neither calm nor over
        for (var i = 0; i < 3; i++) Assert.False(Second(c, clock, 100_000));
        Assert.Equal(3, c.Index);
    }

    [Fact]
    public void NeverLeavesTheLadder()
    {
        var clock = new ManualClock();
        var c = new RateController(Budget, 0, clock);
        for (var i = 0; i < 60; i++) Second(c, clock, 5_000_000);
        Assert.Equal(RateController.Ladder.Count - 1, c.Index);

        for (var i = 0; i < 400; i++) Second(c, clock, 0);
        Assert.Equal(0, c.Index);
    }

    [Fact]
    public void TheLadder_OnlyEverGetsCheaper()
    {
        var steps = RateController.Ladder;
        for (var i = 1; i < steps.Count; i++)
        {
            // each rung reduces at least one of: picture size, quality, frame rate — and raises none of them
            Assert.True(steps[i].ScalePercent <= steps[i - 1].ScalePercent);
            Assert.True(steps[i].JpegQuality <= steps[i - 1].JpegQuality);
            Assert.True(steps[i].Fps <= steps[i - 1].Fps);
            Assert.NotEqual(steps[i - 1], steps[i]);
        }
    }

    [Fact]
    public void TooSmallABudget_IsRefused() => Assert.Throws<ArgumentOutOfRangeException>(() => new RateController(50));
}

public sealed class SenderRateTests
{
    private sealed class CountingFrames(int count, int w = 128, int h = 96) : IScreenCapture
    {
        public int Disposed;
        public int Started, Stopped;
        public bool IsCapturing { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; Started++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; Stopped++; return Task.CompletedTask; }

        private sealed class Owner(CountingFrames parent) : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Increment(ref parent.Disposed); }
        }

        public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                yield return new CaptureFrame { Width = w, Height = h, PixelData = new byte[w * h * 4], Owner = new Owner(this) };
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingEncoder : IVideoEncoder, IRateAdaptive
    {
        public List<RateStep> Applied { get; } = new();
        public CodecType Codec => CodecType.TileDeltaJpeg;
        public VideoQuality Quality { get; set; } = VideoQuality.Good;
        public Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            var b = new byte[16];
            b[0] = (byte)(frame.Width >> 8); b[1] = (byte)frame.Width; b[2] = (byte)(frame.Height >> 8); b[3] = (byte)frame.Height;
            return Task.FromResult<ReadOnlyMemory<byte>>(b);
        }
        public void Apply(RateStep step, int sourceWidth, int sourceHeight) => Applied.Add(step);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan d) => _ticks += d.Ticks;
    }

    [Fact]
    public async Task EveryCapturedFrame_IsReleasedBackToThePool_SentOrSkipped()
    {
        var capture = new CountingFrames(25);
        var sender = new DesktopStreamSender(capture, new DeflateVideoEncoder(), new LoopbackChannel(ChannelType.Desktop));

        await sender.RunAsync();

        Assert.Equal(25, capture.Disposed);
        Assert.Equal(1, capture.Stopped);
    }

    [Fact]
    public async Task SkippedFrames_AreReleasedToo()
    {
        var capture = new CountingFrames(30);
        var clock = new ManualClock(); // time never advances: after the first frame everything is "too soon"
        var rate = new RateController(4000, 0, clock);
        var sender = new DesktopStreamSender(capture, new RecordingEncoder(), new LoopbackChannel(ChannelType.Desktop), rate, clock);

        await sender.RunAsync();

        Assert.Equal(1, sender.FramesSent);
        Assert.Equal(29, sender.FramesSkipped);
        Assert.Equal(30, capture.Disposed);
    }

    [Fact]
    public async Task TheCurrentRung_IsAppliedToTheEncoder_BeforeEachFrame()
    {
        var capture = new CountingFrames(3);
        var clock = new ManualClock();
        var rate = new RateController(4000, startIndex: 3, clock);
        var encoder = new RecordingEncoder();
        var sender = new DesktopStreamSender(capture, encoder, new LoopbackChannel(ChannelType.Desktop), rate, clock);

        // Make every frame arrive "on time" for the rung's frame rate.
        var running = sender.RunAsync();
        for (var i = 0; i < 20 && !running.IsCompleted; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
        await running;

        Assert.NotEmpty(encoder.Applied);
        Assert.All(encoder.Applied, s => Assert.Equal(RateController.Ladder[3], s));
        Assert.Equal(RateController.Ladder[3], sender.CurrentStep);
    }


    /// <summary>Advances the clock by a fixed time per frame it hands out, and per packet it is asked to send.</summary>
    private sealed class TimedCapture(ManualClock clock, int count, TimeSpan perFrame) : IScreenCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; return Task.CompletedTask; }
        public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                clock.Advance(perFrame);
                yield return new CaptureFrame { Width = 64, Height = 64, PixelData = new byte[64 * 64 * 4] };
                await Task.Yield();
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SlowChannel(ManualClock clock, TimeSpan perPacket) : IChannel
    {
        public Guid ChannelId { get; } = Guid.NewGuid();
        public ChannelType Type => ChannelType.Desktop;
        public bool IsOpen => true;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { clock.Advance(perPacket); return Task.CompletedTask; }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheFrameInterval_IsCountedFromTheStartOfTheLastFrame_NotFromTheEndOfItsSend()
    {
        // 20 fps = 50 ms between frames. Each frame is captured 20 ms after the previous one is done and takes
        // 40 ms to send: every frame already used up its interval, so none may be skipped. Counting from the end of
        // the send would add the 40 ms twice and drop every other frame.
        var clock = new ManualClock();
        var rate = new RateController(100_000, 0, clock); // generous budget: stays on rung 0 (20 fps)
        var sender = new DesktopStreamSender(new TimedCapture(clock, 8, TimeSpan.FromMilliseconds(20)), new RecordingEncoder(),
            new SlowChannel(clock, TimeSpan.FromMilliseconds(40)), rate, clock);

        await sender.RunAsync();

        Assert.Equal(8, sender.FramesSent);
        Assert.Equal(0, sender.FramesSkipped);
    }


    private sealed class IdleEncoder : IVideoEncoder
    {
        public CodecType Codec => CodecType.TileDeltaJpeg;
        public VideoQuality Quality { get; set; } = VideoQuality.Good;
        public int Calls;
        public Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            // frame 0 has content, everything after it is "unchanged"
            if (Calls++ == 0) { var b = new byte[16]; b[1] = 64; b[3] = 64; return Task.FromResult<ReadOnlyMemory<byte>>(b); }
            return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UnchangedFrames_AreNotSent_AndStillReleased()
    {
        var capture = new CountingFrames(12);
        var channel = new LoopbackChannel(ChannelType.Desktop);
        var sender = new DesktopStreamSender(capture, new IdleEncoder(), channel);

        await sender.RunAsync();

        Assert.Equal(1, sender.FramesSent);
        Assert.Equal(11, sender.FramesUnchanged);
        Assert.Equal(12, capture.Disposed);
    }

    [Fact]
    public async Task WithoutARateController_NothingIsSkipped()
    {
        var capture = new CountingFrames(10);
        var sender = new DesktopStreamSender(capture, new RecordingEncoder(), new LoopbackChannel(ChannelType.Desktop));
        await sender.RunAsync();
        Assert.Equal(10, sender.FramesSent);
        Assert.Equal(0, sender.FramesSkipped);
        Assert.Null(sender.CurrentStep);
    }
}

public sealed class RateAdaptiveEncoderTests
{
    private sealed class Stub : IJpegCodec
    {
        public int LastQuality;
        public List<(int W, int H)> Sizes { get; } = new();
        public byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality)
        {
            LastQuality = quality;
            Sizes.Add((width, height));
            var d = new byte[15];
            d[0] = 0xFF; d[1] = 0xD8; d[2] = 0xFF; d[3] = 0xC0; d[5] = 11; d[6] = 8;
            d[7] = (byte)(height >> 8); d[8] = (byte)height; d[9] = (byte)(width >> 8); d[10] = (byte)width;
            return d;
        }
        public void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride) { }
    }

    private static CaptureFrame Frame(int w, int h) => new() { Width = w, Height = h, PixelData = new byte[w * h * 4] };

    [Fact]
    public async Task AStep_ChangesTheJpegQuality_AndTheOutputSize_AndStartsWithAKeyframe()
    {
        var codec = new Stub();
        var enc = new TileDeltaJpegEncoder(codec);
        await enc.EncodeAsync(Frame(800, 600));
        Assert.Equal((800, 600), codec.Sizes[^1]);
        Assert.True(enc.LastFrameWasKeyframe);

        enc.Apply(new RateStep(50, 35, 10), 800, 600);
        var next = await enc.EncodeAsync(Frame(800, 600));

        Assert.Equal(35, codec.LastQuality);
        Assert.Equal((400, 300), codec.Sizes[^1]);        // half size
        Assert.True(enc.LastFrameWasKeyframe);            // a new size cannot be a delta
        Assert.Equal(400, (next.Span[0] << 8) | next.Span[1]);
    }

    [Fact]
    public async Task BackToFullScale_RestoresTheNativeSize()
    {
        var codec = new Stub();
        var enc = new TileDeltaJpegEncoder(codec);
        enc.Apply(new RateStep(50, 35, 10), 800, 600);
        await enc.EncodeAsync(Frame(800, 600));
        enc.Apply(new RateStep(100, 80, 20), 800, 600);
        await enc.EncodeAsync(Frame(800, 600));

        Assert.Equal((800, 600), codec.Sizes[^1]);
        Assert.Equal(80, codec.LastQuality);
    }

    [Fact]
    public async Task TinyScalesAreClamped_SoThePictureNeverCollapses()
    {
        var codec = new Stub();
        var enc = new TileDeltaJpegEncoder(codec);
        enc.Apply(new RateStep(1, 1, 1), 100, 100);
        await enc.EncodeAsync(Frame(100, 100));
        Assert.True(codec.Sizes[^1].W >= 16 && codec.Sizes[^1].H >= 16);
    }
}
