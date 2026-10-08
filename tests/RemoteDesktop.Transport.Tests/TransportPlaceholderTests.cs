using RemoteDesktop.Transport;

namespace RemoteDesktop.Transport.Tests;

public sealed class TransportPlaceholderTests
{
    [Fact]
    public void ITransport_InterfaceExists()
    {
        Assert.NotNull(typeof(ITransport));
    }

    [Fact]
    public void IChannel_InterfaceExists()
    {
        Assert.NotNull(typeof(IChannel));
    }
}
