using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client.Tests;

public sealed class PeerBlockListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-block-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "blocked-peers.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ABlockedDevice_IsBlocked_UntilItIsUnblocked()
    {
        var list = new PeerBlockList();
        var a = DeviceId.New();
        Assert.False(list.IsBlocked(a));
        Assert.True(list.Block(a));
        Assert.False(list.Block(a));                 // already there
        Assert.True(list.IsBlocked(a));
        Assert.False(list.IsBlocked(DeviceId.New()));
        Assert.True(list.Unblock(a));
        Assert.False(list.Unblock(a));
        Assert.False(list.IsBlocked(a));
    }

    [Fact]
    public void TheListSurvivesARestart_WithTheNumberItWasKnownBy()
    {
        var a = DeviceId.New();
        new PeerBlockList(File_).Block(a, "482913057");

        var after = new PeerBlockList(File_);
        Assert.True(after.IsBlocked(a));
        var entry = Assert.Single(after.List());
        Assert.Equal(("482913057", a), (entry.ShortId, entry.Device));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"not-a-guid\":{}}")]
    public void ADamagedFile_StartsEmpty_NotCrashing(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);
        var list = new PeerBlockList(File_);
        Assert.Equal(0, list.Count);
        Assert.True(list.Block(DeviceId.New()));      // and works again
    }

    [Fact]
    public void TheListCannotGrowWithoutLimit()
    {
        var list = new PeerBlockList();
        for (var i = 0; i < PeerBlockList.MaxEntries; i++) Assert.True(list.Block(DeviceId.New()));
        Assert.False(list.Block(DeviceId.New()));
        Assert.Equal(PeerBlockList.MaxEntries, list.Count);
    }

    [Fact]
    public void ManyBlocksAtOnce_AreAllKept()
    {
        var list = new PeerBlockList(File_);
        var ids = Enumerable.Range(0, 200).Select(_ => DeviceId.New()).ToArray();
        Parallel.ForEach(ids, id => Assert.True(list.Block(id)));
        Assert.Equal(200, new PeerBlockList(File_).Count);
    }
}

// A person who says "never again" to a device: it is ended at once and stays refused, whoever vouches for it.
public sealed partial class PeerKeyEndToEndTests
{
    private sealed class CountingApprover : IApprovalHandler
    {
        private int _asked;
        public int Asked => Volatile.Read(ref _asked);
        public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult(new ApprovalDecision(true, Permission.ViewScreen));
        }
    }

    private async Task<(RemoteMeshClient Host, CountingApprover Approver, ConcurrentQueue<HostSession> Sessions)> HostAsync(string name = "host")
    {
        var approver = new CountingApprover();
        var services = new HostServices { CaptureFactory = () => new SyntheticScreenCapture(64, 48, fps: 10), Input = new Nobody() };
        var host = new RemoteMeshClient(Options(name), services, approver);
        _disposables.Add(host);
        await host.StartAsync(_cts.Token);
        var sessions = new ConcurrentQueue<HostSession>();
        host.HostSessionStarted += sessions.Enqueue;
        return (host, approver, sessions);
    }

    [Fact]
    public async Task BlockingAConnectedDevice_EndsItsSession_AtOnce()
    {
        var (host, _, sessions) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        await Eventually(() => !sessions.IsEmpty, "host session");

        await host.BlockPeerAsync(viewerClient.DeviceId);

        var ended = await Task.WhenAny(viewer.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(viewer.Completion, ended);       // the viewer is told the session is over
        Assert.True(host.IsBlocked(viewerClient.DeviceId));
    }

    [Fact]
    public async Task ABlockedDevice_IsDeclined_WithoutAskingThePerson()
    {
        var (host, approver, _) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.BlockPeerAsync(viewerClient.DeviceId);

        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token));

        Assert.Equal(0, approver.Asked);              // nobody was bothered
    }

    [Fact]
    public async Task ABlockedDevice_StaysOut_EvenWhenTheServerTrustsIt_OrItKnowsAOneTimeCode()
    {
        var (host, approver, sessions) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.BlockPeerAsync(viewerClient.DeviceId);

        // The server auto-approves trusted devices; blocking is decided on this computer and overrules it.
        var trusted = _factory.Services.GetRequiredService<ITrustedDeviceStore>();
        await trusted.AddAsync(new TrustedDevice { OwnerDeviceId = host.DeviceId, TrustedDeviceId = viewerClient.DeviceId, DefaultPermissions = Permission.ViewOnly });
        try
        {
            await using var viaTrust = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
            var ended = await Task.WhenAny(viaTrust.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(viaTrust.Completion, ended);
        }
        catch (Exception ex) when (ex is ConnectionRefusedException or InvalidOperationException or OperationCanceledException) { /* refused outright: just as good */ }

        var code = await host.GenerateOtpAsync(_cts.Token);
        try
        {
            await using var viaCode = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), otp: code, cancellationToken: _cts.Token);
            var ended = await Task.WhenAny(viaCode.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(viaCode.Completion, ended);
        }
        catch (Exception ex) when (ex is ConnectionRefusedException or InvalidOperationException or OperationCanceledException) { }

        Assert.Equal(0, approver.Asked);
        Assert.True(sessions.IsEmpty);                // no session was ever shown as running
    }

    [Fact]
    public async Task Unblocking_LetsTheDeviceAskAgain_AndItIsAskedAbout()
    {
        var (host, approver, _) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.BlockPeerAsync(viewerClient.DeviceId);
        Assert.True(host.UnblockPeer(viewerClient.DeviceId));

        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);

        Assert.Equal(1, approver.Asked);
        Assert.False(viewer.Completion.IsCompleted);
    }

    [Fact]
    public async Task TheBlockSurvivesARestartOfTheComputer()
    {
        var (host, _, _) = await HostAsync();
        var viewerClient = await StartAsync("viewer");
        await host.BlockPeerAsync(viewerClient.DeviceId, "123456789");
        await host.DisposeAsync();

        var (again, approver, _) = await HostAsync();   // same data directory
        Assert.True(again.IsBlocked(viewerClient.DeviceId));
        Assert.Equal("123456789", Assert.Single(again.BlockedPeers).ShortId);
        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(again.DeviceId, new Sink(), cancellationToken: _cts.Token));
        Assert.Equal(0, approver.Asked);
    }

    [Fact]
    public async Task BlockingOneDevice_DoesNotAffectAnother()
    {
        var (host, approver, _) = await HostAsync();
        var bad = await StartAsync("bad");
        var good = await StartAsync("good");
        await host.BlockPeerAsync(bad.DeviceId);

        await using var viewer = await good.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);

        Assert.Equal(1, approver.Asked);
        Assert.False(viewer.Completion.IsCompleted);
    }
}
