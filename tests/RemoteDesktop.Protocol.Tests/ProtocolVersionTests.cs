using RemoteDesktop.Protocol;

namespace RemoteDesktop.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void Current_ContainsMajorAndMinor()
    {
        Assert.Contains(ProtocolVersion.Major.ToString(), ProtocolVersion.Current);
        Assert.Contains(ProtocolVersion.Minor.ToString(), ProtocolVersion.Current);
    }

    [Fact]
    public void ChannelTypeEnum_HasExpectedMembers()
    {
        var values = Enum.GetValues<ChannelType>();
        Assert.Contains(ChannelType.Control, values);
        Assert.Contains(ChannelType.Desktop, values);
        Assert.Contains(ChannelType.Input, values);
        Assert.Contains(ChannelType.Clipboard, values);
        Assert.Contains(ChannelType.File, values);
    }
}
