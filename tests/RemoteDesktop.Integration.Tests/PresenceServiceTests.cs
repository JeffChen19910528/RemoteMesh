using RemoteDesktop.Core;
using RemoteDesktop.Server.Presence;

namespace RemoteDesktop.Integration.Tests;

public sealed class PresenceServiceTests
{
    private static readonly InMemoryPresenceService Svc = new();

    [Fact]
    public async Task UnknownDevice_IsOffline()
    {
        var status = await Svc.GetAsync(DeviceId.New());
        Assert.Equal(PresenceStatus.Offline, status);
    }

    [Fact]
    public async Task UpdateOnline_ReportsOnline()
    {
        var id = DeviceId.New();
        await Svc.UpdateAsync(id, PresenceStatus.Online);
        Assert.Equal(PresenceStatus.Online, await Svc.GetAsync(id));
    }

    [Fact]
    public async Task Heartbeat_KeepsOnline()
    {
        var svc = new InMemoryPresenceService();
        var id = DeviceId.New();
        await svc.UpdateAsync(id, PresenceStatus.Online);
        await svc.HeartbeatAsync(id);
        Assert.Equal(PresenceStatus.Online, await svc.GetAsync(id));
    }

    [Fact]
    public async Task UpdateToBusy_ReportsBusy()
    {
        var svc = new InMemoryPresenceService();
        var id = DeviceId.New();
        await svc.UpdateAsync(id, PresenceStatus.Busy);
        Assert.Equal(PresenceStatus.Busy, await svc.GetAsync(id));
    }

    [Fact]
    public async Task UpdateToOffline_ReportsOffline()
    {
        var svc = new InMemoryPresenceService();
        var id = DeviceId.New();
        await svc.UpdateAsync(id, PresenceStatus.Online);
        await svc.UpdateAsync(id, PresenceStatus.Offline);
        Assert.Equal(PresenceStatus.Offline, await svc.GetAsync(id));
    }
}
