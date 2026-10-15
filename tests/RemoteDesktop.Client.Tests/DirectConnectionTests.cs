using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client.Tests;

/// <summary>
/// Two real clients against the real server, connecting straight to each other over TCP (loopback here) instead of
/// through the relay, and falling back to the relay when the other side does not take part.
/// </summary>
public sealed class DirectConnectionTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-direct-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));
    private int _relayConnections;

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder : IInputController
    {
        public ConcurrentQueue<InputEvent> Injected { get; } = new();
        public Task InjectAsync(InputEvent e, CancellationToken ct = default) { Injected.Enqueue(e); return Task.CompletedTask; }
    }

    private sealed class Collector : IFrameRenderer
    {
        private long _frames;
        public long Frames => Interlocked.Read(ref _frames);
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default) { Interlocked.Increment(ref _frames); return Task.CompletedTask; }
    }

    private sealed class Approver : IApprovalHandler
    {
        public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new ApprovalDecision(true, Permission.ViewScreen | Permission.ControlMouse));
    }

    private ClientOptions Options(string name, bool direct) => new()
    {
        ServerUri = new Uri("http://localhost/"),
        DeviceName = name,
        DataDirectory = Path.Combine(_dir, name),
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveTimeout = TimeSpan.FromSeconds(10),
        DirectConnection = direct,
        DirectIncludeLoopback = true,
        ConfigureHub = o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        },
        RelayConnector = async (uri, token, ct) =>
        {
            Interlocked.Increment(ref _relayConnections);
            var ws = _factory.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await ws.ConnectAsync(uri, ct);
        }
    };

    private async Task<(HostSession Host, ViewerSession Viewer, Collector Screen, Recorder Input)> ConnectAsync(bool hostDirect, bool viewerDirect)
    {
        var input = new Recorder();
        var hostClient = new RemoteMeshClient(Options("host", hostDirect),
            new HostServices { CaptureFactory = () => new SyntheticScreenCapture(160, 96, fps: 30), Input = input }, new Approver());
        _disposables.Add(hostClient);
        await hostClient.StartAsync(_cts.Token);
        var hostSessions = new ConcurrentQueue<HostSession>();
        hostClient.HostSessionStarted += hostSessions.Enqueue;

        var viewerClient = new RemoteMeshClient(Options("viewer", viewerDirect));
        _disposables.Add(viewerClient);
        await viewerClient.StartAsync(_cts.Token);

        var screen = new Collector();
        var viewer = await viewerClient.ConnectAsync(hostClient.DeviceId, screen, cancellationToken: _cts.Token);
        _disposables.Add(viewer);
        await Eventually(() => !hostSessions.IsEmpty, "host session started");
        hostSessions.TryPeek(out var host);
        await Eventually(() => screen.Frames >= 5, "first frames");
        return (host!, viewer, screen, input);
    }

    private static async Task Eventually(Func<bool> cond, string what, int seconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(20);
        Assert.True(cond(), $"not reached in time: {what}");
    }

    private async Task AssertWorksAsync(ViewerSession viewer, Collector screen, Recorder input)
    {
        var before = screen.Frames;
        await Eventually(() => screen.Frames >= before + 5, "new frames");
        var x = 5000 + (int)(DateTime.UtcNow.Ticks % 1000);
        await viewer.SendInputAsync(new InputEvent { EventType = InputEventType.MouseMove, X = x, Y = 2000 }, _cts.Token);
        await Eventually(() => input.Injected.Any(e => e.X == x), "input reaches the host");
    }

    [Fact]
    public async Task WhenBothSidesAllowIt_TheComputersConnectDirectly_WithoutTheRelay()
    {
        var (host, viewer, screen, input) = await ConnectAsync(hostDirect: true, viewerDirect: true);

        Assert.Equal(ConnectionMode.Direct, viewer.ConnectionMode);
        Assert.Equal(ConnectionMode.Direct, host.ConnectionMode);
        Assert.Equal(0, Volatile.Read(ref _relayConnections)); // nothing went through the server's relay
        Assert.False(string.IsNullOrEmpty(viewer.TransportSecurity)); // still inside TLS
        await AssertWorksAsync(viewer, screen, input);
    }

    [Fact]
    public async Task WhenTheOtherSideDoesNotOfferADirectConnection_TheSessionUsesTheRelay()
    {
        var (host, viewer, screen, input) = await ConnectAsync(hostDirect: false, viewerDirect: true);

        Assert.Equal(ConnectionMode.Relay, viewer.ConnectionMode);
        Assert.Equal(ConnectionMode.Relay, host.ConnectionMode);
        Assert.Equal(2, Volatile.Read(ref _relayConnections));
        await AssertWorksAsync(viewer, screen, input);
    }

    [Fact]
    public async Task ADirectSession_ThatLosesItsConnection_ComesBack()
    {
        var (host, viewer, screen, input) = await ConnectAsync(hostDirect: true, viewerDirect: true);
        Assert.Equal(ConnectionMode.Direct, viewer.ConnectionMode);

        viewer.Endpoint.DropConnectionForTest();

        await Eventually(() => viewer.Epoch >= 1 && host.Epoch == viewer.Epoch, "both sides on the new epoch", seconds: 60);
        await AssertWorksAsync(viewer, screen, input);
    }
}
