using RemoteDesktop.Core;

namespace RemoteDesktop.Client.Tests;

// A person who adds a device to their standing trust list: it connects from then on without a prompt, with at
// most the rights given when it was trusted; removing it makes it ask again like anyone else.
public sealed partial class PeerKeyEndToEndTests
{
    [Fact]
    public async Task TrustingADevice_LetsItConnectWithoutAskingThePerson()
    {
        var (host, approver, sessions) = await HostAsync();
        var viewerClient = await StartAsync("viewer");

        await host.TrustPeerAsync(viewerClient.DeviceId, "Viewer", Permission.ViewScreen, _cts.Token);

        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        await Eventually(() => !sessions.IsEmpty, "host session");

        Assert.Equal(0, approver.Asked);          // trusted: nobody was bothered
        Assert.False(viewer.Completion.IsCompleted);
    }

    [Fact]
    public async Task TrustingADevice_OnlyGrantsWhatWasGiven()
    {
        var (host, _, sessions) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.TrustPeerAsync(viewerClient.DeviceId, "Viewer", Permission.ViewScreen, _cts.Token);

        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        await Eventually(() => !sessions.IsEmpty, "host session");

        Assert.Equal(Permission.ViewScreen, viewer.Granted);
    }

    [Fact]
    public async Task GetTrustedDevices_ListsWhatWasTrusted_WithItsNameAndPermissions()
    {
        var (host, _, _) = await HostAsync();
        var viewerClient = await StartAsync("viewer");

        await host.TrustPeerAsync(viewerClient.DeviceId, "Alice's laptop", Permission.ViewScreen | Permission.ControlMouse, _cts.Token);

        var trusted = Assert.Single(await host.GetTrustedDevicesAsync(_cts.Token));
        Assert.Equal(viewerClient.DeviceId.ToString(), trusted.DeviceId);
        Assert.Equal("Alice's laptop", trusted.DeviceName);
        Assert.Equal((long)(Permission.ViewScreen | Permission.ControlMouse), trusted.DefaultPermissions);
    }

    [Fact]
    public async Task UntrustingADevice_MakesItAskAgain()
    {
        var (host, approver, sessions) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.TrustPeerAsync(viewerClient.DeviceId, "Viewer", Permission.ViewScreen, _cts.Token);

        await host.UntrustPeerAsync(viewerClient.DeviceId, _cts.Token);
        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        await Eventually(() => !sessions.IsEmpty, "host session");

        Assert.Equal(1, approver.Asked);          // no longer trusted: the person was asked
        Assert.Empty(await host.GetTrustedDevicesAsync(_cts.Token));
    }

    [Fact]
    public async Task TrustingOneDevice_DoesNotAffectAnother()
    {
        var (host, approver, _) = await HostAsync();
        var trusted = await StartAsync("trusted");
        var other = await StartAsync("other");
        await host.TrustPeerAsync(trusted.DeviceId, "Trusted", Permission.ViewScreen, _cts.Token);

        await using var viaTrust = await trusted.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        Assert.Equal(0, approver.Asked);

        await using var viaPrompt = await other.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        Assert.Equal(1, approver.Asked);
    }
}
