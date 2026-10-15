using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Transport.Tests;

public sealed class TransportTests
{
    private static SessionId NewSession() => SessionId.New();

    // ── LoopbackTransport ─────────────────────────────────────────────────

    [Fact]
    public void LoopbackTransport_IsConnected_OnCreation()
    {
        var transport = new LoopbackTransport(NewSession());
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public void LoopbackTransport_DefaultMode_IsDirect()
    {
        var transport = new LoopbackTransport(NewSession());
        Assert.Equal(ConnectionMode.Direct, transport.Mode);
    }

    [Fact]
    public void LoopbackTransport_RelayMode_IsRelay()
    {
        var transport = new LoopbackTransport(NewSession(), ConnectionMode.Relay);
        Assert.Equal(ConnectionMode.Relay, transport.Mode);
    }

    [Fact]
    public async Task LoopbackTransport_OpenChannel_ReturnsOpenChannel()
    {
        var transport = new LoopbackTransport(NewSession());

        var channel = await transport.OpenChannelAsync(ChannelType.Control);

        Assert.NotNull(channel);
        Assert.True(channel.IsOpen);
        Assert.Equal(ChannelType.Control, channel.Type);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task LoopbackTransport_CloseAsync_IsConnectedFalse()
    {
        var transport = new LoopbackTransport(NewSession());

        await transport.CloseAsync();

        Assert.False(transport.IsConnected);
    }

    // ── LoopbackChannel echo mode ─────────────────────────────────────────

    [Fact]
    public async Task LoopbackChannel_SendReceive_EchoMode()
    {
        var transport = new LoopbackTransport(NewSession());
        var ch = (LoopbackChannel)await transport.OpenChannelAsync(ChannelType.Control);

        var sent = new byte[] { 1, 2, 3, 4, 5 };
        await ch.SendAsync(sent);

        var received = await ch.ReceiveAsync().FirstAsync();
        Assert.Equal(sent, received.ToArray());

        await ch.DisposeAsync();
    }

    // ── LoopbackChannel paired mode ───────────────────────────────────────

    [Fact]
    public async Task LoopbackChannel_PairedMode_SendGoesToPeer()
    {
        var tA = new LoopbackTransport(NewSession());
        var tB = new LoopbackTransport(NewSession());

        var chA = (LoopbackChannel)await tA.OpenChannelAsync(ChannelType.Input);
        var chB = (LoopbackChannel)await tB.OpenChannelAsync(ChannelType.Input);

        // Wire A→B and B→A
        chA.PeerWriter = chB.Writer;
        chB.PeerWriter = chA.Writer;

        var payload = new byte[] { 10, 20, 30 };
        await chA.SendAsync(payload);

        var fromB = await chB.ReceiveAsync().FirstAsync();
        Assert.Equal(payload, fromB.ToArray());

        await chA.DisposeAsync();
        await chB.DisposeAsync();
    }

    [Fact]
    public async Task LoopbackTransport_OpenAllChannelTypes_Succeeds()
    {
        var transport = new LoopbackTransport(NewSession());
        var types = Enum.GetValues<ChannelType>();

        foreach (var type in types)
        {
            var ch = await transport.OpenChannelAsync(type);
            Assert.Equal(type, ch.Type);
            await ch.DisposeAsync();
        }

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task LoopbackChannel_AfterTransportClose_IsNotOpen()
    {
        var transport = new LoopbackTransport(NewSession());
        var ch = (LoopbackChannel)await transport.OpenChannelAsync(ChannelType.Desktop);

        await transport.CloseAsync();

        Assert.False(ch.IsOpen);
    }
}
