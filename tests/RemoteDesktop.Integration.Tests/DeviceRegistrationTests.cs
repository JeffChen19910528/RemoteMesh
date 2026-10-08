using RemoteDesktop.Core;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Identity;

namespace RemoteDesktop.Integration.Tests;

public sealed class DeviceRegistrationTests
{
    [Fact]
    public async Task RegisterDevice_ThenFind_ReturnsDevice()
    {
        var registry = new InMemoryDeviceRegistry();
        var kp = DeviceKeyGenerator.Generate();
        var deviceId = DeviceId.New();
        var device = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = "TestPC",
            Platform = "Windows",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await registry.RegisterAsync(device, kp.PublicKey);

        var found = await registry.FindAsync(deviceId);
        Assert.NotNull(found);
        Assert.Equal("TestPC", found!.DeviceName);
    }

    [Fact]
    public async Task RegisterDevice_ThenGetPublicKey_ReturnsCorrectKey()
    {
        var registry = new InMemoryDeviceRegistry();
        var kp = DeviceKeyGenerator.Generate();
        var deviceId = DeviceId.New();
        var device = new DeviceInfo { DeviceId = deviceId, DeviceName = "KeyTest", Platform = "Linux", CreatedAt = DateTimeOffset.UtcNow };

        await registry.RegisterAsync(device, kp.PublicKey);

        var storedKey = await registry.GetPublicKeyAsync(deviceId);
        Assert.Equal(kp.PublicKey, storedKey);
    }

    [Fact]
    public async Task FindUnknownDevice_ReturnsNull()
    {
        var registry = new InMemoryDeviceRegistry();
        var result = await registry.FindAsync(DeviceId.New());
        Assert.Null(result);
    }

    [Fact]
    public async Task UnregisterDevice_RemovesFromRegistry()
    {
        var registry = new InMemoryDeviceRegistry();
        var kp = DeviceKeyGenerator.Generate();
        var deviceId = DeviceId.New();
        var device = new DeviceInfo { DeviceId = deviceId, DeviceName = "ToRemove", Platform = "Windows", CreatedAt = DateTimeOffset.UtcNow };

        await registry.RegisterAsync(device, kp.PublicKey);
        await registry.UnregisterAsync(deviceId);

        Assert.Null(await registry.FindAsync(deviceId));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task RegisterMultipleDevices_AreIndependent()
    {
        var registry = new InMemoryDeviceRegistry();
        for (int i = 0; i < 3; i++)
        {
            var kp = DeviceKeyGenerator.Generate();
            var id = DeviceId.New();
            await registry.RegisterAsync(
                new DeviceInfo { DeviceId = id, DeviceName = $"PC{i}", Platform = "Windows", CreatedAt = DateTimeOffset.UtcNow },
                kp.PublicKey);
        }
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public async Task DeviceIdentityService_LoadOrCreate_IsIdempotent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RMTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var keyDir = Path.Combine(tempDir, "keys");
            var keyStore = new OsKeyStore(keyDir);
            var svc = new DeviceIdentityService(keyStore, tempDir);

            var id1 = await svc.LoadOrCreateAsync("TestDevice");
            var id2 = await svc.LoadOrCreateAsync("TestDevice");

            Assert.Equal(id1.DeviceId, id2.DeviceId);
            Assert.Equal(id1.PublicKey, id2.PublicKey);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
