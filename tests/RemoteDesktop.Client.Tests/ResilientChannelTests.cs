using System.Threading.Channels;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.Tests;

/// <summary>The channel that outlives the connection under it: what the screen, input and file loops rely on.</summary>
public sealed class ResilientChannelTests
{
    private sealed class FakeChannel(ChannelType type = ChannelType.Control) : IChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbox = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public List<byte[]> Sent { get; } = new();
        public bool Open { get; private set; } = true;
        public Exception? SendFails { get; set; }
        public bool Disposed { get; private set; }

        public Guid ChannelId { get; } = Guid.NewGuid();
        public ChannelType Type => type;
        public bool IsOpen => Open;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (SendFails is not null) throw SendFails;
            lock (Sent) Sent.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => _inbox.Reader.ReadAllAsync(ct);

        public void Deliver(params byte[] data) => _inbox.Writer.TryWrite(data);

        /// <summary>The connection dies: nothing more is sent or received.</summary>
        public void Kill() { Open = false; _inbox.Writer.TryComplete(); }

        public ValueTask DisposeAsync() { Disposed = true; Kill(); return ValueTask.CompletedTask; }
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Send_WaitsForAConnection_AndGoesOutOnTheFirstOneAttached()
    {
        var channel = new ResilientChannel(ChannelType.Desktop);
        var pending = channel.SendAsync(new byte[] { 1 });
        await Task.Delay(100);
        Assert.False(pending.IsCompleted); // nothing to send on yet

        var inner = new FakeChannel();
        await channel.AttachAsync(inner);
        await pending.WaitAsync(Wait);

        Assert.Equal(new byte[] { 1 }, Assert.Single(inner.Sent));
    }

    [Fact]
    public async Task Input_IsDroppedWhileDown_NotQueuedForLater()
    {
        // A mouse move from ten seconds ago is worse than none, and the caller (the UI) must never be left waiting.
        var channel = new ResilientChannel(ChannelType.Input, dropWhileDown: true);

        await channel.SendAsync(new byte[] { 1 }).WaitAsync(Wait);

        var inner = new FakeChannel(ChannelType.Input);
        await channel.AttachAsync(inner);
        await Task.Delay(100);
        Assert.Empty(inner.Sent);
        await channel.SendAsync(new byte[] { 2 }).WaitAsync(Wait);
        Assert.Equal(new byte[] { 2 }, Assert.Single(inner.Sent));
    }

    [Fact]
    public async Task Receive_CarriesOnAcrossTheReplacementOfTheConnection()
    {
        var channel = new ResilientChannel(ChannelType.Control);
        var first = new FakeChannel();
        await channel.AttachAsync(first);

        var received = new List<byte>();
        var reader = Task.Run(async () =>
        {
            await foreach (var m in channel.ReceiveAsync()) lock (received) received.Add(m.Span[0]);
        });

        first.Deliver(1);
        await Eventually(() => received.Count == 1);

        first.Kill(); // the connection dies; the reader must not end, nor throw
        await Task.Delay(100);
        Assert.False(reader.IsCompleted);

        var second = new FakeChannel();
        await channel.AttachAsync(second);
        second.Deliver(2);
        await Eventually(() => received.Count == 2);
        Assert.Equal(new byte[] { 1, 2 }, received);
        Assert.True(first.Disposed); // the replaced connection is let go

        await channel.DisposeAsync();
        await reader.WaitAsync(Wait); // only closing the channel ends the stream
    }

    [Fact]
    public async Task ASend_ThatFailedBecauseTheConnectionDied_GoesOutOnTheNextOne()
    {
        var channel = new ResilientChannel(ChannelType.Control);
        var dead = new FakeChannel();
        await channel.AttachAsync(dead);
        dead.SendFails = new IOException("the socket is gone");
        dead.Kill();

        var pending = channel.SendAsync(new byte[] { 7 });
        await Task.Delay(100);
        Assert.False(pending.IsCompleted); // waiting for the replacement rather than failing the caller

        var alive = new FakeChannel();
        await channel.AttachAsync(alive);
        await pending.WaitAsync(Wait);

        Assert.Equal(new byte[] { 7 }, Assert.Single(alive.Sent));
        Assert.Empty(dead.Sent);
    }

    [Fact]
    public async Task ASend_ThatFailsWhileTheConnectionIsFine_IsNotHiddenAsAnOutage()
    {
        var channel = new ResilientChannel(ChannelType.Control);
        var inner = new FakeChannel { SendFails = new ArgumentException("too big") };
        await channel.AttachAsync(inner);

        await Assert.ThrowsAsync<ArgumentException>(() => channel.SendAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Closing_ReleasesWaitingSenders_AndRefusesNewOnes()
    {
        var channel = new ResilientChannel(ChannelType.File);
        var pending = channel.SendAsync(new byte[] { 1 });
        await Task.Delay(50);

        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(Wait));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new byte[] { 2 }));
        Assert.False(channel.IsOpen);
    }

    [Fact]
    public async Task ACancelledSend_StopsWaiting()
    {
        var channel = new ResilientChannel(ChannelType.Control);
        using var cts = new CancellationTokenSource();
        var pending = channel.SendAsync(new byte[] { 1 }, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(condition());
    }
}
