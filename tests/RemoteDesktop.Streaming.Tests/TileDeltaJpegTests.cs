using System.Buffers.Binary;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;

namespace RemoteDesktop.Streaming.Tests;

/// <summary>
/// The tile/region logic of the lossy codec, run with a stand-in "JPEG" that keeps pixels exactly (a valid JPEG
/// header followed by raw pixels), so what is checked here is the planning, framing and validation — not image quality.
/// </summary>
public sealed class TileDeltaJpegTests
{
    /// <summary>[SOI][SOF0 with the real size][raw BGRA]: parses like a JPEG header, round-trips like a lossless codec.</summary>
    private sealed class FakeJpeg : IJpegCodec
    {
        public int Encodes, Decodes;
        public bool FailDecode;
        public List<(int W, int H)> EncodedSizes { get; } = new();

        public byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality)
        {
            Encodes++;
            EncodedSizes.Add((width, height));
            var data = new byte[15 + width * height * 4];
            data[0] = 0xFF; data[1] = 0xD8;
            data[2] = 0xFF; data[3] = 0xC0; data[4] = 0; data[5] = 11; data[6] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(7), (ushort)height);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(9), (ushort)width);
            data[11] = 1; data[12] = 1; data[13] = 0x11; data[14] = 0;
            for (var y = 0; y < height; y++)
                pixels.Slice(y * stride, width * 4).CopyTo(data.AsSpan(15 + y * width * 4));
            return data;
        }

        public void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride)
        {
            Decodes++;
            if (FailDecode) throw new InvalidDataException("boom");
            for (var y = 0; y < height; y++)
            {
                var from = 15 + y * width * 4;
                if (from + width * 4 > jpeg.Length) break; // lenient on purpose: only the decoder's own checks may stop a bad frame
                jpeg.Slice(from, width * 4).CopyTo(destination.Slice(y * destinationStride, width * 4));
            }
        }
    }

    private static VideoQuality Q(int fps = 30, int refreshSeconds = 1000) =>
        new() { Width = 0, Height = 0, Fps = fps, BitrateKbps = 4000, KeyframeIntervalSeconds = refreshSeconds };

    private static byte[] Pixels(int w, int h, int seed = 1)
    {
        var px = new byte[w * h * 4];
        new Random(seed).NextBytes(px);
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    private static CaptureFrame Frame(int w, int h, byte[] px) => new() { Width = w, Height = h, PixelData = px };

    private static void Paint(byte[] px, int w, int x0, int y0, int rw, int rh, byte v)
    {
        for (var y = y0; y < y0 + rh; y++)
            for (var x = x0; x < x0 + rw; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v;
            }
    }

    private static int RegionCount(ReadOnlyMemory<byte> frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.Span[9..]);

    private static (TileDeltaJpegEncoder Enc, TileDeltaJpegDecoder Dec, FakeJpeg Codec) Pair(int fps = 30, int refresh = 1000)
    {
        var codec = new FakeJpeg();
        // refreshSeconds is the encoder's own parameter; Quality.Fps converts it to frames.
        return (new TileDeltaJpegEncoder(codec, refresh) { Quality = Q(fps) }, new TileDeltaJpegDecoder(codec), codec);
    }

    // ── Round trips ──────────────────────────────────────────────────────

    [Fact]
    public async Task KeyframeThenSmallChange_RoundTrips_AndOnlyTheChangedAreaIsSent()
    {
        var (enc, dec, codec) = Pair();
        const int w = 300, h = 200;
        var px = Pixels(w, h);

        var key = await enc.EncodeAsync(Frame(w, h, px));
        Assert.True(enc.LastFrameWasKeyframe);
        Assert.Equal(1, RegionCount(key));
        using (var f = await dec.DecodeAsync(key)) Assert.Equal(px, f.PixelData.ToArray());

        codec.EncodedSizes.Clear();
        Paint(px, w, 70, 70, 10, 10, 200); // inside tile (1,1)
        var delta = await enc.EncodeAsync(Frame(w, h, px));
        Assert.False(enc.LastFrameWasKeyframe);
        Assert.Equal(1, RegionCount(delta));
        Assert.Equal((64, 64), Assert.Single(codec.EncodedSizes)); // one tile, not the screen
        using (var f = await dec.DecodeAsync(delta)) Assert.Equal(px, f.PixelData.ToArray());
    }

    [Fact]
    public async Task AnUnchangedScreen_SendsNothing_AndTheNextChangeStillDecodes()
    {
        var (enc, dec, codec) = Pair();
        const int w = 200, h = 200;
        var px = Pixels(w, h);
        using (await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)))) { }
        var encodesAfterKey = codec.Encodes;

        for (var i = 0; i < 5; i++)
        {
            var idle = await enc.EncodeAsync(Frame(w, h, px));
            Assert.True(idle.IsEmpty);          // nothing to send
            Assert.Equal(encodesAfterKey, codec.Encodes); // and no picture was compressed for it
        }

        // The idle frames used no sequence numbers: a real change right after them is not seen as a gap.
        Paint(px, w, 70, 70, 10, 10, 200);
        using var f = await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)));
        Assert.Equal(px, f.PixelData.ToArray());
    }

    [Fact]
    public async Task EdgeTiles_ArePartial_AndStillExact()
    {
        var (enc, dec, _) = Pair();
        const int w = 130, h = 70; // 3 x 2 tiles, the last column is 2 px wide, the last row 6 px high
        var px = Pixels(w, h);
        using (await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)))) { }

        Paint(px, w, 128, 64, 2, 6, 77); // the corner tile only
        using var f = await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)));
        Assert.Equal(px, f.PixelData.ToArray());
    }

    [Fact]
    public async Task ManyFrames_StayInSync()
    {
        var (enc, dec, _) = Pair();
        const int w = 256, h = 192;
        var px = Pixels(w, h);
        var rnd = new Random(3);
        for (var i = 0; i < 40; i++)
        {
            Paint(px, w, rnd.Next(0, w - 20), rnd.Next(0, h - 20), rnd.Next(1, 20), rnd.Next(1, 20), (byte)rnd.Next(256));
            using var f = await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, px)));
            Assert.Equal(px, f.PixelData.ToArray());
        }
    }

    // ── Keyframes ────────────────────────────────────────────────────────

    [Fact]
    public async Task ForceKeyframe_ResolutionChange_AndPeriodicRefresh_ProduceKeyframes()
    {
        var (enc, dec, _) = Pair(fps: 2, refresh: 1); // refresh every 1 s x 2 fps = every 2 frames
        var px = Pixels(128, 128);
        await enc.EncodeAsync(Frame(128, 128, px));
        Assert.True(enc.LastFrameWasKeyframe);

        await enc.EncodeAsync(Frame(128, 128, px));
        Assert.False(enc.LastFrameWasKeyframe);
        await enc.EncodeAsync(Frame(128, 128, px));
        Assert.True(enc.LastFrameWasKeyframe); // the periodic refresh

        await enc.EncodeAsync(Frame(128, 128, px));
        enc.ForceKeyframe();
        await enc.EncodeAsync(Frame(128, 128, px));
        Assert.True(enc.LastFrameWasKeyframe);

        await enc.EncodeAsync(Frame(64, 64, Pixels(64, 64)));
        Assert.True(enc.LastFrameWasKeyframe); // a new size cannot be a delta
    }

    [Fact]
    public async Task ADeltaWithoutAKeyframe_AndALostFrame_AskForOne()
    {
        var (enc, dec, _) = Pair();
        var px = Pixels(128, 128);
        var key = await enc.EncodeAsync(Frame(128, 128, px));
        Paint(px, 128, 0, 0, 5, 5, 9);
        var d1 = await enc.EncodeAsync(Frame(128, 128, px));
        Paint(px, 128, 70, 70, 5, 5, 9);
        var d2 = await enc.EncodeAsync(Frame(128, 128, px));

        var fresh = new TileDeltaJpegDecoder(new FakeJpeg());
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => fresh.DecodeAsync(d1)); // never saw a keyframe

        using (await dec.DecodeAsync(key)) { }
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(d2)); // d1 was lost
    }

    // ── Region planning ──────────────────────────────────────────────────

    private static bool[] Mask(int tilesX, int tilesY, Func<int, int, bool> on)
    {
        var m = new bool[tilesX * tilesY];
        for (var y = 0; y < tilesY; y++) for (var x = 0; x < tilesX; x++) m[y * tilesX + x] = on(x, y);
        return m;
    }

    [Fact]
    public void NothingChanged_PlansNothing() => Assert.Empty(RegionPlanner.Plan(Mask(10, 10, (_, _) => false), 10, 10));

    [Fact]
    public void ARunOfTiles_IsOneStrip()
    {
        var plan = RegionPlanner.Plan(Mask(20, 12, (x, y) => y == 3 && x is >= 4 and <= 8), 20, 12);
        Assert.Equal(new TileRect(4, 3, 5, 1), Assert.Single(plan));
    }

    [Fact]
    public void TheSameColumnsOnConsecutiveRows_MergeIntoOneRectangle()
    {
        var plan = RegionPlanner.Plan(Mask(20, 12, (x, y) => y is >= 2 and <= 5 && x is >= 6 and <= 9), 20, 12);
        Assert.Equal(new TileRect(6, 2, 4, 4), Assert.Single(plan));
    }

    [Fact]
    public void SeparateAreas_StaySeparate()
    {
        var plan = RegionPlanner.Plan(Mask(30, 20, (x, y) => (x == 1 && y == 1) || (x == 25 && y == 17)), 30, 20);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void ALargeChange_BecomesOneBoundingPicture()
    {
        // 50 % of the tiles in a checkerboard: hundreds of strips would be far worse than one picture.
        var plan = RegionPlanner.Plan(Mask(20, 20, (x, y) => (x + y) % 2 == 0), 20, 20);
        Assert.Equal(new TileRect(0, 0, 20, 20), Assert.Single(plan));
    }

    [Fact]
    public void TooManySmallAreas_FallBackToTheBoundingBox()
    {
        // ~12 % of 2400 tiles, but 300 separate strips: more than the cap.
        var plan = RegionPlanner.Plan(Mask(60, 40, (x, y) => x % 4 == 0 && y % 2 == 0), 60, 40);
        var box = Assert.Single(plan);
        Assert.True(plan.Count <= RegionPlanner.MaxRegions);
        Assert.Equal(0, box.X);
        Assert.Equal(0, box.Y);
    }

    [Fact]
    public void PlannedRegions_AlwaysCoverEveryChangedTile_AndStayInsideTheGrid()
    {
        var rnd = new Random(11);
        for (var round = 0; round < 300; round++)
        {
            int tx = rnd.Next(1, 40), ty = rnd.Next(1, 30);
            var density = rnd.NextDouble() * 0.5;
            var mask = Mask(tx, ty, (_, _) => rnd.NextDouble() < density);
            var plan = RegionPlanner.Plan(mask, tx, ty);

            var covered = new bool[tx * ty];
            foreach (var r in plan)
            {
                Assert.InRange(r.X, 0, tx - 1); Assert.InRange(r.Y, 0, ty - 1);
                Assert.True(r.Width >= 1 && r.Height >= 1 && r.X + r.Width <= tx && r.Y + r.Height <= ty);
                for (var y = r.Y; y < r.Y + r.Height; y++) for (var x = r.X; x < r.X + r.Width; x++) covered[y * tx + x] = true;
            }
            for (var i = 0; i < mask.Length; i++) if (mask[i]) Assert.True(covered[i], "a changed tile was left out");
        }
    }

    [Theory]
    [InlineData(90, 8000)]
    [InlineData(80, 4000)]
    [InlineData(70, 2000)]
    [InlineData(60, 1000)]
    [InlineData(50, 100)]
    public void PoorerConnections_GetLowerJpegQuality(int expected, int bitrate) =>
        Assert.Equal(expected, LossyQuality.For(new VideoQuality { BitrateKbps = bitrate }));

    // ── Hostile input ────────────────────────────────────────────────────

    private static byte[] Header(int w, int h, byte flags, uint seq, int regions)
    {
        var b = new byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)h);
        b[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(5), seq);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(9), (ushort)regions);
        return b;
    }

    private static byte[] Region(int tx, int ty, int tw, int th, byte[] picture, uint? declaredLength = null)
    {
        var b = new byte[12 + picture.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)tx);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)ty);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)tw);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)th);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), declaredLength ?? (uint)picture.Length);
        picture.CopyTo(b.AsSpan(12));
        return b;
    }

    private static byte[] Picture(int w, int h) => new FakeJpeg().Encode(new byte[w * h * 4], w, h, w * 4, 80);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static async Task<TileDeltaJpegDecoder> SyncedDecoder(int w = 128, int h = 128)
    {
        var codec = new FakeJpeg();
        var dec = new TileDeltaJpegDecoder(codec);
        var enc = new TileDeltaJpegEncoder(codec) { Quality = Q() };
        using (await dec.DecodeAsync(await enc.EncodeAsync(Frame(w, h, Pixels(w, h))))) { }
        return dec;
    }

    public static IEnumerable<object[]> Hostile()
    {
        var pic = Picture(64, 64);
        // keyframe-sized helpers for a 128x128 screen = 2x2 tiles
        yield return ["empty", Array.Empty<byte>()];
        yield return ["truncated header", new byte[5]];
        yield return ["zero width", Join(Header(0, 128, 1, 1, 1))];
        yield return ["huge width", Join(Header(60000, 128, 1, 1, 1))];
        yield return ["unknown flag bits", Join(Header(128, 128, 0x80, 1, 0))];
        yield return ["too many regions", Join(Header(128, 128, 0, 1, 5000))];
        yield return ["keyframe with two regions", Join(Header(128, 128, 1, 1, 2), Region(0, 0, 2, 2, Picture(128, 128)), Region(0, 0, 2, 2, Picture(128, 128)))];
        yield return ["keyframe not covering the screen", Join(Header(128, 128, 1, 1, 1), Region(0, 0, 1, 1, pic))];
        yield return ["region outside the grid", Join(Header(128, 128, 0, 1, 1), Region(1, 1, 2, 2, Picture(128, 128)))];
        yield return ["zero-size region", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 0, 1, pic))];
        yield return ["length longer than the data", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, pic, declaredLength: uint.MaxValue))];
        yield return ["zero length", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, [], declaredLength: 0))];
        yield return ["picture size differs from its region", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, Picture(32, 32)))];
        yield return ["picture that is not a JPEG", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, new byte[100]))];
        yield return ["two big overlapping regions (area beyond the screen)", Join(Header(128, 128, 0, 1, 2),
            Region(0, 0, 2, 2, Picture(128, 128)), Region(0, 0, 2, 2, Picture(128, 128)))];
        yield return ["regions overlapping beyond the screen area", Join(Header(128, 128, 0, 1, 5),
            Region(0, 0, 1, 1, pic), Region(0, 0, 1, 1, pic), Region(0, 0, 1, 1, pic), Region(0, 0, 1, 1, pic), Region(0, 0, 1, 1, pic))];
        yield return ["data after the last region", Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, pic), [1, 2, 3])];
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task HostileFrames_AreRejected_BeforeAnythingIsDecoded_AndNeverApplied(string why, byte[] frame)
    {
        var codec = new FakeJpeg();
        var dec = new TileDeltaJpegDecoder(codec);
        var enc = new TileDeltaJpegEncoder(codec) { Quality = Q() };
        using (await dec.DecodeAsync(await enc.EncodeAsync(Frame(128, 128, Pixels(128, 128))))) { }
        var decodesBefore = codec.Decodes;

        await Assert.ThrowsAnyAsync<Exception>(() => dec.DecodeAsync(frame));

        // The frame was refused on its own numbers: the image decoder never saw any of its data.
        Assert.True(codec.Decodes == decodesBefore, $"'{why}' reached the image decoder");

        // After any rejected frame the decoder will not build on a possibly damaged canvas.
        var good = Join(Header(128, 128, 0, 1, 1), Region(0, 0, 1, 1, Picture(64, 64)));
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(good));
    }

    [Fact]
    public async Task ADecoderFailure_ForcesAKeyframe()
    {
        var codec = new FakeJpeg();
        var dec = new TileDeltaJpegDecoder(codec);
        var enc = new TileDeltaJpegEncoder(codec) { Quality = Q() };
        var px = Pixels(128, 128);
        using (await dec.DecodeAsync(await enc.EncodeAsync(Frame(128, 128, px)))) { }
        Paint(px, 128, 0, 0, 4, 4, 5);
        var delta = await enc.EncodeAsync(Frame(128, 128, px));

        codec.FailDecode = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => dec.DecodeAsync(delta));
        codec.FailDecode = false;
        Paint(px, 128, 70, 70, 4, 4, 5);
        var next = await enc.EncodeAsync(Frame(128, 128, px));
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(next));
    }

    [Fact]
    public async Task ADeltaThatDoesNotMatchTheSequence_IsNotApplied()
    {
        var dec = await SyncedDecoder();
        var skipped = Join(Header(128, 128, 0, 99, 1), Region(0, 0, 1, 1, Picture(64, 64)));
        await Assert.ThrowsAsync<KeyframeRequiredException>(() => dec.DecodeAsync(skipped));
    }

    // ── JPEG header reader ───────────────────────────────────────────────

    [Fact]
    public void JpegHeader_ReadsTheDeclaredSize()
    {
        Assert.Equal((300, 200), JpegHeader.ReadSize(Picture(300, 200)));
        Assert.Equal((1, 1), JpegHeader.ReadSize(Picture(1, 1)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02 })]                                  // scan before any frame header
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0xFF, 0xFF, 0x08, 0, 1, 0, 1, 1 })]            // segment longer than the data
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 0, 0, 1, 1, 1, 0x11, 0 })] // zero height
    public void JpegHeader_RejectsNonsense(byte[] data) => Assert.Null(JpegHeader.ReadSize(data));

    [Fact]
    public void JpegHeader_NeverThrows_OnRandomBytes()
    {
        var rnd = new Random(5);
        for (var i = 0; i < 5000; i++)
        {
            var b = new byte[rnd.Next(0, 80)];
            rnd.NextBytes(b);
            if (b.Length > 1 && i % 2 == 0) { b[0] = 0xFF; b[1] = 0xD8; }
            _ = JpegHeader.ReadSize(b);
        }
    }
}
