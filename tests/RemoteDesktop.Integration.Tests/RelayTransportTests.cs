using System.Net.WebSockets;
using System.Threading.Channels;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Relay;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Integration.Tests;

public sealed class RelayTransportTests
{
    /// <summary>Feeds scripted frames to RelayTransport and records what it sends/closes with.</summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        public sealed record Frame(byte[] Data, WebSocketMessageType Type, bool EndOfMessage);

        private readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>();
        private WebSocketState _state = WebSocketState.Open;

        public WebSocketCloseStatus? ClosedWith { get; private set; }
        public Exception? CloseThrows { get; set; }
        public void Enqueue(byte[] data, bool end = true, WebSocketMessageType type = WebSocketMessageType.Binary) =>
            _frames.Writer.TryWrite(new Frame(data, type, end));
        public void EndInput() => _frames.Writer.TryComplete();

        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            if (CloseThrows is not null) throw CloseThrows;
            ClosedWith = s;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) =>
            CloseAsync(s, d, ct);

        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) =>
            throw new NotSupportedException();

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!await _frames.Reader.WaitToReadAsync(ct))
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            var f = await _frames.Reader.ReadAsync(ct);
            f.Data.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(f.Data.Length, f.Type, f.EndOfMessage);
        }
    }

    private static async Task<byte[]> NextAsync(IChannel ch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = ch.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());
        return e.Current.ToArray();
    }

    [Fact]
    public async Task FragmentedMessage_IsReassembled()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var desktop = await t.OpenChannelAsync(ChannelType.Desktop);

        ws.Enqueue([(byte)ChannelType.Desktop, 1, 2, 3], end: false);
        ws.Enqueue([4, 5, 6], end: false);
        ws.Enqueue([7, 8], end: true);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await NextAsync(desktop));
    }

    [Fact]
    public async Task MessagesAreRoutedByChannelType_AndUnknownOrUnopenedAreIgnored()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var input = await t.OpenChannelAsync(ChannelType.Input);

        ws.Enqueue([(byte)ChannelType.Clipboard, 9, 9]);   // channel never opened: dropped
        ws.Enqueue([200, 9, 9]);                           // not a channel type: dropped
        ws.Enqueue([(byte)ChannelType.Input, 42], type: WebSocketMessageType.Text); // text frames are not protocol
        ws.Enqueue([(byte)ChannelType.Input, 1]);

        Assert.Equal(new byte[] { 1 }, await NextAsync(input));
    }

    [Fact]
    public async Task OnlyChannelByteMessage_DeliversEmptyPayload()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var ctl = await t.OpenChannelAsync(ChannelType.Control);

        ws.Enqueue([(byte)ChannelType.Control]);

        Assert.Empty(await NextAsync(ctl));
    }

    [Fact]
    public async Task OversizedMessage_ClosesConnection_WithMessageTooBig()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var desktop = await t.OpenChannelAsync(ChannelType.Desktop);

        var chunk = new byte[16 * 1024];
        chunk[0] = (byte)ChannelType.Desktop;
        for (var i = 0; i < RelayTransport.MaxMessageBytes / chunk.Length + 2; i++)
            ws.Enqueue(chunk, end: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ws.ClosedWith is null && !cts.IsCancellationRequested) await Task.Delay(10);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, ws.ClosedWith);
        Assert.False(desktop.IsOpen);
    }

    [Fact]
    public async Task SendingOversizedPayload_IsRefusedLocally()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var ch = await t.OpenChannelAsync(ChannelType.File);

        await Assert.ThrowsAsync<ArgumentException>(
            () => ch.SendAsync(new byte[RelayTransport.MaxMessageBytes]));
    }

    [Fact]
    public async Task OpeningSameChannelTwice_ReturnsSameChannel()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        Assert.Same(await t.OpenChannelAsync(ChannelType.Input), await t.OpenChannelAsync(ChannelType.Input));
    }

    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(WebSocketException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Disposing_WhenTheSocketIsAlreadyGone_DoesNotThrow(Type failure)
    {
        var ws = new ScriptedWebSocket { CloseThrows = (Exception)Activator.CreateInstance(failure, "gone")! };
        var t = new RelayTransport(ws, SessionId.New());

        await t.DisposeAsync(); // must be safe: teardown cannot depend on the peer being alive
    }

    [Fact]
    public async Task PeerClose_CompletesChannels()
    {
        var ws = new ScriptedWebSocket();
        await using var t = new RelayTransport(ws, SessionId.New());
        var ch = await t.OpenChannelAsync(ChannelType.Control);

        ws.EndInput(); // peer closed

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in ch.ReceiveAsync(cts.Token)) { }
    }
}
