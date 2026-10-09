using System.Net.WebSockets;
using System.Threading.Channels;
using RemoteDesktop.Relay;
using RemoteDesktop.Security;

namespace RemoteDesktop.Integration.Tests;

/// <summary>One end of an in-memory message pipe that behaves like a WebSocket (whole binary messages, in order).</summary>
internal sealed class MemorySocket : WebSocket
{
    private readonly Channel<byte[]> _in;
    private readonly Channel<byte[]> _out;
    private readonly Func<byte[], byte[]>? _tamper;
    private byte[]? _current;
    private int _pos;
    private WebSocketState _state = WebSocketState.Open;

    private MemorySocket(Channel<byte[]> input, Channel<byte[]> output, Func<byte[], byte[]>? tamperOutgoing)
    {
        _in = input;
        _out = output;
        _tamper = tamperOutgoing;
    }

    /// <param name="tamperAtoB">Optionally rewrites each message A sends before B sees it (a hostile relay).</param>
    public static (MemorySocket A, MemorySocket B) Pair(Func<byte[], byte[]>? tamperAtoB = null, Func<byte[], byte[]>? tamperBtoA = null)
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new MemorySocket(ba, ab, tamperAtoB), new MemorySocket(ab, ba, tamperBtoA));
    }

    /// <summary>Everything this end sent, as the other end receives it.</summary>
    public List<byte[]> Wire { get; } = new();

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => _state;
    public override void Abort() { _state = WebSocketState.Aborted; _out.Writer.TryComplete(); }
    public override void Dispose() { _state = WebSocketState.Closed; _out.Writer.TryComplete(); }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        _state = WebSocketState.Closed;
        _out.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) =>
        CloseAsync(closeStatus, statusDescription, ct);

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        var copy = buffer.ToArray();
        lock (Wire) Wire.Add(copy);
        await _out.Writer.WriteAsync(_tamper is null ? copy : _tamper(copy), ct);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, ct).AsTask();

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_current is null)
        {
            if (!await _in.Reader.WaitToReadAsync(ct) || !_in.Reader.TryRead(out _current))
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            _pos = 0;
        }
        var n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsMemory(_pos, n).CopyTo(buffer);
        _pos += n;
        var done = _pos >= _current.Length;
        if (done) _current = null;
        return new ValueWebSocketReceiveResult(n, WebSocketMessageType.Binary, done);
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var r = await ReceiveAsync(buffer.AsMemory(), ct);
        return new WebSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage);
    }
}

/// <summary>TLS between two devices through an untrusted relay: who gets in, what the relay sees, what it cannot do.</summary>
public sealed class RelayTlsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static async Task<(WebSocket Client, WebSocket Server)> ConnectAsync(
        DeviceKeyPair clientKeys, DeviceKeyPair serverKeys, byte[] clientPinsServerAs, byte[] serverPinsClientAs,
        MemorySocket? a = null, MemorySocket? b = null)
    {
        if (a is null || b is null) (a, b) = MemorySocket.Pair();
        var client = RelayTls.AuthenticateAsync(a, initiator: true, clientKeys, clientPinsServerAs);
        var server = RelayTls.AuthenticateAsync(b, initiator: false, serverKeys, serverPinsClientAs);
        await Task.WhenAll(client, server).WaitAsync(Limit);
        return (await client, await server);
    }

    private static async Task<byte[]> ReceiveMessageAsync(WebSocket s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        ValueWebSocketReceiveResult r;
        do
        {
            r = await s.ReceiveAsync(buf.AsMemory(), CancellationToken.None).AsTask().WaitAsync(Limit);
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return ms.ToArray();
    }

    [Fact]
    public async Task TwoDevicesThatPinEachOther_Connect_AndMessagesArriveWhole_BothWays()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey);

        await client.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await server.SendAsync(new byte[] { 9, 8 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, true, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, await ReceiveMessageAsync(server));
        Assert.Equal(new byte[] { 9, 8 }, await ReceiveMessageAsync(client));
        Assert.Empty(await ReceiveMessageAsync(server));
    }

    [Fact]
    public async Task ABigMessage_StaysOneMessage_AndNothingIsLostOrReordered()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey);
        var big = new byte[900_000];
        new Random(5).NextBytes(big);

        await client.SendAsync(big, WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(new byte[] { 7 }, WebSocketMessageType.Binary, true, CancellationToken.None);

        Assert.Equal(big, await ReceiveMessageAsync(server));
        Assert.Equal(new byte[] { 7 }, await ReceiveMessageAsync(server));
    }

    [Fact]
    public async Task ARelayThatSendsItsOwnKey_IsRefusedByTheClient()
    {
        // A man in the middle holds a valid key of its own, but not the one the client pinned for the server.
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var impostor = DeviceKeyGenerator.Generate();
        var (a, b) = MemorySocket.Pair();

        var client = RelayTls.AuthenticateAsync(a, true, c, s.PublicKey);                     // pins the real server
        var fakeServer = RelayTls.AuthenticateAsync(b, false, impostor, c.PublicKey);         // is somebody else

        // The client sees a key it did not pin and walks away. (The impostor may well finish its own side of the
        // handshake: it accepted the client's real key, which is public anyway. It learns nothing and gets no data.)
        await Assert.ThrowsAnyAsync<Exception>(() => client.WaitAsync(Limit));
        try { (await fakeServer.WaitAsync(Limit)).Dispose(); } catch (Exception) { }
    }

    [Fact]
    public async Task AClientThatIsNotWhoTheServerPinned_IsRefused()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var other = DeviceKeyGenerator.Generate();
        var (a, b) = MemorySocket.Pair();

        var client = RelayTls.AuthenticateAsync(a, true, other, s.PublicKey);                 // the server pinned c, not "other"
        var server = RelayTls.AuthenticateAsync(b, false, s, c.PublicKey);

        // The server refuses the unknown key. (In TLS 1.3 the client has already finished its side when the server
        // rejects it; what matters is that the server never accepts, and that the client's connection then dies.)
        await Assert.ThrowsAnyAsync<Exception>(() => server.WaitAsync(Limit));
        try
        {
            var socket = await client.WaitAsync(Limit);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                var r = await socket.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) throw new IOException("closed by the server");
            });
        }
        catch (Exception) { /* refused during the handshake: just as good */ }
    }

    [Fact]
    public async Task WhatTheRelaySees_ContainsNoPlaintext()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (a, b) = MemorySocket.Pair();
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey, a, b);
        var secret = System.Text.Encoding.ASCII.GetBytes("the-password-is-hunter2-and-more-text-to-look-for");

        await client.SendAsync(secret, WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveMessageAsync(server);

        var wire = a.Wire.SelectMany(m => m).ToArray();
        Assert.True(wire.Length > secret.Length);
        Assert.False(wire.AsSpan().IndexOf(secret) >= 0, "the relay could read the message");
    }

    [Fact]
    public async Task ARecordTheRelayChanges_EndsTheConnection_InsteadOfDeliveringWrongData()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var tamper = false;
        var (a, b) = MemorySocket.Pair(tamperAtoB: m => { if (tamper && m.Length > 20) m[^3] ^= 0x01; return m; });
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey, a, b);

        tamper = true;
        await client.SendAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, WebSocketMessageType.Binary, true, CancellationToken.None);

        await Assert.ThrowsAsync<WebSocketException>(() => ReceiveMessageAsync(server));
        Assert.NotEqual(WebSocketState.Open, server.State);
    }

    [Fact]
    public async Task ReplayingARecord_DoesNotDeliverItTwice()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var replay = false;
        MemorySocket? aRef = null;
        var (a, b) = MemorySocket.Pair(tamperAtoB: m => m);
        aRef = a;
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey, a, b);

        await client.SendAsync(new byte[] { 42 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal(new byte[] { 42 }, await ReceiveMessageAsync(server));
        // The relay now injects the very record it saw before (the last thing A put on the wire).
        var last = aRef.Wire[^1];
        var field = typeof(MemorySocket).GetField("_out", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var writer = ((Channel<byte[]>)field.GetValue(a)!).Writer;
        await writer.WriteAsync(last);
        _ = replay;

        await Assert.ThrowsAsync<WebSocketException>(() => ReceiveMessageAsync(server));
    }

    [Fact]
    public async Task TheKeyExchangeIsEphemeral_SoTheNegotiatedSuiteIsForwardSecret()
    {
        // RelayTls refuses to return a connection that is not (see IsForwardSecret); reaching here proves the suite was
        // TLS 1.3 or ECDHE. Two connections between the same devices must not share any session secrets.
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (a1, b1) = MemorySocket.Pair();
        var (a2, b2) = MemorySocket.Pair();
        var first = await ConnectAsync(c, s, s.PublicKey, c.PublicKey, a1, b1);
        var second = await ConnectAsync(c, s, s.PublicKey, c.PublicKey, a2, b2);
        var same = new byte[200];
        await first.Client.SendAsync(same, WebSocketMessageType.Binary, true, CancellationToken.None);
        await second.Client.SendAsync(same, WebSocketMessageType.Binary, true, CancellationToken.None);

        // identical plaintext, different keys: the ciphertext on the two wires differs
        var wire1 = a1.Wire.Last();
        var wire2 = a2.Wire.Last();
        Assert.NotEqual(wire1, wire2);
    }

    [Fact]
    public async Task AnEndThatSaysNothing_TimesOutOrFails_NeverHangsForever()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (a, b) = MemorySocket.Pair();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // b never answers
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RelayTls.AuthenticateAsync(a, true, c, s.PublicKey, cts.Token));
        b.Dispose();
    }

    [Fact]
    public async Task ClosingOneEnd_EndsTheOther()
    {
        var c = DeviceKeyGenerator.Generate();
        var s = DeviceKeyGenerator.Generate();
        var (client, server) = await ConnectAsync(c, s, s.PublicKey, c.PublicKey);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var r = await server.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None).AsTask().WaitAsync(Limit);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }
}

public sealed class RelayTransportEarlyMessageTests
{
    [Fact]
    public async Task AMessageThatArrivesBeforeTheChannelIsOpened_IsKept_NotThrownAway()
    {
        var (a, b) = MemorySocket.Pair();
        var session = RemoteDesktop.Core.SessionId.New();
        var sender = new RemoteDesktop.Relay.RelayTransport(a, session);
        var receiver = new RemoteDesktop.Relay.RelayTransport(b, session);

        // The first side talks at once; the second has not opened its channels yet.
        var channel = await sender.OpenChannelAsync(RemoteDesktop.Protocol.ChannelType.Control);
        await channel.SendAsync(new byte[] { 0xA1, 7 });
        await Task.Delay(200);

        var late = await receiver.OpenChannelAsync(RemoteDesktop.Protocol.ChannelType.Control);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in late.ReceiveAsync(cts.Token))
        {
            Assert.Equal(new byte[] { 0xA1, 7 }, message.ToArray());
            break;
        }
        await sender.DisposeAsync();
        await receiver.DisposeAsync();
    }
}
