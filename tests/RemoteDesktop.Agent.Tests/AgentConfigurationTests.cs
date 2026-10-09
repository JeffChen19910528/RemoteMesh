using RemoteDesktop.Agent;
using RemoteDesktop.Client;
using RemoteDesktop.Core;

namespace RemoteDesktop.Agent.Tests;

public sealed class AgentConfigurationTests
{
    private static AgentOptions Valid() => new() { ServerUrl = "https://relay.example.com" };

    private static string Id() => DeviceId.New().ToString();

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public void MinimalSettings_AreValid() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("relay.example.com")]
    public void BadServerUrl_IsRejected(string url)
    {
        var o = Valid();
        o.ServerUrl = url;
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void PlainHttp_IsOnlyAllowedForLocalhost()
    {
        var remote = Valid();
        remote.ServerUrl = "http://relay.example.com";
        Assert.Contains(remote.Validate(), p => p.Contains("https"));

        var local = Valid();
        local.ServerUrl = "http://localhost:5222";
        Assert.Empty(local.Validate());
    }

    [Fact]
    public void RelativeFileRoot_IsRejected()
    {
        var o = Valid();
        o.FileRoot = "files";
        Assert.NotEmpty(o.Validate());
    }

    [Theory]
    [InlineData("garbage", "ViewOnly")]
    [InlineData("", "ViewOnly")]
    public void TrustedPeer_WithABadId_IsRejected(string id, string permissions)
    {
        var o = Valid();
        o.TrustedPeers.Add(new TrustedPeer { DeviceId = id, Permissions = permissions });
        Assert.NotEmpty(o.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("Nonsense")]
    [InlineData("ViewScreen, RemoteShutdown")]
    [InlineData("RemoteRestart")]
    public void TrustedPeer_WithNoRightsOrDangerousRights_IsRejected(string permissions)
    {
        var o = Valid();
        o.TrustedPeers.Add(new TrustedPeer { DeviceId = Id(), Permissions = permissions });
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void TrustedPeer_WithRights_IsAccepted()
    {
        var o = Valid();
        o.TrustedPeers.Add(new TrustedPeer { DeviceId = Id(), Permissions = "ViewScreen, ControlMouse" });
        Assert.Empty(o.Validate());
    }

    // ── Approval ─────────────────────────────────────────────────────────

    private static ApprovalRequest From(DeviceId id) => new(SessionId.New(), id);

    [Fact]
    public async Task ListedDevice_GetsExactlyItsRights()
    {
        var friend = DeviceId.New();
        var handler = new ConfigApprovalHandler([new TrustedPeer { DeviceId = friend.ToString(), Permissions = "ViewScreen, ControlMouse" }]);

        var decision = await handler.DecideAsync(From(friend), default);

        Assert.True(decision.Accept);
        Assert.Equal(Permission.ViewScreen | Permission.ControlMouse, decision.Granted);
    }

    [Fact]
    public async Task UnlistedDevice_IsDeclined()
    {
        var handler = new ConfigApprovalHandler([new TrustedPeer { DeviceId = Id(), Permissions = "FullControl" }]);
        Assert.False((await handler.DecideAsync(From(DeviceId.New()), default)).Accept);
    }

    [Fact]
    public async Task EmptyList_DeclinesEveryone()
    {
        var handler = new ConfigApprovalHandler([]);
        Assert.False((await handler.DecideAsync(From(DeviceId.New()), default)).Accept);
    }

    [Fact]
    public async Task RestartAndShutdown_AreNeverGrantedFromTheList()
    {
        // Validate() rejects such entries; the handler also strips them if one slips through.
        var friend = DeviceId.New();
        var handler = new ConfigApprovalHandler([new TrustedPeer
        {
            DeviceId = friend.ToString(),
            Permissions = (Permission.ViewScreen | Permission.RemoteShutdown | Permission.RemoteRestart).ToString()
        }]);

        var decision = await handler.DecideAsync(From(friend), default);

        Assert.True(decision.Accept);
        Assert.Equal(Permission.ViewScreen, decision.Granted);
    }

    [Fact]
    public async Task ListedDeviceWithOnlyDangerousRights_IsDeclined()
    {
        var friend = DeviceId.New();
        var handler = new ConfigApprovalHandler([new TrustedPeer { DeviceId = friend.ToString(), Permissions = "RemoteShutdown" }]);
        Assert.False((await handler.DecideAsync(From(friend), default)).Accept);
    }

    [Fact]
    public async Task MalformedEntries_AreIgnored_NotTrusted()
    {
        var handler = new ConfigApprovalHandler([
            new TrustedPeer { DeviceId = "garbage", Permissions = "FullControl" },
            new TrustedPeer { DeviceId = null, Permissions = null }]);
        Assert.False((await handler.DecideAsync(From(DeviceId.New()), default)).Accept);
    }
}
