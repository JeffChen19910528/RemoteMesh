using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Integration.Tests;

public sealed class IntegrationPlaceholderTests
{
    [Fact]
    public void CoreAndProtocol_AssembliesLoad()
    {
        Assert.NotNull(typeof(DeviceId).Assembly);
        Assert.NotNull(typeof(ChannelType).Assembly);
    }

    [Fact]
    public void SessionState_HasAllRequiredStates()
    {
        var states = Enum.GetNames<SessionState>();
        Assert.Contains("Connected", states);
        Assert.Contains("Disconnected", states);
        Assert.Contains("Authenticating", states);
        Assert.Contains("Authorizing", states);
        Assert.Contains("Failed", states);
        Assert.Contains("Expired", states);
    }
}
