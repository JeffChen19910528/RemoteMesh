using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class SecurityPlaceholderTests
{
    [Fact]
    public void IKeyStore_InterfaceExists()
    {
        var type = typeof(IKeyStore);
        Assert.NotNull(type);
    }

    [Fact]
    public void ISessionCrypto_InterfaceExists()
    {
        var type = typeof(ISessionCrypto);
        Assert.NotNull(type);
    }

    [Fact]
    public void DeviceKeyPair_TypeExists()
    {
        var type = typeof(DeviceKeyPair);
        Assert.NotNull(type);
    }
}
