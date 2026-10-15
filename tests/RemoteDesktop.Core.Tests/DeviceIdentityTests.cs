using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void ToDeviceInfo_MapsAllFields()
    {
        var id = DeviceId.New();
        var identity = new DeviceIdentity
        {
            DeviceId = id,
            DeviceName = "MyPC",
            Platform = "Windows",
            PublicKey = new byte[] { 0x01, 0x02 },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var info = identity.ToDeviceInfo();

        Assert.Equal(id, info.DeviceId);
        Assert.Equal("MyPC", info.DeviceName);
        Assert.Equal("Windows", info.Platform);
    }

    [Fact]
    public void DetectPlatform_ReturnsNonEmpty()
    {
        var platform = DeviceIdentity.DetectPlatform();
        Assert.False(string.IsNullOrWhiteSpace(platform));
    }

    [Fact]
    public void DetectPlatform_ReturnsKnownValue()
    {
        var platform = DeviceIdentity.DetectPlatform();
        var knownPlatforms = new[] { "Windows", "Linux", "macOS", "Unknown" };
        Assert.Contains(platform, (IEnumerable<string>)knownPlatforms);
    }
}
