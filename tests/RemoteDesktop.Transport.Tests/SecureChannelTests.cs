using System.Buffers.Binary;
using System.Text;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Transport.Tests;

public sealed class SecureChannelTests : IAsyncLifetime
{
    private readonly SessionId _session = SessionId.New();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    private AesGcmSessionCrypto _aliceCrypto = null!;
    private AesGcmSessionCrypto _bobCrypto = null!;

    public async Task InitializeAsync()
    {
        var a = DeviceKeyGenerator.Generate();
        var b = DeviceKeyGenerator.Generate();
        _aliceCrypto = new AesGcmSessionCrypto();
        _bobCrypto = new AesGcmSessionCrypto();
        await _aliceCrypto.DeriveSessionKeyAsync(_session, a.PrivateKey, b.PublicKey);
        await _bobCrypto.DeriveSessionKeyAsync(_session, b.PrivateKey, a.PublicKey);
    }

    public Task DisposeAsync()
    {
        _aliceCrypto.Dispose();
        _bobCrypto.Dispose();
        _cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Raw wire pair plus the secure endpoints on top. Alice initiated the session.</summary>
    private sealed class Link
    {
        public required LoopbackChannel AliceWire { get; init; }
        public required LoopbackChannel BobWire { get; init; }
        public required SecureChannel Alice { get; init; }
        public required SecureChannel Bob { get; init; }
    }

    private Link MakeLink(ChannelType type = ChannelType.Input, ISessionCrypto? bobCrypto = null)
    {
        var aw = new LoopbackChannel(type);
        var bw = new LoopbackChannel(type);
        aw.PeerWriter = bw.Writer;
        bw.PeerWriter = aw.Writer;
        return new Link
        {
            AliceWire = aw, BobWire = bw,
            Alice = new SecureChannel(aw, _aliceCrypto, _session, isInitiator: true),
            Bob = new SecureChannel(bw, bobCrypto ?? _bobCrypto, _session, isInitiator: false)
        };
    }

    /// <summary>Takes the next raw frame Alice put on the wire towards Bob (so Bob never sees it unless re-injected).</summary>
    private async Task<byte[]> CaptureAsync(LoopbackChannel wireTowardsBob)
    {
        await using var e = wireTowardsBob.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
        Assert.True(await e.MoveNextAsync());
        return e.Current.ToArray();
    }

    private async Task<List<byte[]>> ReadAsync(SecureChannel ch, int count, TimeSpan? idle = null)
    {
        var got = new List<byte[]>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(idle ?? TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var m in ch.ReceiveAsync(cts.Token))
            {
                got.Add(m.ToArray());
                if (got.Count == count) break;
            }
        }
        catch (OperationCanceledException) { }
        return got;
    }

    private static byte[] Msg(string s) => Encoding.UTF8.GetBytes(s);

    // ── Confidentiality & integrity ──────────────────────────────────────

    [Fact]
    public async Task RoundTrips_AndWireNeverContainsPlaintext()
    {
        var l = MakeLink();
        var secret = Msg("password=hunter2;clipboard=TOP-SECRET-DATA");

        await l.Alice.SendAsync(secret, _cts.Token);
        var onWire = await CaptureAsync(l.BobWire);

        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(onWire));
        Assert.False(onWire.AsSpan().IndexOf(secret.AsSpan(0, 12)) >= 0);

        await l.BobWire.Writer.WriteAsync(onWire, _cts.Token);
        var received = await ReadAsync(l.Bob, 1);
        Assert.Equal(secret, Assert.Single(received));
    }

    [Fact]
    public async Task BothDirections_Work_AndManyMessagesArriveInOrder()
    {
        var l = MakeLink();
        for (var i = 0; i < 50; i++) await l.Alice.SendAsync(Msg("a" + i), _cts.Token);
        for (var i = 0; i < 50; i++) await l.Bob.SendAsync(Msg("b" + i), _cts.Token);

        var atBob = await ReadAsync(l.Bob, 50);
        var atAlice = await ReadAsync(l.Alice, 50);

        Assert.Equal(Enumerable.Range(0, 50).Select(i => "a" + i), atBob.Select(Encoding.UTF8.GetString));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => "b" + i), atAlice.Select(Encoding.UTF8.GetString));
        Assert.Equal(0, l.Bob.DroppedFrames);
    }

    [Fact]
    public async Task EmptyPayload_RoundTrips()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(ReadOnlyMemory<byte>.Empty, _cts.Token);
        Assert.Empty(Assert.Single(await ReadAsync(l.Bob, 1)));
    }

    [Theory]
    [InlineData(8)]    // inside nonce
    [InlineData(25)]   // inside tag
    [InlineData(40)]   // inside ciphertext
    public async Task TamperedFrame_IsDropped_AndStreamContinues(int flipAt)
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("first-message-with-some-length"), _cts.Token);
        var frame = await CaptureAsync(l.BobWire);
        frame[Math.Min(flipAt, frame.Length - 1)] ^= 0x01;
        await l.BobWire.Writer.WriteAsync(frame, _cts.Token);

        await l.Alice.SendAsync(Msg("second"), _cts.Token);
        var got = await ReadAsync(l.Bob, 1);

        Assert.Equal("second", Encoding.UTF8.GetString(Assert.Single(got)));
        Assert.Equal(1, l.Bob.DroppedFrames);
    }

    [Fact]
    public async Task TamperedSequenceNumber_IsDropped()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("x"), _cts.Token);
        var frame = await CaptureAsync(l.BobWire);
        BinaryPrimitives.WriteInt64BigEndian(frame, 99); // attacker rewrites the counter
        await l.BobWire.Writer.WriteAsync(frame, _cts.Token);

        Assert.Empty(await ReadAsync(l.Bob, 1));
        Assert.Equal(1, l.Bob.DroppedFrames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(35)]
    public async Task TruncatedFrames_AreDropped(int length)
    {
        var l = MakeLink();
        await l.BobWire.Writer.WriteAsync(new byte[length], _cts.Token);
        Assert.Empty(await ReadAsync(l.Bob, 1));
        Assert.Equal(1, l.Bob.DroppedFrames);
    }

    [Fact]
    public async Task WrongKey_DropsEverything()
    {
        var other = new AesGcmSessionCrypto();
        await other.DeriveSessionKeyAsync(_session, DeviceKeyGenerator.Generate().PrivateKey,
            DeviceKeyGenerator.Generate().PublicKey);
        var l = MakeLink(bobCrypto: other);

        await l.Alice.SendAsync(Msg("hello"), _cts.Token);

        Assert.Empty(await ReadAsync(l.Bob, 1));
        Assert.Equal(1, l.Bob.DroppedFrames);
        other.Dispose();
    }

    // ── Replay / reorder ─────────────────────────────────────────────────

    [Fact]
    public async Task ReplayedFrame_IsDeliveredOnlyOnce()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("transfer $100"), _cts.Token);
        var frame = await CaptureAsync(l.BobWire);

        for (var i = 0; i < 3; i++) await l.BobWire.Writer.WriteAsync(frame, _cts.Token);
        var got = await ReadAsync(l.Bob, 3);

        Assert.Single(got);
        Assert.Equal(2, l.Bob.ReplayedFrames);
    }

    [Fact]
    public async Task ReplayAfterLaterTraffic_IsStillRejected()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("one"), _cts.Token);
        var first = await CaptureAsync(l.BobWire);
        await l.BobWire.Writer.WriteAsync(first, _cts.Token);
        for (var i = 0; i < 5; i++) await l.Alice.SendAsync(Msg("more" + i), _cts.Token);
        await ReadAsync(l.Bob, 6);

        await l.BobWire.Writer.WriteAsync(first, _cts.Token);

        Assert.Empty(await ReadAsync(l.Bob, 1));
        Assert.Equal(1, l.Bob.ReplayedFrames);
    }

    [Fact]
    public async Task OutOfOrderWithinWindow_IsAccepted_ThenReplayRejected()
    {
        var l = MakeLink();
        var frames = new List<byte[]>();
        await using var tap = l.BobWire.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
        for (var i = 0; i < 3; i++)
        {
            await l.Alice.SendAsync(Msg("m" + i), _cts.Token);
            Assert.True(await tap.MoveNextAsync());
            frames.Add(tap.Current.ToArray());
        }

        foreach (var idx in new[] { 2, 0, 1, 1, 0 }) // reordered, then replayed
            await l.BobWire.Writer.WriteAsync(frames[idx], _cts.Token);
        var got = await ReadAsync(l.Bob, 5);

        Assert.Equal(new[] { "m2", "m0", "m1" }, got.Select(Encoding.UTF8.GetString));
        Assert.Equal(2, l.Bob.ReplayedFrames);
    }

    [Fact]
    public async Task FrameOlderThanWindow_IsRejected()
    {
        var l = MakeLink();
        await using var tap = l.BobWire.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
        await l.Alice.SendAsync(Msg("ancient"), _cts.Token);
        Assert.True(await tap.MoveNextAsync());
        var ancient = tap.Current.ToArray();

        for (var i = 0; i < 100; i++)
        {
            await l.Alice.SendAsync(Msg("n" + i), _cts.Token);
            Assert.True(await tap.MoveNextAsync());
            await l.BobWire.Writer.WriteAsync(tap.Current.ToArray(), _cts.Token);
        }
        await ReadAsync(l.Bob, 100);

        await l.BobWire.Writer.WriteAsync(ancient, _cts.Token);
        Assert.Empty(await ReadAsync(l.Bob, 1));
    }

    [Fact]
    public async Task ForgedFrame_DoesNotBurnTheRealSequenceNumber()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("real"), _cts.Token);
        var real = await CaptureAsync(l.BobWire);

        var forged = (byte[])real.Clone();
        forged[^1] ^= 0xFF; // same sequence number, bad tag
        await l.BobWire.Writer.WriteAsync(forged, _cts.Token);
        await l.BobWire.Writer.WriteAsync(real, _cts.Token);

        var got = await ReadAsync(l.Bob, 2);
        Assert.Equal("real", Encoding.UTF8.GetString(Assert.Single(got)));
    }

    // ── Binding to channel and direction ─────────────────────────────────

    [Fact]
    public async Task FrameCannotBeMovedToAnotherChannelType()
    {
        var input = MakeLink(ChannelType.Input);
        var clipboard = MakeLink(ChannelType.Clipboard);
        await input.Alice.SendAsync(Msg("keystrokes"), _cts.Token);
        var frame = await CaptureAsync(input.BobWire);

        await clipboard.BobWire.Writer.WriteAsync(frame, _cts.Token);

        Assert.Empty(await ReadAsync(clipboard.Bob, 1));
        Assert.Equal(1, clipboard.Bob.DroppedFrames);
    }

    [Fact]
    public async Task ReflectedFrame_IsRejected()
    {
        var l = MakeLink();
        await l.Alice.SendAsync(Msg("my own message"), _cts.Token);
        var frame = await CaptureAsync(l.BobWire);

        await l.AliceWire.Writer.WriteAsync(frame, _cts.Token); // attacker bounces it back to the sender

        Assert.Empty(await ReadAsync(l.Alice, 1));
        Assert.Equal(1, l.Alice.DroppedFrames);
    }

    [Fact]
    public async Task ConcurrentSends_AllArriveExactlyOnce()
    {
        var l = MakeLink();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => l.Alice.SendAsync(Msg("c" + i), _cts.Token)));

        var got = await ReadAsync(l.Bob, 100);

        Assert.Equal(100, got.Select(Encoding.UTF8.GetString).Distinct().Count());
        Assert.Equal(0, l.Bob.DroppedFrames);
    }

    // ── SecureTransport wrapper ──────────────────────────────────────────

    [Fact]
    public async Task SecureTransport_OnlyHandsOutEncryptedChannels()
    {
        var inner = new LoopbackTransport(_session);
        await using var secure = new SecureTransport(inner, _aliceCrypto, isInitiator: true);

        var ch = await secure.OpenChannelAsync(ChannelType.File, _cts.Token);

        Assert.IsType<SecureChannel>(ch);
        Assert.Equal(ChannelType.File, ch.Type);
        Assert.Equal(_session, secure.SessionId);
    }

    // ── ReplayWindow unit ────────────────────────────────────────────────

    [Fact]
    public void ReplayWindow_BasicRules()
    {
        var w = new ReplayWindow();
        Assert.False(w.CanAccept(0));
        Assert.False(w.CanAccept(-5));
        Assert.True(w.CanAccept(1));
        Assert.True(w.Commit(1));
        Assert.False(w.CanAccept(1));
        Assert.False(w.Commit(1));
        Assert.True(w.Commit(70));          // jumps the window forward
        Assert.False(w.CanAccept(1));       // now outside the 64-wide window
        Assert.True(w.CanAccept(7));        // 70-63: oldest still inside
        Assert.False(w.CanAccept(6));
        Assert.True(w.Commit(8));
        Assert.False(w.Commit(8));
        Assert.True(w.Commit(1000));        // large jump clears history
        Assert.False(w.CanAccept(900));
    }
}
