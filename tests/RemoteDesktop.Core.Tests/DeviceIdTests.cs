using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class DeviceIdTests
{
    [Fact]
    public void New_GeneratesUniqueIds()
    {
        var a = DeviceId.New();
        var b = DeviceId.New();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Parse_RoundTrips()
    {
        var original = DeviceId.New();
        var parsed = DeviceId.Parse(original.ToString());
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void TryParse_ValidGuid_ReturnsTrue()
    {
        var id = DeviceId.New();
        Assert.True(DeviceId.TryParse(id.ToString(), out var result));
        Assert.Equal(id, result);
    }

    [Fact]
    public void TryParse_InvalidString_ReturnsFalse()
    {
        Assert.False(DeviceId.TryParse("not-a-guid", out _));
    }

    [Fact]
    public void ToString_ProducesDGuidFormat()
    {
        var id = DeviceId.New();
        // D format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (36 chars)
        Assert.Equal(36, id.ToString().Length);
        Assert.Equal(4, id.ToString().Count(c => c == '-'));
    }
}
