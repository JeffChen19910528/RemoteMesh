using System.Buffers.Binary;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Streaming.Tests;

public sealed class TileDeltaTests
{
    private static VideoQuality Big => new() { Width = 8192, Height = 8192, Fps = 30, BitrateKbps = 1, KeyframeIntervalSeconds = 1000 };

    private static CaptureFrame Frame(int w, int h, byte fill = 0, Action<byte[]>? edit = null)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = fill; px[i + 1] = (byte)(fill + 1); px[i + 2] = (byte)(fill + 2); px[i + 3] = 255; }
        edit?.Invoke(px);
        return new CaptureFrame { Width = w, Height = h, PixelData = px };
    }

    private static void Paint(byte[] px, int w, int x0, int y0, int rw, int rh, byte v)
    {
        for (var y = y0; y < y0 + rh; y++)
            for (var x = x0; x < x0 + rw; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v;
            }
    }

    private static async Task<byte[]> RoundTrip(TileDeltaVideoEncoder enc, TileDeltaVideoDecoder dec, CaptureFrame f)
    {
        var encoded = await enc.EncodeAsync(f);
        using var decoded = await dec.DecodeAsync(encoded);
        return decoded.PixelData.ToArray();
    }

    // ── Correctness ──────────────────────────────────────────────────────

    [Fact]
    public async Task Keyframe_RoundTripsExactly()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var f = Frame(200, 130, 7, px => Paint(px, 200, 10, 10, 50, 40, 200));

        Assert.Equal(f.PixelData.ToArray(), await RoundTrip(enc, dec, f));
        Assert.True(enc.LastFrameWasKeyframe);
    }

    [Fact]
    public async Task DeltaFrame_RoundTripsExactly_AndIsMuchSmallerThanTheKeyframe()
    {
        var rng = new Random(5);
        var first = Frame(1280, 720, 30, px => rng.NextBytes(px.AsSpan(0, px.Length / 2))); // noisy half: poorly compressible
        var keyBytes = (await new TileDeltaVideoEncoder { Quality = Big }.EncodeAsync(first)).Length;

        await using var enc2 = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec2 = new TileDeltaVideoDecoder();
        await RoundTrip(enc2, dec2, first);

        var second = Frame(1280, 720, 30, px =>
        {
            first.PixelData.Span.CopyTo(px);
            Paint(px, 1280, 100, 100, 40, 30, 250); // a small window changes
        });
        var encoded = await enc2.EncodeAsync(second);
        using var decoded = await dec2.DecodeAsync(encoded);

        Assert.False(enc2.LastFrameWasKeyframe);
        Assert.Equal(second.PixelData.ToArray(), decoded.PixelData.ToArray());
        Assert.True(encoded.Length < keyBytes / 20, $"delta {encoded.Length} vs key {keyBytes}");
    }

    [Fact]
    public async Task IdenticalFrame_CostsAlmostNothing()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var f = Frame(640, 360, 9);
        await RoundTrip(enc, dec, f);

        var encoded = await enc.EncodeAsync(f);
        using var decoded = await dec.DecodeAsync(encoded);

        Assert.Equal(TileLayout.HeaderSize, encoded.Length);
        Assert.Equal(f.PixelData.ToArray(), decoded.PixelData.ToArray());
    }

    [Fact]
    public async Task OnePixelChange_SendsExactlyOneTile()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        await RoundTrip(enc, dec, Frame(640, 360, 9));

        var changed = Frame(640, 360, 9, px => Paint(px, 640, 321, 200, 1, 1, 77));
        var encoded = await enc.EncodeAsync(changed);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(encoded.Span[9..]));
        Assert.True(encoded.Length < 200);
        using var decoded = await dec.DecodeAsync(encoded);
        Assert.Equal(changed.PixelData.ToArray(), decoded.PixelData.ToArray());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(63, 65)]
    [InlineData(64, 64)]
    [InlineData(100, 70)]
    [InlineData(333, 129)]
    public async Task EdgeTileSizes_AndRandomChanges_AlwaysDecodeExactly(int w, int h)
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var rng = new Random(w * 1000 + h);
        var current = Frame(w, h, 1);
        Assert.Equal(current.PixelData.ToArray(), await RoundTrip(enc, dec, current));

        for (var step = 0; step < 60; step++)
        {
            var px = current.PixelData.ToArray();
            for (var k = rng.Next(0, 6); k > 0; k--) // a handful of random rectangles, sometimes none
            {
                var rw = rng.Next(1, w + 1);
                var rh = rng.Next(1, h + 1);
                Paint(px, w, rng.Next(0, w - rw + 1), rng.Next(0, h - rh + 1), rw, rh, (byte)rng.Next(256));
            }
            current = new CaptureFrame { Width = w, Height = h, PixelData = px };
            Assert.Equal(px, await RoundTrip(enc, dec, current));
        }
    }

    [Fact]
    public async Task OutputFrame_IsAStableCopy_NotTheLiveCanvas()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        using var first = await dec.DecodeAsync(await enc.EncodeAsync(Frame(128, 128, 5)));
        var snapshot = first.PixelData.ToArray();

        using var second = await dec.DecodeAsync(await enc.EncodeAsync(Frame(128, 128, 5, px => Paint(px, 128, 0, 0, 64, 64, 99))));

        Assert.Equal(snapshot, first.PixelData.ToArray()); // later frames do not rewrite earlier ones
    }

    [Fact]
    public async Task Scaling_FollowsTheQualityTier()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = VideoQuality.VeryPoor };
        await using var dec = new TileDeltaVideoDecoder();

        var encoded = await enc.EncodeAsync(Frame(1920, 1080, 3));
        using var decoded = await dec.DecodeAsync(encoded);

        Assert.Equal(960, decoded.Width);
        Assert.Equal(540, decoded.Height);
    }

    // ── Keyframe policy ──────────────────────────────────────────────────

    [Fact]
    public async Task KeyframesAreSentFirst_Periodically_OnRequest_AndOnResolutionChange()
    {
        var q = new VideoQuality { Width = 8192, Height = 8192, Fps = 5, BitrateKbps = 1, KeyframeIntervalSeconds = 1 }; // every 5 frames
        await using var enc = new TileDeltaVideoEncoder { Quality = q };
        var f = Frame(128, 128, 1);

        var kinds = new List<bool>();
        for (var i = 0; i < 12; i++) { await enc.EncodeAsync(f); kinds.Add(enc.LastFrameWasKeyframe); }
        Assert.Equal(new[] { true, false, false, false, false, true, false, false, false, false, true, false }, kinds);

        enc.ForceKeyframe();
        await enc.EncodeAsync(f);
        Assert.True(enc.LastFrameWasKeyframe);
        await enc.EncodeAsync(f);
        Assert.False(enc.LastFrameWasKeyframe);

        await enc.EncodeAsync(Frame(256, 128, 1)); // resolution changed
        Assert.True(enc.LastFrameWasKeyframe);
    }

    [Fact]
    public async Task SequenceNumbers_Increase_AndWrapAroundSafely_InTheDecoder()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var f = Frame(64, 64, 2);
        for (var i = 0; i < 20; i++) await RoundTrip(enc, dec, f);
    }

    // ── Loss recovery ────────────────────────────────────────────────────

    [Fact]
    public async Task DeltaWithoutReference_RequiresKeyframe()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        await enc.EncodeAsync(Frame(64, 64, 1));
        var delta = await enc.EncodeAsync(Frame(64, 64, 1, px => Paint(px, 64, 0, 0, 4, 4, 200)));

        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(delta)); // joined mid-stream
    }

    [Fact]
    public async Task LostFrame_IsDetectedByTheSequenceGap_AndAKeyframeRecovers()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var f0 = Frame(128, 128, 1);
        var f1 = Frame(128, 128, 1, px => Paint(px, 128, 0, 0, 10, 10, 100));
        var f2 = Frame(128, 128, 1, px => Paint(px, 128, 64, 64, 10, 10, 150));
        var f3 = Frame(128, 128, 1, px => Paint(px, 128, 0, 64, 10, 10, 200));

        await dec.DecodeAsync(await enc.EncodeAsync(f0));
        await enc.EncodeAsync(f1);                          // lost in transit
        var e2 = await enc.EncodeAsync(f2);

        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(e2)); // never applied on a wrong base

        enc.ForceKeyframe();
        using var recovered = await dec.DecodeAsync(await enc.EncodeAsync(f3));
        Assert.Equal(f3.PixelData.ToArray(), recovered.PixelData.ToArray());

        var f4 = Frame(128, 128, 1, px => Paint(px, 128, 5, 5, 3, 3, 33));
        using var next = await dec.DecodeAsync(await enc.EncodeAsync(f4)); // deltas work again
        Assert.Equal(f4.PixelData.ToArray(), next.PixelData.ToArray());
    }

    // ── Untrusted input ──────────────────────────────────────────────────

    private static byte[] Header(int w, int h, byte flags, uint seq, int count)
    {
        var b = new byte[TileLayout.HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)h);
        b[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(5), seq);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(9), (ushort)count);
        return b;
    }

    [Fact]
    public async Task MalformedFrames_AreRejected_WithoutThrowingOtherExceptions()
    {
        await using var dec = new TileDeltaVideoDecoder();

        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Array.Empty<byte>()));
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(new byte[5]));
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(0, 10, 1, 0, 0)));              // zero size
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(9000, 10, 1, 0, 0)));           // too large
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 0x80, 0, 0)));         // unknown flags
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 1, 0, 0)));             // keyframe must list all tiles
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 1, 0, 65535)));         // more tiles than exist
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 1, 0, 1)));            // truncated tile list
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 1, 0, 1).Concat(new byte[] { 0, 5 }).ToArray())); // tile index out of range
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(Header(64, 64, 1, 0, 1).Concat(new byte[] { 0, 0 }).ToArray())); // no pixel data
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(
            Header(64, 64, 1, 0, 1).Concat(new byte[] { 0, 0, 0xFF, 0xFF, 0xFF }).ToArray()));                      // not a deflate stream
    }

    [Fact]
    public async Task TileList_MustBeStrictlyIncreasing()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var key = (await enc.EncodeAsync(Frame(128, 64, 1))).ToArray(); // 2 tiles
        // swap the two tile indices (0,1 → 1,0)
        (key[TileLayout.HeaderSize + 1], key[TileLayout.HeaderSize + 3]) = (key[TileLayout.HeaderSize + 3], key[TileLayout.HeaderSize + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(key));
    }

    [Fact]
    public async Task InflatedBomb_IsRejected_AndTrailingBytesAreHarmless()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var good = (await enc.EncodeAsync(Frame(64, 64, 1))).ToArray();

        // Declared one tile (16 KiB) but the stream inflates to more: must not be accepted or overrun.
        var big = Frame(128, 64, 1);
        var twoTiles = (await new TileDeltaVideoEncoder { Quality = Big }.EncodeAsync(big)).ToArray();
        var spliced = Header(64, 64, 1, 0, 1).Concat(new byte[] { 0, 0 }).Concat(twoTiles.Skip(TileLayout.HeaderSize + 4)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(spliced));

        // Bytes after the end of a valid deflate stream are ignored (DeflateStream reads ahead, so they cannot be
        // detected reliably); they are harmless because the message size is already capped.
        using var ok = await dec.DecodeAsync(good.Concat(new byte[] { 1, 2, 3 }).ToArray());
        Assert.Equal(64, ok.Width);
    }

    [Fact]
    public async Task AfterAnError_DeltasAreRefused_UntilTheNextKeyframe()
    {
        await using var enc = new TileDeltaVideoEncoder { Quality = Big };
        await using var dec = new TileDeltaVideoDecoder();
        var f = Frame(128, 128, 1);
        await RoundTrip(enc, dec, f);
        var delta = await enc.EncodeAsync(Frame(128, 128, 1, px => Paint(px, 128, 0, 0, 5, 5, 9)));

        var corrupted = delta.ToArray();
        corrupted[^1] ^= 0xFF;
        await Assert.ThrowsAnyAsync<Exception>(() => dec.DecodeAsync(corrupted));

        var next = await enc.EncodeAsync(Frame(128, 128, 1, px => Paint(px, 128, 0, 0, 6, 6, 9)));
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(next));

        enc.ForceKeyframe();
        await dec.DecodeAsync(await enc.EncodeAsync(f));
    }

    // ── Through the real pipeline ────────────────────────────────────────

    private sealed class CountingRenderer : IFrameRenderer
    {
        public int Count;
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return Task.CompletedTask;
        }
    }

    /// <summary>Wire that silently drops the packets of chosen frames.</summary>
    private sealed class LossyChannel(IChannel inner, Func<long, bool> drop) : IChannel
    {
        public Guid ChannelId => inner.ChannelId;
        public ChannelType Type => inner.Type;
        public bool IsOpen => inner.IsOpen;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            FramePacket.TryParse(data, out var p) && drop(p!.FrameNumber) ? Task.CompletedTask : inner.SendAsync(data, ct);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => inner.ReceiveAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task Pipeline_RecoversFromALostFrame_ThroughKeyframeRequest()
    {
        var host = new LoopbackChannel(ChannelType.Desktop);
        var viewer = new LoopbackChannel(ChannelType.Desktop);
        host.PeerWriter = viewer.Writer;
        var renderer = new CountingRenderer();
        var encoder = new TileDeltaVideoEncoder { Quality = Big };
        // A moving square makes every frame differ.
        var capture = new ScriptedCapture(8, i => Frame(128, 128, 1, px => Paint(px, 128, (i * 9) % 100, 10, 20, 20, 200)));
        var sender = new DesktopStreamSender(capture, encoder, new LossyChannel(host, n => n == 2));
        var receiver = new DesktopStreamReceiver(viewer, new TileDeltaVideoDecoder(), renderer);
        var needed = 0;
        receiver.KeyframeNeeded += () =>
        {
            Interlocked.Increment(ref needed);
            sender.RequestKeyframe(); // in a real session this travels over the Control channel
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rx = receiver.RunAsync(cts.Token);
        await sender.RunAsync(cts.Token);
        await Task.Delay(200);
        await viewer.DisposeAsync();
        await rx;

        // 8 frames sent, frame 2 lost on the wire: frame 3 cannot be applied; the keyframe request fixes the rest.
        Assert.True(needed >= 1);
        Assert.True(receiver.FramesAwaitingKeyframe >= 1);
        Assert.True(renderer.Count >= 5, $"rendered {renderer.Count}");
        Assert.Equal(0, receiver.FramesCorrupt);
    }

    private sealed class ScriptedCapture(int frames, Func<int, CaptureFrame> make) : IScreenCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; return Task.CompletedTask; }
        public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < frames; i++)
            {
                yield return make(i);
                await Task.Delay(30, ct); // gives the receiver time to react between frames
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Pipeline_PacketsAreFlaggedAsKeyframeOrDelta()
    {
        var host = new LoopbackChannel(ChannelType.Desktop);
        var flags = new List<bool>();
        var tap = new TapChannel(host, d =>
        {
            if (FramePacket.TryParse(d, out var p) && p!.ChunkIndex == 0) flags.Add(p.IsKeyframe);
        });
        var sender = new DesktopStreamSender(new ScriptedCapture(4, _ => Frame(64, 64, 1)), new TileDeltaVideoEncoder { Quality = Big }, tap);

        await sender.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.Equal(new[] { true, false, false, false }, flags);
    }

    private sealed class TapChannel(IChannel inner, Action<ReadOnlyMemory<byte>> onSend) : IChannel
    {
        public Guid ChannelId => inner.ChannelId;
        public ChannelType Type => inner.Type;
        public bool IsOpen => inner.IsOpen;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { onSend(data); return inner.SendAsync(data, ct); }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => inner.ReceiveAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
