using RemoteDesktop.Core;
using RemoteDesktop.Server.Identity;
using RemoteDesktop.Server.Infrastructure;

namespace RemoteDesktop.Integration.Tests;

public sealed class TrustedDeviceTests
{
    private static InMemoryTrustedDeviceStore NewStore() => new();

    [Fact]
    public async Task AddTrusted_IsTrustedReturnsTrue()
    {
        var store = NewStore();
        var owner = DeviceId.New();
        var trusted = DeviceId.New();

        await store.AddAsync(new TrustedDevice
        {
            OwnerDeviceId = owner,
            TrustedDeviceId = trusted,
            TrustedDeviceName = "Test"
        });

        Assert.True(await store.IsTrustedAsync(owner, trusted));
    }

    [Fact]
    public async Task RemoveTrusted_IsTrustedReturnsFalse()
    {
        var store = NewStore();
        var owner = DeviceId.New();
        var trusted = DeviceId.New();

        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = trusted });
        await store.RemoveAsync(owner, trusted);

        Assert.False(await store.IsTrustedAsync(owner, trusted));
    }

    [Fact]
    public async Task IsTrusted_UnknownDevice_ReturnsFalse()
    {
        var store = NewStore();
        Assert.False(await store.IsTrustedAsync(DeviceId.New(), DeviceId.New()));
    }

    [Fact]
    public async Task List_ReturnsOwnersDevices()
    {
        var store = NewStore();
        var owner = DeviceId.New();
        var other = DeviceId.New();
        var t1 = DeviceId.New();
        var t2 = DeviceId.New();

        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = t1 });
        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = t2 });
        await store.AddAsync(new TrustedDevice { OwnerDeviceId = other, TrustedDeviceId = DeviceId.New() });

        var list = await store.ListAsync(owner);

        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Equal(owner, e.OwnerDeviceId));
    }

    [Fact]
    public async Task GetTrusted_ReturnsCorrectEntry()
    {
        var store = NewStore();
        var owner = DeviceId.New();
        var trusted = DeviceId.New();

        await store.AddAsync(new TrustedDevice
        {
            OwnerDeviceId = owner,
            TrustedDeviceId = trusted,
            TrustedDeviceName = "Alice's Laptop",
            DefaultPermissions = Permission.ViewScreen | Permission.ControlMouse
        });

        var entry = await store.GetAsync(owner, trusted);

        Assert.NotNull(entry);
        Assert.Equal("Alice's Laptop", entry!.TrustedDeviceName);
        Assert.True(entry.DefaultPermissions.HasFlag(Permission.ViewScreen));
    }

    [Fact]
    public async Task AddTrusted_DefaultPermissionsIsViewOnly()
    {
        var store = NewStore();
        var owner = DeviceId.New();
        var trusted = DeviceId.New();

        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = trusted });

        var entry = await store.GetAsync(owner, trusted);
        Assert.Equal(Permission.ViewOnly, entry!.DefaultPermissions);
    }

    [Fact]
    public async Task RateLimiter_BlocksAfterMaxAttempts()
    {
        var limiter = new InMemoryRateLimiter();
        const string key = "test-key";

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.TryConsume(key, maxAttempts: 3, window: TimeSpan.FromMinutes(1)));

        Assert.False(limiter.TryConsume(key, maxAttempts: 3, window: TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RateLimiter_Reset_ClearsCount()
    {
        var limiter = new InMemoryRateLimiter();
        const string key = "test-key";

        for (var i = 0; i < 3; i++)
            limiter.TryConsume(key, maxAttempts: 3, window: TimeSpan.FromMinutes(1));

        limiter.Reset(key);

        Assert.True(limiter.TryConsume(key, maxAttempts: 3, window: TimeSpan.FromMinutes(1)));
    }
}
