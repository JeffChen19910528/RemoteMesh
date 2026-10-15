using RemoteDesktop.Signaling;

namespace RemoteDesktop.Signaling.Tests;

public sealed class SignalingClientTests
{
    [Fact]
    public async Task SignalingClient_IsNotConnected_BeforeConnect()
    {
        await using var client = new SignalingClient();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ISignalingClient_InterfaceIsSatisfied()
    {
        await using ISignalingClient client = new SignalingClient();
        Assert.NotNull(client);
    }
}
