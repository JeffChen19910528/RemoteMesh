using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class SessionIdTests
{
    [Fact]
    public void New_GeneratesUniqueIds()
    {
        var a = SessionId.New();
        var b = SessionId.New();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Parse_RoundTrips()
    {
        var id = SessionId.New();
        var parsed = SessionId.Parse(id.ToString());
        Assert.Equal(id, parsed);
    }
}
