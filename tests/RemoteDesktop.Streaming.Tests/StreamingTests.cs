using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Streaming.Tests;

public sealed class StreamingTests
{
    private sealed class CollectingRenderer : IFrameRenderer
    {
        public List<CaptureFrame> Frames { get; } = new();
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }
    }

    private static CaptureFrame Frame(int w, int h) => new()
    {
        Width = w, Height = h,
        PixelData = Enumerable.Range(0, w * h * 4).Select(i => (byte)(i % 251)).ToArray()
    };

    // ── Packetization ────────────────────────────────────────────────────

    [Fact]
    public void Packetizer_SplitsAndReassembles()
    {
        var data = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        var packets = FramePacketizer.Split(7, 4, true, 100, 50, data, maxPayload: 4096).ToList();
        Assert.Equal(13, packets.Count);

        var r = new FrameReassembler();
        ReassembledFrame? done = null;
        foreach (var p in Enumerable.Reverse(packets)) // out of order
            done = r.Add(p) ?? done;

        Assert.NotNull(done);
        Assert.Equal(data, done!.Data);
        Assert.Equal(7, done.FrameNumber);
        Assert.Equal(100, done.Width);
    }

    [Fact]
    public void Reassembler_DropsIncompleteOlderFrame()
    {
        var r = new FrameReassembler();
        var lost = FramePacketizer.Split(1, 4, true, 1, 1, new byte[100], 40).ToList();
        r.Add(lost[0]); // frame 1 never completes
        var ok = FramePacketizer.Split(2, 4, true, 1, 1, new byte[10], 40).Single();

        Assert.NotNull(r.Add(ok));
        Assert.Equal(1, r.DroppedFrames);
        Assert.Null(r.Add(lost[1])); // stale chunk ignored
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void FramePacket_RejectsTruncated(int length) =>
        Assert.False(FramePacket.TryParse(new byte[length], out _));

    [Fact]
    public void FramePacket_RejectsBadChunkIndex()
    {
        var good = FramePacketizer.Split(1, 0, true, 1, 1, new byte[4]).Single();
        good[11] = 5; // chunkIndex = 5 >= count 1
        Assert.False(FramePacket.TryParse(good, out _));
    }

    // ── Codec ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codec_RoundTrips_AndCompresses()
    {
        var frame = Frame(64, 48);
        await using var enc = new DeflateVideoEncoder { Quality = VideoQuality.Excellent };
        var encoded = await enc.EncodeAsync(frame);
        Assert.True(encoded.Length < frame.PixelData.Length);

        await using var dec = new DeflateVideoDecoder();
        var decoded = await dec.DecodeAsync(encoded);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(frame.PixelData.ToArray(), decoded.PixelData.ToArray());
    }

    [Fact]
    public async Task Encoder_DownscalesToQuality()
    {
        await using var enc = new DeflateVideoEncoder { Quality = VideoQuality.VeryPoor };
        var encoded = await enc.EncodeAsync(Frame(1920, 1080));
        await using var dec = new DeflateVideoDecoder();
        var decoded = await dec.DecodeAsync(encoded);
        Assert.Equal(960, decoded.Width);
        Assert.Equal(540, decoded.Height);
    }

    [Fact]
    public async Task Decoder_RejectsGarbageAndOversize()
    {
        await using var dec = new DeflateVideoDecoder();
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(new byte[] { 0, 1, 0, 1, 9, 9 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 }));
    }

    // ── Adaptive quality ─────────────────────────────────────────────────

    [Theory]
    [InlineData(20, 0, 20000, QualityPreset.Excellent)]
    [InlineData(150, 0, 20000, QualityPreset.Good)]
    [InlineData(250, 0, 20000, QualityPreset.Poor)]
    [InlineData(50, 12, 20000, QualityPreset.VeryPoor)]
    [InlineData(50, 0, 500, QualityPreset.VeryPoor)]
    public void Recommend_MapsStatsToPreset(double rtt, double loss, double bw, QualityPreset expected) =>
        Assert.Equal(expected, AdaptiveQualityController.Recommend(new NetworkStats(rtt, loss, bw)));

    [Fact]
    public void Controller_DegradesImmediately_UpgradesWithHysteresis()
    {
        var c = new AdaptiveQualityController(QualityPreset.Excellent, upgradeAfterSamples: 3);
        Assert.True(c.Update(new NetworkStats(500, 0, 20000)));
        Assert.Equal(QualityPreset.VeryPoor, c.Preset);

        var good = new NetworkStats(10, 0, 20000);
        Assert.False(c.Update(good));
        Assert.False(c.Update(good));
        Assert.True(c.Update(good));
        Assert.Equal(QualityPreset.Poor, c.Preset); // one tier at a time
    }

    // ── End to end ───────────────────────────────────────────────────────

    private static (LoopbackChannel host, LoopbackChannel viewer) Pair()
    {
        var host = new LoopbackChannel(ChannelType.Desktop);
        var viewer = new LoopbackChannel(ChannelType.Desktop);
        host.PeerWriter = viewer.Writer;
        return (host, viewer);
    }

    [Fact]
    public async Task Stream_CaptureToRenderer_EndToEnd()
    {
        var (hostCh, viewerCh) = Pair();
        var renderer = new CollectingRenderer();
        var sender = new DesktopStreamSender(new SyntheticScreenCapture(128, 72, 200, maxFrames: 5),
            new DeflateVideoEncoder { Quality = VideoQuality.Excellent }, hostCh);
        var receiver = new DesktopStreamReceiver(viewerCh, new DeflateVideoDecoder(), renderer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rx = receiver.RunAsync(cts.Token);
        await sender.RunAsync(cts.Token);
        while (receiver.FramesRendered < 5 && !cts.IsCancellationRequested) await Task.Delay(10);
        await viewerCh.DisposeAsync();
        await rx;

        Assert.Equal(5, renderer.Frames.Count);
        Assert.All(renderer.Frames, f => Assert.Equal(128, f.Width));
        Assert.Equal(5, sender.FramesSent);
        Assert.Equal(0, receiver.FramesCorrupt);
    }

    [Fact]
    public async Task Receiver_SkipsCorruptFrames()
    {
        var (hostCh, viewerCh) = Pair();
        var renderer = new CollectingRenderer();
        var receiver = new DesktopStreamReceiver(viewerCh, new DeflateVideoDecoder(), renderer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rx = receiver.RunAsync(cts.Token);

        foreach (var p in FramePacketizer.Split(0, (byte)CodecType.DeflateReference, true, 1, 1,
                     new byte[] { 0, 1, 0, 1, 9, 9 }))
            await hostCh.SendAsync(p);
        await hostCh.SendAsync(new byte[] { 1, 2, 3 }); // malformed packet
        while (receiver.FramesCorrupt < 1 && !cts.IsCancellationRequested) await Task.Delay(10);
        await viewerCh.DisposeAsync();
        await rx;

        Assert.Equal(1, receiver.FramesCorrupt);
        Assert.Empty(renderer.Frames);
    }

    [Fact]
    public void Sender_RejectsNonDesktopChannel() =>
        Assert.Throws<ArgumentException>(() => new DesktopStreamSender(
            new SyntheticScreenCapture(1, 1), new DeflateVideoEncoder(), new LoopbackChannel(ChannelType.Input)));
}
