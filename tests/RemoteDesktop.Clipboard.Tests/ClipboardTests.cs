using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Clipboard.Tests;

public sealed class ClipboardTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    private readonly InMemoryClipboardManager _clip = new();
    private SessionInfo _session = null!;
    private ClipboardClient _client = null!;
    private Task _hostLoop = null!;
    private Task _clientLoop = null!;
    private LoopbackChannel _viewerCh = null!;
    private LoopbackChannel _hostCh = null!;
    private ClipboardHostOptions? _hostOptions;
    private Func<DateTimeOffset>? _clock;

    private static SessionInfo Session(Permission p, SessionState state = SessionState.Connected) => new()
    {
        SessionId = SessionId.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        State = state,
        Permissions = new PermissionSet(p)
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_hostCh is not null) await _hostCh.DisposeAsync();
        if (_viewerCh is not null) await _viewerCh.DisposeAsync();
        if (_hostLoop is not null) await _hostLoop;
        if (_clientLoop is not null) await _clientLoop;
    }

    private void Start(Permission perms, ClipboardPolicy policy, SessionState state = SessionState.Connected)
    {
        _session = Session(perms, state);
        _clip.Policy = policy;
        _viewerCh = new LoopbackChannel(ChannelType.Clipboard);
        _hostCh = new LoopbackChannel(ChannelType.Clipboard);
        _viewerCh.PeerWriter = _hostCh.Writer;
        _hostCh.PeerWriter = _viewerCh.Writer;
        var host = new ClipboardHost(_hostCh, _session, _clip, _hostOptions, _clock);
        _client = new ClipboardClient(_viewerCh);
        _hostLoop = host.RunAsync(_cts.Token);
        _clientLoop = _client.RunAsync(_cts.Token);
    }

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    // ── Wire format ──────────────────────────────────────────────────────

    [Fact]
    public void Message_RoundTrips()
    {
        var m = new ClipboardMessage(ClipboardMessageType.Data, ClipboardFormat.Image, [1, 2, 3]);
        Assert.True(ClipboardMessage.TryParse(m.ToBytes(), out var r));
        Assert.Equal(m.Type, r!.Type);
        Assert.Equal(m.Payload, r.Payload);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 99, 0 })]
    [InlineData(new byte[] { 0, 99 })]
    public void Message_RejectsMalformed(byte[] data) =>
        Assert.False(ClipboardMessage.TryParse(data, out _));

    [Fact]
    public void Message_RejectsOversizedText()
    {
        var big = new byte[2 + ClipboardLimits.MaxTextBytes + 1];
        big[0] = (byte)ClipboardMessageType.WriteRequest;
        big[1] = (byte)ClipboardFormat.Text;
        Assert.False(ClipboardMessage.TryParse(big, out _));
    }

    [Fact]
    public void Text_RejectsInvalidUtf8() =>
        Assert.False(ClipboardMessage.TryDecodeText([0xFF, 0xFE, 0xFD], out _));

    // ── Policy ───────────────────────────────────────────────────────────

    [Fact]
    public void DefaultPolicy_IsDisabled() =>
        Assert.Equal(ClipboardPolicy.Disabled, new InMemoryClipboardManager().Policy);

    [Fact]
    public async Task Disabled_DeniesReadAndWrite_EvenWithFullPermission()
    {
        Start(Permission.FullControl, ClipboardPolicy.Disabled);
        await _clip.SetTextAsync("secret");

        Assert.Equal(ClipboardResult.PolicyDenied,
            (await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Result);
        Assert.Equal(ClipboardResult.PolicyDenied, await _client.WriteTextAsync("x", Short, _cts.Token));
        Assert.Equal("secret", await _clip.GetTextAsync());
    }

    [Fact]
    public async Task ReadOnlyPolicy_AllowsReadOnly()
    {
        Start(Permission.FullControl, ClipboardPolicy.ReadOnly);
        await _clip.SetTextAsync("hello");

        var read = await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token);
        Assert.True(read.Ok);
        Assert.True(ClipboardMessage.TryDecodeText(read.Data!, out var text));
        Assert.Equal("hello", text);
        Assert.Equal(ClipboardResult.PolicyDenied, await _client.WriteTextAsync("x", Short, _cts.Token));
    }

    [Fact]
    public async Task WriteOnlyPolicy_AllowsWriteOnly()
    {
        Start(Permission.FullControl, ClipboardPolicy.WriteOnly);
        await _clip.SetTextAsync("host-private");

        Assert.Equal(ClipboardResult.Ok, await _client.WriteTextAsync("from viewer", Short, _cts.Token));
        Assert.Equal("from viewer", await _clip.GetTextAsync());
        Assert.Equal(ClipboardResult.PolicyDenied,
            (await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Result);
    }

    [Fact]
    public async Task Bidirectional_AllowsBoth_Text_And_Image()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);

        Assert.Equal(ClipboardResult.Ok, await _client.WriteAsync(ClipboardFormat.Image, [9, 8, 7], Short, _cts.Token));
        var img = await _client.ReadAsync(ClipboardFormat.Image, Short, _cts.Token);
        Assert.Equal(new byte[] { 9, 8, 7 }, img.Data);
    }

    // ── Session permissions ──────────────────────────────────────────────

    [Fact]
    public async Task PolicyOpen_ButNoSessionPermission_Denied()
    {
        Start(Permission.ViewOnly, ClipboardPolicy.Bidirectional);
        await _clip.SetTextAsync("x");

        Assert.Equal(ClipboardResult.PermissionDenied,
            (await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Result);
        Assert.Equal(ClipboardResult.PermissionDenied, await _client.WriteTextAsync("y", Short, _cts.Token));
    }

    [Fact]
    public async Task ReadPermissionDoesNotImplyWrite()
    {
        Start(Permission.ViewScreen | Permission.ClipboardRead, ClipboardPolicy.Bidirectional);
        await _clip.SetTextAsync("x");

        Assert.True((await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Ok);
        Assert.Equal(ClipboardResult.PermissionDenied, await _client.WriteTextAsync("y", Short, _cts.Token));
    }

    [Fact]
    public async Task RevokedPermission_AppliesImmediately()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);
        await _clip.SetTextAsync("x");
        Assert.True((await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Ok);

        _session.Permissions.Revoke(Permission.ClipboardRead);
        Assert.Equal(ClipboardResult.PermissionDenied,
            (await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Result);
    }

    [Fact]
    public async Task HostPolicyChange_AppliesImmediately()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);
        Assert.Equal(ClipboardResult.Ok, await _client.WriteTextAsync("a", Short, _cts.Token));
        _clip.Policy = ClipboardPolicy.Disabled;
        Assert.Equal(ClipboardResult.PolicyDenied, await _client.WriteTextAsync("b", Short, _cts.Token));
        Assert.Equal("a", await _clip.GetTextAsync());
    }

    [Theory]
    [InlineData(SessionState.Authorizing)]
    [InlineData(SessionState.Disconnecting)]
    [InlineData(SessionState.Failed)]
    public async Task NonConnectedSession_Denied(SessionState state)
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional, state);
        Assert.Equal(ClipboardResult.SessionNotActive, await _client.WriteTextAsync("x", Short, _cts.Token));
    }

    // ── Limits / robustness ──────────────────────────────────────────────

    [Fact]
    public async Task EmptyClipboard_ReportsEmpty()
    {
        Start(Permission.FullControl, ClipboardPolicy.ReadOnly);
        Assert.Equal(ClipboardResult.Empty,
            (await _client.ReadAsync(ClipboardFormat.Text, Short, _cts.Token)).Result);
    }

    [Fact]
    public async Task Client_RejectsOversizeAndEmptyWritesLocally()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);
        Assert.Equal(ClipboardResult.TooLarge, await _client.WriteAsync(ClipboardFormat.Text,
            new byte[ClipboardLimits.MaxTextBytes + 1], Short, _cts.Token));
        Assert.Equal(ClipboardResult.Empty, await _client.WriteAsync(ClipboardFormat.Text, [], Short, _cts.Token));
    }

    [Fact]
    public async Task InvalidUtf8Write_Rejected_ClipboardUnchanged()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);
        await _clip.SetTextAsync("keep");
        Assert.Equal(ClipboardResult.Malformed,
            await _client.WriteAsync(ClipboardFormat.Text, [0xFF, 0xFE], Short, _cts.Token));
        Assert.Equal("keep", await _clip.GetTextAsync());
    }

    [Fact]
    public async Task HostIgnoresViewerSpoofedHostMessages()
    {
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);
        // A viewer sending a "Data"/"Ack" message must not change the clipboard.
        await _viewerCh.SendAsync(new ClipboardMessage(ClipboardMessageType.Data, ClipboardFormat.Text,
            ClipboardMessage.EncodeText("evil")).ToBytes());
        await Task.Delay(100);
        Assert.Null(await _clip.GetTextAsync());
    }

    [Fact]
    public async Task RateLimit_BlocksFlood_ThenRecovers()
    {
        var now = DateTimeOffset.UtcNow;
        _clock = () => now;
        _hostOptions = new ClipboardHostOptions { MaxRequestsPerWindow = 2, Window = TimeSpan.FromSeconds(10) };
        Start(Permission.FullControl, ClipboardPolicy.Bidirectional);

        Assert.Equal(ClipboardResult.Ok, await _client.WriteTextAsync("1", Short, _cts.Token));
        Assert.Equal(ClipboardResult.Ok, await _client.WriteTextAsync("2", Short, _cts.Token));
        Assert.Equal(ClipboardResult.RateLimited, await _client.WriteTextAsync("3", Short, _cts.Token));

        now = now.AddSeconds(11);
        Assert.Equal(ClipboardResult.Ok, await _client.WriteTextAsync("4", Short, _cts.Token));
        Assert.Equal("4", await _clip.GetTextAsync());
    }

    [Fact]
    public void ClientAndHost_RejectNonClipboardChannel()
    {
        var ch = new LoopbackChannel(ChannelType.Input);
        Assert.Throws<ArgumentException>(() => new ClipboardClient(ch));
        Assert.Throws<ArgumentException>(() =>
            new ClipboardHost(ch, Session(Permission.FullControl), new InMemoryClipboardManager()));
    }
}
