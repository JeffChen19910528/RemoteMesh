using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client.Tests;

/// <summary>
/// Connections that die in the middle of a session: the relay socket on its own, and a whole computer losing the
/// network and getting it back. Two real clients against the real server; only the network is simulated, per client,
/// so the same code runs here as on a laptop whose Wi-Fi drops.
/// </summary>
public sealed class ReconnectTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-reconnect-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── A network that can be cut ────────────────────────────────────────

    /// <summary>One computer's connection to the world. <see cref="Cut"/> kills what is open and refuses what is new.</summary>
    private sealed class Network
    {
        private TaskCompletionSource _cut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentBag<WebSocket> _sockets = new();
        public volatile bool Offline;

        public Task CutSignal => Volatile.Read(ref _cut).Task;
        public void Track(WebSocket ws) => _sockets.Add(ws);

        public void Cut()
        {
            Offline = true;
            Interlocked.Exchange(ref _cut, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            DropRelaySockets();
        }

        /// <summary>Only the relay connection dies (the server link is fine).</summary>
        public void DropRelaySockets()
        {
            foreach (var ws in _sockets) { try { ws.Abort(); } catch (Exception) { } }
        }

        public void Restore() => Offline = false;
    }

    private sealed class FlakyHandler(HttpMessageHandler inner, Network network) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (network.Offline) throw new HttpRequestException("the network is down");
            var cut = network.CutSignal;
            var send = base.SendAsync(request, ct);
            if (await Task.WhenAny(send, cut) == cut) throw new HttpRequestException("the network went down"); // a long poll in flight
            return await send;
        }
    }

    // ── Fakes for the OS backends ────────────────────────────────────────

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

    // ── Setup ────────────────────────────────────────────────────────────

    private ClientOptions Options(string name, Network network, TimeSpan? reconnectTimeout = null) => new()
    {
        ServerUri = new Uri("http://localhost/"),
        DeviceName = name,
        DataDirectory = Path.Combine(_dir, name),
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveTimeout = TimeSpan.FromSeconds(10),
        ReconnectGrace = TimeSpan.FromMilliseconds(200),
        ReconnectTimeout = reconnectTimeout ?? TimeSpan.FromSeconds(60),
        ConfigureHub = o =>
        {
            o.HttpMessageHandlerFactory = _ => new FlakyHandler(_factory.Server.CreateHandler(), network);
            o.Transports = HttpTransportType.LongPolling;
        },
        RelayConnector = async (uri, token, ct) =>
        {
            if (network.Offline) throw new HttpRequestException("the network is down");
            var ws = _factory.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            var socket = await ws.ConnectAsync(uri, ct);
            network.Track(socket);
            return socket;
        }
    };

    private sealed record Pair(
        RemoteMeshClient HostClient, RemoteMeshClient ViewerClient, HostSession Host, ViewerSession Viewer,
        Network HostNet, Network ViewerNet, Collector Screen, Recorder Input);

    private async Task<Pair> ConnectedPairAsync(TimeSpan? viewerReconnectTimeout = null)
    {
        var input = new Recorder();
        var hostNet = new Network();
        var viewerNet = new Network();
        var services = new HostServices
        {
            CaptureFactory = () => new SyntheticScreenCapture(160, 96, fps: 30),
            Input = input
        };

        var hostClient = new RemoteMeshClient(Options("host", hostNet), services, new Approver());
        _disposables.Add(hostClient);
        await hostClient.StartAsync(_cts.Token);
        var hostSessions = new ConcurrentQueue<HostSession>();
        hostClient.HostSessionStarted += hostSessions.Enqueue;

        var viewerClient = new RemoteMeshClient(Options("viewer", viewerNet, viewerReconnectTimeout));
        _disposables.Add(viewerClient);
        await viewerClient.StartAsync(_cts.Token);

        var screen = new Collector();
        var viewer = await viewerClient.ConnectAsync(hostClient.DeviceId, screen, cancellationToken: _cts.Token);
        _disposables.Add(viewer);
        await Eventually(() => !hostSessions.IsEmpty, "host session started");
        hostSessions.TryPeek(out var host);
        await Eventually(() => screen.Frames >= 5, "first frames");
        return new Pair(hostClient, viewerClient, host!, viewer, hostNet, viewerNet, screen, input);
    }

    private static async Task Eventually(Func<bool> cond, string what, int seconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(20);
        Assert.True(cond(), $"not reached in time: {what}");
    }

    private static InputEvent Mouse(int x) => new() { EventType = InputEventType.MouseMove, X = x, Y = 2000 };

    /// <summary>The screen keeps coming and a click still reaches the host: the session is really working again.</summary>
    private async Task AssertWorksAsync(Pair p, string when)
    {
        var before = p.Screen.Frames;
        await Eventually(() => p.Screen.Frames >= before + 5, $"new frames after {when}");

        var x = 5000 + (int)(DateTime.UtcNow.Ticks % 1000);
        await p.Viewer.SendInputAsync(Mouse(x), _cts.Token);
        await Eventually(() => p.Input.Injected.Any(e => e.X == x), $"input reaches the host after {when}");
    }

    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenOnlyTheRelayConnectionDies_BothSidesRebuildItWithANewKey_AndTheSessionCarriesOn()
    {
        var p = await ConnectedPairAsync();
        var reconnecting = 0;
        p.Viewer.Reconnecting += () => Interlocked.Increment(ref reconnecting);
        Assert.Equal(0, p.Viewer.Epoch);

        p.ViewerNet.DropRelaySockets();

        await Eventually(() => p.Viewer.Epoch >= 1 && p.Host.Epoch == p.Viewer.Epoch, "both sides on the new epoch");
        Assert.True(reconnecting >= 1);
        Assert.False(p.Viewer.Completion.IsCompleted);
        Assert.False(p.Host.Completion.IsCompleted);
        Assert.False(p.Viewer.IsReconnecting);
        Assert.False(p.Host.IsReconnecting);
        await AssertWorksAsync(p, "the relay drop");
    }

    [Fact]
    public async Task ADropOnTheControlledSide_IsRecoveredJustTheSame()
    {
        var p = await ConnectedPairAsync();

        p.HostNet.DropRelaySockets();

        await Eventually(() => p.Host.Epoch >= 1 && p.Viewer.Epoch == p.Host.Epoch, "both sides on the new epoch");
        await AssertWorksAsync(p, "the relay drop on the host");
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("host")]
    public async Task AComputerThatLosesItsNetworkAndGetsItBack_RejoinsItsSession(string whoLosesTheNetwork)
    {
        var p = await ConnectedPairAsync();
        var network = whoLosesTheNetwork == "viewer" ? p.ViewerNet : p.HostNet;
        var lostSide = whoLosesTheNetwork == "viewer" ? (SessionBase)p.Viewer : p.Host;

        network.Cut();
        await Eventually(() => lostSide.IsReconnecting, "the dropped side notices");
        var frames = p.Screen.Frames;
        await Task.Delay(1500);
        Assert.False(p.Viewer.Completion.IsCompleted); // an outage is not the end of the session
        Assert.False(p.Host.Completion.IsCompleted);

        network.Restore();

        await Eventually(() => !p.Viewer.IsReconnecting && !p.Host.IsReconnecting && p.Viewer.Epoch >= 1 && p.Viewer.Epoch == p.Host.Epoch,
            "both sides reconnected on the same epoch", seconds: 60);
        await AssertWorksAsync(p, "the outage");
        Assert.True(p.Screen.Frames > frames);
    }

    [Fact]
    public async Task TheSessionSurvivesSeveralDropsInARow()
    {
        var p = await ConnectedPairAsync();

        for (var round = 1; round <= 3; round++)
        {
            p.ViewerNet.DropRelaySockets();
            await Eventually(() => p.Viewer.Epoch >= round && p.Host.Epoch == p.Viewer.Epoch, $"epoch {round} on both sides");
            await AssertWorksAsync(p, $"drop {round}");
        }

        Assert.True(p.Viewer.Epoch >= 3); // every drop got a key of its own
    }

    [Fact]
    public async Task AComputerThatNeverGetsItsNetworkBack_EndsTheSession_InsteadOfWaitingForever()
    {
        var p = await ConnectedPairAsync(viewerReconnectTimeout: TimeSpan.FromSeconds(4));
        string? reason = null;
        p.Viewer.Ended += r => reason = r;

        p.ViewerNet.Cut(); // and it stays down

        var ended = await Task.WhenAny(p.Viewer.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(p.Viewer.Completion, ended);
        Assert.Equal("could not reconnect", await p.Viewer.Completion);
        await Eventually(() => reason is not null, "the Ended event");
        Assert.Equal("could not reconnect", reason);
    }

    [Fact]
    public async Task ASessionTheOtherSideEndedWhileWeWereDown_IsEndedWhenWeComeBack()
    {
        var p = await ConnectedPairAsync();
        p.ViewerNet.Cut();
        await Eventually(() => p.Viewer.IsReconnecting, "the viewer notices");

        await p.Host.DisposeAsync(); // the controlled side closes the session while the viewer is away
        p.ViewerNet.Restore();

        var ended = await Task.WhenAny(p.Viewer.Completion, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(p.Viewer.Completion, ended); // it does not keep "reconnecting" to a session that no longer exists
    }
}
