using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Capture;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Input;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Relay;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Streaming;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Reliability;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Integration.Tests;

/// <summary>
/// Acceptance: the whole product path with real components and no shortcuts —
/// device authentication → request → approval/OTP/trust → relay pairing → end-to-end encryption →
/// screen, input, clipboard, file and keep-alive channels → permission enforcement → recovery → teardown + audit.
/// The only simulated parts are the network (in-process) and the screen/input/clipboard OS backends.
/// </summary>
public sealed class AcceptanceTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<HubConnection> _hubs = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-accept-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        foreach (var h in _hubs) await h.DisposeAsync();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Parties ──────────────────────────────────────────────────────────

    private sealed class Party
    {
        public required HubConnection Conn { get; init; }
        public required DeviceInfoDto Dto { get; init; }
        public DeviceId Id => DeviceId.Parse(Dto.DeviceId);
        public ConcurrentQueue<SessionDto> Incoming { get; } = new();
        public ConcurrentQueue<SessionDto> StateChanges { get; } = new();
        public ConcurrentQueue<AuthResultDto> AuthSuccess { get; } = new();
        public ConcurrentQueue<AuthResultDto> Authorized { get; } = new();   // target's token (approval flow)
        public ConcurrentQueue<AuthResultDto> Established { get; } = new();  // target's token (OTP / trusted flow)
        public ConcurrentQueue<string> RecoveryTokens { get; } = new();
        public ConcurrentQueue<string> Closed { get; } = new();
    }

    private async Task<Party> NewPartyAsync(string name)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        _hubs.Add(conn);
        var p = new Party { Conn = conn, Dto = TestAuth.MakeDevice(name) };
        conn.On<SessionDto>("IncomingSessionRequest", d => p.Incoming.Enqueue(d));
        conn.On<SessionDto>("SessionStateChanged", d => p.StateChanges.Enqueue(d));
        conn.On<AuthResultDto>("AuthSuccess", d => p.AuthSuccess.Enqueue(d));
        conn.On<AuthResultDto>("SessionAuthorized", d => p.Authorized.Enqueue(d));
        conn.On<AuthResultDto>("SessionEstablished", d => p.Established.Enqueue(d));
        conn.On<string, string>("RecoveryTokenIssued", (_, t) => p.RecoveryTokens.Enqueue(t));
        conn.On<string, string>("SessionClosed", (id, _) => p.Closed.Enqueue(id));
        await conn.StartAsync(_cts.Token);
        await TestAuth.RegisterAsync(conn, p.Dto);
        return p;
    }

    private static async Task Eventually(Func<bool> cond, int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond(), "condition not reached in time");
    }

    private T Svc<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    // ── One side of an established, encrypted session ────────────────────

    private sealed class Endpoint
    {
        public required SecureTransport Transport { get; init; }
        public required Dictionary<ChannelType, IChannel> Channels { get; init; }
        public IChannel this[ChannelType t] => Channels[t];
    }

    private async Task<Endpoint> OpenEndpointAsync(Party me, Party peer, SessionId sid, string relayToken, int epoch, bool initiator)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["X-Relay-Token"] = relayToken;
        var ws = await client.ConnectAsync(
            new Uri($"ws://localhost/ws/relay?sessionId={sid}&deviceId={me.Dto.DeviceId}"), _cts.Token);

        // The peer's public key comes from the server's registry; our private key never leaves this process.
        var peerKey = (await Svc<IDeviceRegistry>().GetPublicKeyAsync(peer.Id))!;
        var crypto = new AesGcmSessionCrypto();
        await crypto.DeriveSessionKeyAsync(sid, TestAuth.KeysOf(me.Dto.DeviceId).PrivateKey, peerKey,
            SessionKeyContext.ForEpoch(epoch));

        var transport = new SecureTransport(new RelayTransport(ws, sid), crypto, initiator);
        _disposables.Add(transport);
        var channels = new Dictionary<ChannelType, IChannel>();
        foreach (var t in Enum.GetValues<ChannelType>()) channels[t] = await transport.OpenChannelAsync(t, _cts.Token);
        return new Endpoint { Transport = transport, Channels = channels };
    }

    private sealed class Recorder : IInputController
    {
        public ConcurrentQueue<InputEvent> Injected { get; } = new();
        public Task InjectAsync(InputEvent e, CancellationToken ct = default)
        {
            Injected.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    /// <summary>The host application: serves every channel according to the permissions the user granted.</summary>
    private sealed class HostApp
    {
        public required SessionInfo Session { get; init; }
        public required Recorder Input { get; init; }
        public required InMemoryClipboardManager Clipboard { get; init; }
        public required string FileRoot { get; init; }
        public ConcurrentQueue<InputDecision> InputDecisions { get; } = new();
        public KeepAliveService KeepAlive { get; set; } = null!;
    }

    private HostApp StartHost(Endpoint host, AuthResultDto granted, string root, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var session = new SessionInfo
        {
            SessionId = SessionId.Parse(granted.SessionId),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            State = SessionState.Connected,
            Permissions = new PermissionSet((Permission)granted.GrantedPermissions)
        };
        var app = new HostApp { Session = session, Input = new Recorder(), Clipboard = new InMemoryClipboardManager(), FileRoot = root };

        var gate = new InputGate(session, app.Input, new InputGateOptions { MaxEventsPerSecond = 100_000 }, onDecision: app.InputDecisions.Enqueue);
        _ = new InputReceiver(host[ChannelType.Input], gate).RunAsync(ct);
        _ = new ClipboardHost(host[ChannelType.Clipboard], session, app.Clipboard).RunAsync(ct);
        _ = new FileTransferHost(host[ChannelType.File], session, root).RunAsync(ct);
        app.KeepAlive = new KeepAliveService(host[ChannelType.Control], new KeepAliveOptions { Interval = TimeSpan.FromMilliseconds(100) });
        _ = app.KeepAlive.RunAsync(ct);
        return app;
    }

    private sealed class FramesCapture(IReadOnlyList<CaptureFrame> frames) : IScreenCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsCapturing = false; return Task.CompletedTask; }
        public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var f in frames)
            {
                yield return new CaptureFrame { Width = f.Width, Height = f.Height, PixelData = f.PixelData };
                await Task.Delay(20, ct);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CollectingRenderer : IFrameRenderer
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            Frames.Enqueue(frame.PixelData.ToArray()); // pooled pixels: copy
            return Task.CompletedTask;
        }
    }

    private static List<CaptureFrame> Scene(int count)
    {
        var list = new List<CaptureFrame>();
        for (var i = 0; i < count; i++)
        {
            var px = new byte[160 * 96 * 4];
            for (var p = 0; p < px.Length; p += 4) { px[p] = 30; px[p + 1] = 60; px[p + 2] = 90; px[p + 3] = 255; }
            for (var y = 10; y < 30; y++)
                for (var x = 5 + i * 3; x < 25 + i * 3; x++) { var o = (y * 160 + x) * 4; px[o] = 250; px[o + 1] = 200; px[o + 2] = 10; }
            list.Add(new CaptureFrame { Width = 160, Height = 96, PixelData = px });
        }
        return list;
    }

    private static readonly VideoQuality NoScaling = new() { Width = 8192, Height = 8192, Fps = 25, BitrateKbps = 1, KeyframeIntervalSeconds = 100 };

    /// <summary>Alice requests Bob, Bob approves with <paramref name="perms"/>; both get their tokens; transports open.</summary>
    private async Task<(Party alice, Party bob, SessionDto session, Endpoint viewer, Endpoint host, AuthResultDto granted)>
        ApprovedSessionAsync(Permission perms)
    {
        var alice = await NewPartyAsync("Alice-viewer");
        var bob = await NewPartyAsync("Bob-host");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Eventually(() => !bob.Incoming.IsEmpty);
        Assert.Equal(alice.Dto.DeviceId, bob.Incoming.Single().RequesterDeviceId); // the prompt names who is asking

        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)perms, _cts.Token);
        await Eventually(() => !alice.AuthSuccess.IsEmpty && !bob.Authorized.IsEmpty);
        var granted = alice.AuthSuccess.Single();
        Assert.Equal((long)perms, granted.GrantedPermissions);

        var sid = SessionId.Parse(s.SessionId);
        var viewer = await OpenEndpointAsync(alice, bob, sid, granted.SessionToken, 0, initiator: true);
        var host = await OpenEndpointAsync(bob, alice, sid, bob.Authorized.Single().SessionToken, 0, initiator: false);

        await alice.Conn.InvokeAsync("ReportConnectionMode", s.SessionId, "Relay", _cts.Token);
        await Eventually(() => !alice.RecoveryTokens.IsEmpty && !bob.RecoveryTokens.IsEmpty);
        return (alice, bob, s, viewer, host, granted);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 1. Interactive approval: every channel, permissions enforced, audited
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approved_Session_ServesScreen_Input_Clipboard_Files_AndEnforcesPermissions_ThenTearsDownCleanly()
    {
        // Bob grants: view, mouse, clipboard read, file upload + download. NOT keyboard, NOT clipboard write.
        var perms = Permission.ViewScreen | Permission.ControlMouse | Permission.ClipboardRead
                    | Permission.FileUpload | Permission.FileDownload;
        var (alice, bob, session, viewer, host, granted) = await ApprovedSessionAsync(perms);
        var app = StartHost(host, granted, Path.Combine(_dir, "host-root"), _cts.Token);
        var sid = SessionId.Parse(session.SessionId);

        // ── Screen: host captures and encodes, viewer decodes and renders, pixel-exact ──
        var frames = Scene(12);
        var renderer = new CollectingRenderer();
        var receiver = new DesktopStreamReceiver(viewer[ChannelType.Desktop], new TileDeltaVideoDecoder(), renderer);
        _ = receiver.RunAsync(_cts.Token);
        var sender = new DesktopStreamSender(new FramesCapture(frames), new TileDeltaVideoEncoder { Quality = NoScaling }, host[ChannelType.Desktop]);
        await sender.RunAsync(_cts.Token);
        await Eventually(() => renderer.Frames.Count == frames.Count);
        Assert.Equal(frames.Select(f => f.PixelData.ToArray()), renderer.Frames.ToArray());
        Assert.Equal(0, receiver.FramesCorrupt);

        // ── Input: mouse allowed, keyboard refused (not granted) ──
        var tx = new InputSender(viewer[ChannelType.Input]);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 100, Y = 50 }, _cts.Token);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.KeyDown, KeyCode = 65 }, _cts.Token);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.MouseWheel, Delta = 120 }, _cts.Token);
        await Eventually(() => app.InputDecisions.Count == 3);
        Assert.Equal(new[] { InputDecision.Injected, InputDecision.PermissionDenied, InputDecision.Injected }, app.InputDecisions.ToArray());
        Assert.Equal(2, app.Input.Injected.Count);
        Assert.DoesNotContain(app.Input.Injected, e => e.EventType == InputEventType.KeyDown);

        // ── Clipboard: off by default on the host, then opt-in read-only; write never allowed ──
        var clip = new ClipboardClient(viewer[ChannelType.Clipboard]);
        _ = clip.RunAsync(_cts.Token);
        await app.Clipboard.SetTextAsync("host secret");
        Assert.Equal(ClipboardResult.PolicyDenied, (await clip.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5), _cts.Token)).Result);

        app.Clipboard.Policy = ClipboardPolicy.ReadOnly; // the host user opts in
        var read = await clip.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5), _cts.Token);
        Assert.True(read.Ok);
        Assert.Equal("host secret", System.Text.Encoding.UTF8.GetString(read.Data!));
        Assert.Equal(ClipboardResult.PolicyDenied, await clip.WriteTextAsync("pwned", TimeSpan.FromSeconds(5), _cts.Token));
        Assert.Equal("host secret", await app.Clipboard.GetTextAsync());

        app.Session.Permissions.Revoke(Permission.ClipboardRead); // revoked mid-session: effective immediately
        Assert.Equal(ClipboardResult.PermissionDenied, (await clip.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5), _cts.Token)).Result);

        // ── Files: upload and download, byte-exact, checksummed ──
        var files = new FileTransferClient(viewer[ChannelType.File]);
        _ = files.RunAsync(_cts.Token);
        var src = Path.Combine(_dir, "doc.bin");
        var bytes = new byte[700_000];
        new Random(3).NextBytes(bytes);
        await File.WriteAllBytesAsync(src, bytes);

        Assert.True((await files.UploadAsync(src, "doc.bin", ct: _cts.Token)).Ok);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(app.FileRoot, "doc.bin")));
        var back = Path.Combine(_dir, "back.bin");
        Assert.True((await files.DownloadAsync("doc.bin", back, ct: _cts.Token)).Ok);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(back));
        Assert.Equal(FileTransferResult.InvalidName, (await files.UploadAsync(src, "../escape.bin", ct: _cts.Token)).Result);

        // ── Keep-alive: the host measures a real round trip through the relay ──
        var viewerKeepAlive = new KeepAliveService(viewer[ChannelType.Control], new KeepAliveOptions { Interval = TimeSpan.FromMilliseconds(100) });
        _ = viewerKeepAlive.RunAsync(_cts.Token);
        await Eventually(() => viewerKeepAlive.LastRtt is not null && app.KeepAlive.LastRtt is not null);
        Assert.False(viewerKeepAlive.IsLost);

        // ── Teardown: close, relay gone, tokens dead, peer told ──
        await alice.Conn.InvokeAsync("CloseSession", session.SessionId, "done", _cts.Token);
        await Eventually(() => !bob.Closed.IsEmpty);
        await Eventually(() => ((RemoteDesktop.Server.Relay.InMemoryRelayService)Svc<IRelayService>()).ActivePairings == 0);
        Assert.False(Svc<ISessionTokenService>().Validate(granted.SessionToken, sid, alice.Id));
        Assert.False(Svc<ISessionTokenService>().Validate(alice.RecoveryTokens.Single(), sid, alice.Id));

        // ── Audit trail tells the whole story ──
        var audit = await Svc<IAuditLog>().QueryAsync(alice.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        var types = audit.Where(e => e.SessionId == sid).Select(e => e.EventType).ToHashSet();
        foreach (var expected in new[]
                 {
                     AuditEventType.RemoteSessionRequested, AuditEventType.RemoteSessionApproved, AuditEventType.PermissionGranted,
                     AuditEventType.RemoteSessionStarted, AuditEventType.RelaySessionStarted, AuditEventType.RelaySessionEnded,
                     AuditEventType.RemoteSessionTerminated
                 })
            Assert.Contains(expected, types);
        Assert.All(audit.Where(e => e.SessionId == sid), e => Assert.DoesNotContain("secret", e.Reason ?? "", StringComparison.OrdinalIgnoreCase));
        var grant = audit.Single(e => e.SessionId == sid && e.EventType == AuditEventType.PermissionGranted);
        Assert.Equal(bob.Id, grant.ActorDeviceId);
        Assert.Equal(alice.Id, grant.TargetDeviceId);
        Assert.Equal(perms.ToString(), grant.Reason); // the trail shows exactly what was allowed
    }

    // ═════════════════════════════════════════════════════════════════════
    // 2. View-only is really view-only
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ViewOnlySession_AllowsWatching_ButNothingElse()
    {
        var (_, _, _, viewer, host, granted) = await ApprovedSessionAsync(Permission.ViewOnly);
        var app = StartHost(host, granted, Path.Combine(_dir, "root"), _cts.Token);
        app.Clipboard.Policy = ClipboardPolicy.Bidirectional; // even a permissive host policy cannot exceed the grant

        var renderer = new CollectingRenderer();
        _ = new DesktopStreamReceiver(viewer[ChannelType.Desktop], new TileDeltaVideoDecoder(), renderer).RunAsync(_cts.Token);
        await new DesktopStreamSender(new FramesCapture(Scene(5)), new TileDeltaVideoEncoder { Quality = NoScaling }, host[ChannelType.Desktop]).RunAsync(_cts.Token);
        await Eventually(() => renderer.Frames.Count == 5);

        var tx = new InputSender(viewer[ChannelType.Input]);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 1, Y = 1 }, _cts.Token);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.KeyDown, KeyCode = 13 }, _cts.Token);
        await Eventually(() => app.InputDecisions.Count == 2);
        Assert.All(app.InputDecisions, d => Assert.Equal(InputDecision.PermissionDenied, d));
        Assert.Empty(app.Input.Injected);

        var clip = new ClipboardClient(viewer[ChannelType.Clipboard]);
        _ = clip.RunAsync(_cts.Token);
        Assert.Equal(ClipboardResult.PermissionDenied, (await clip.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5), _cts.Token)).Result);
        Assert.Equal(ClipboardResult.PermissionDenied, await clip.WriteTextAsync("x", TimeSpan.FromSeconds(5), _cts.Token));

        var files = new FileTransferClient(viewer[ChannelType.File]);
        _ = files.RunAsync(_cts.Token);
        var src = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(src, "hello");
        Assert.Equal(FileTransferResult.PermissionDenied, (await files.UploadAsync(src, "a.txt", ct: _cts.Token)).Result);
        Assert.Equal(FileTransferResult.PermissionDenied, (await files.DownloadAsync("a.txt", Path.Combine(_dir, "o"), ct: _cts.Token)).Result);
        Assert.Empty(Directory.GetFiles(app.FileRoot));
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3. The other ways in: OTP and trusted device
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OtpSession_ConnectsWithoutAnApprovalDialog_AndStreams()
    {
        var alice = await NewPartyAsync("Alice");
        var bob = await NewPartyAsync("Bob");
        var code = await bob.Conn.InvokeAsync<string>("GenerateOtp", _cts.Token); // shown on the host's screen

        var auth = await alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, code, _cts.Token);
        await Eventually(() => !bob.Established.IsEmpty);
        var sid = SessionId.Parse(auth.SessionId);

        var viewer = await OpenEndpointAsync(alice, bob, sid, auth.SessionToken, 0, true);
        var host = await OpenEndpointAsync(bob, alice, sid, bob.Established.Single().SessionToken, 0, false);
        var renderer = new CollectingRenderer();
        _ = new DesktopStreamReceiver(viewer[ChannelType.Desktop], new TileDeltaVideoDecoder(), renderer).RunAsync(_cts.Token);
        await new DesktopStreamSender(new FramesCapture(Scene(4)), new TileDeltaVideoEncoder { Quality = NoScaling }, host[ChannelType.Desktop]).RunAsync(_cts.Token);

        await Eventually(() => renderer.Frames.Count == 4);
        Assert.Equal(Permission.ViewOnly, (Permission)auth.GrantedPermissions); // OTP never grants more than viewing
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            alice.Conn.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bob.Dto.DeviceId, code, _cts.Token)); // used up
    }

    [Fact]
    public async Task TrustedDevice_ConnectsAutomatically_WithTheStandingPermissions()
    {
        var alice = await NewPartyAsync("Alice");
        var bob = await NewPartyAsync("Bob");
        await bob.Conn.InvokeAsync("AddTrustedDevice", alice.Dto.DeviceId, "Alice", (long)(Permission.ViewScreen | Permission.ControlMouse), _cts.Token);

        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Eventually(() => !bob.Established.IsEmpty);
        var sid = SessionId.Parse(s.SessionId);

        Assert.Equal(nameof(SessionState.EstablishingTransport), s.State);
        Assert.Equal((long)(Permission.ViewScreen | Permission.ControlMouse), s.Auth!.GrantedPermissions);
        var viewer = await OpenEndpointAsync(alice, bob, sid, s.Auth.SessionToken, 0, true);
        var host = await OpenEndpointAsync(bob, alice, sid, bob.Established.Single().SessionToken, 0, false);
        var app = StartHost(host, s.Auth, Path.Combine(_dir, "r"), _cts.Token);

        await new InputSender(viewer[ChannelType.Input]).SendAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 3, Y = 4 }, _cts.Token);
        await Eventually(() => app.Input.Injected.Count == 1);
    }

    [Fact]
    public async Task RejectedRequest_NeverGetsATransport()
    {
        var alice = await NewPartyAsync("Alice");
        var bob = await NewPartyAsync("Bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Eventually(() => !bob.Incoming.IsEmpty);

        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, false, 0L, _cts.Token);

        await Eventually(() => alice.StateChanges.Any(c => c.State == nameof(SessionState.Failed)));
        Assert.Empty(alice.AuthSuccess);
        Assert.Empty(bob.Authorized);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["X-Relay-Token"] = "guess"; // no token was ever issued for this session
        using var ws = await client.ConnectAsync(
            new Uri($"ws://localhost/ws/relay?sessionId={s.SessionId}&deviceId={alice.Dto.DeviceId}"), _cts.Token);
        var r = await ws.ReceiveAsync(new byte[8], _cts.Token);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, r.CloseStatus);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 4. Recovery: the link drops mid-session and the session carries on
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DroppedConnection_IsResumed_WithANewKeyEpoch_AndOldTrafficCannotBeReplayed()
    {
        var (alice, bob, session, viewer, host, granted) = await ApprovedSessionAsync(Permission.ViewScreen | Permission.ControlMouse);
        var sid = SessionId.Parse(session.SessionId);
        var app = StartHost(host, granted, Path.Combine(_dir, "r1"), _cts.Token);
        var tx = new InputSender(viewer[ChannelType.Input]);
        await tx.SendAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 1, Y = 1 }, _cts.Token);
        await Eventually(() => app.Input.Injected.Count == 1);
        var recoveryToken = alice.RecoveryTokens.Single();

        // Alice's machine loses its network: both her signaling connection and her relay socket die.
        await alice.Conn.StopAsync(_cts.Token);
        await viewer.Transport.DisposeAsync();
        await Eventually(() => Svc<ISessionManager>().GetAsync(sid).Result!.State == SessionState.Paused);
        await Eventually(() => bob.StateChanges.Any(c => c.State == nameof(SessionState.Paused)));

        // She comes back, proves who she is again, and resumes with her single-use recovery token.
        var back = await NewPartyAsync("Alice-back");
        var aliceAgain = new Party { Conn = back.Conn, Dto = alice.Dto }; // same device identity (same key)
        await TestAuth.RegisterAsync(back.Conn, alice.Dto);
        var resumed = await back.Conn.InvokeAsync<SessionDto>("ResumeSession", session.SessionId, recoveryToken, _cts.Token);
        Assert.Equal(1, resumed.Epoch);
        Assert.Equal(nameof(SessionState.Connected), resumed.State);
        await Eventually(() => bob.StateChanges.Any(c => c.State == nameof(SessionState.Connected) && c.Epoch == 1));

        // Both sides get fresh relay tokens (the old pairing ended with the drop) and move to epoch 1.
        var aliceRelay = await back.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", session.SessionId, _cts.Token);
        var bobRelay = await bob.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", session.SessionId, _cts.Token);
        var viewer2 = await OpenEndpointAsync(aliceAgain, bob, sid, aliceRelay.SessionToken, resumed.Epoch, true);
        await host.Transport.DisposeAsync();
        var host2 = await OpenEndpointAsync(bob, aliceAgain, sid, bobRelay.SessionToken, resumed.Epoch, false);
        var app2 = StartHost(host2, granted, Path.Combine(_dir, "r2"), _cts.Token);

        await new InputSender(viewer2[ChannelType.Input]).SendAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 9, Y = 9 }, _cts.Token);
        await Eventually(() => app2.Input.Injected.Count == 1);
        Assert.Equal(9, app2.Input.Injected.Single().X);

        // The recovery token was single-use: a stolen copy is worthless, and the reply carried a fresh one.
        var thief = await NewPartyAsync("Thief");
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            thief.Conn.InvokeAsync<SessionDto>("ResumeSession", session.SessionId, recoveryToken, _cts.Token));
        Assert.False(string.IsNullOrEmpty(resumed.Auth!.SessionToken));
        Assert.NotEqual(recoveryToken, resumed.Auth.SessionToken);
    }

    [Fact]
    public async Task ConnectionSupervisor_ReestablishesTheTransport_AfterAKeepAliveTimeout()
    {
        var (alice, bob, session, viewer, host, granted) = await ApprovedSessionAsync(Permission.ViewScreen);
        var sid = SessionId.Parse(session.SessionId);
        StartHost(host, granted, Path.Combine(_dir, "rs"), _cts.Token);

        var state = new ConnectionStateMachine();
        foreach (var s in new[] { ConnectionState.Connecting, ConnectionState.Negotiating, ConnectionState.Authenticating,
                     ConnectionState.Authorizing, ConnectionState.EstablishingTransport, ConnectionState.Connected })
            state.Transition(s);

        var reconnected = 0;
        var supervisor = new ConnectionSupervisor(state, new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
            async (attempt, ct) =>
            {
                // What a client does on a lost transport: new relay token, new relay connection, same epoch (session was not paused).
                var tok = await alice.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", session.SessionId, ct);
                var tokB = await bob.Conn.InvokeAsync<AuthResultDto>("RequestRelayToken", session.SessionId, ct);
                await viewer.Transport.DisposeAsync();
                await host.Transport.DisposeAsync();
                var v = await OpenEndpointAsync(alice, bob, sid, tok.SessionToken, 0, true);
                var h = await OpenEndpointAsync(bob, alice, sid, tokB.SessionToken, 0, false);
                var app = StartHost(h, granted, Path.Combine(_dir, "rs2"), _cts.Token);
                await v[ChannelType.Control].SendAsync(new byte[] { 1 }, ct); // the new link carries traffic
                Interlocked.Increment(ref reconnected);
                return app.Session.State == SessionState.Connected;
            });

        // The keep-alive on a silent peer declares the link dead, which is what drives the supervisor.
        var silent = new LoopbackChannel(ChannelType.Control);
        silent.PeerWriter = new LoopbackChannel(ChannelType.Control).Writer; // pings go to a peer that never answers
        var keepAlive = new KeepAliveService(silent, new KeepAliveOptions { Interval = TimeSpan.FromMilliseconds(20), Timeout = TimeSpan.FromMilliseconds(100) });
        var lost = new TaskCompletionSource();
        keepAlive.ConnectionLost += () => lost.TrySetResult();
        _ = keepAlive.RunAsync(_cts.Token);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var outcome = await supervisor.OnConnectionLostAsync(ct: _cts.Token);

        Assert.Equal(RecoveryOutcome.Recovered, outcome);
        Assert.Equal(ConnectionState.Connected, state.State);
        Assert.Equal(1, reconnected);
    }
}
