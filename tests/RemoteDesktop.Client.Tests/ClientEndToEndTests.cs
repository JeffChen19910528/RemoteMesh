using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.Input;
using RemoteDesktop.Platform.Windows;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client.Tests;

/// <summary>
/// Two real <see cref="RemoteMeshClient"/>s against the real server: registration, approval, relay, end-to-end
/// encryption and every channel, with nothing wired by hand. Only the network (in-process) and the OS backends
/// (screen, input injection, clipboard) are simulated.
/// </summary>
public sealed class ClientEndToEndTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-client-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Fakes for the OS backends ────────────────────────────────────────

    private sealed class Recorder : IInputController
    {
        public ConcurrentQueue<InputEvent> Injected { get; } = new();
        public Task InjectAsync(InputEvent e, CancellationToken ct = default) { Injected.Enqueue(e); return Task.CompletedTask; }
    }

    private sealed class Collector : IFrameRenderer
    {
        public ConcurrentQueue<(int W, int H)> Frames { get; } = new();
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
        {
            Frames.Enqueue((frame.Width, frame.Height));
            return Task.CompletedTask;
        }
    }

    private sealed class Approver(Func<ApprovalRequest, ApprovalDecision> decide) : IApprovalHandler
    {
        public ConcurrentQueue<ApprovalRequest> Asked { get; } = new();
        public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(decide(request));
        }
    }

    // ── Setup ────────────────────────────────────────────────────────────

    private ClientOptions Options(string name, IJpegCodec? jpeg = null) => new()
    {
        JpegCodec = jpeg,
        ServerUri = new Uri("http://localhost/"),
        DeviceName = name,
        DataDirectory = Path.Combine(_dir, name),
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveTimeout = TimeSpan.FromSeconds(10),
        ConfigureHub = o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        },
        RelayConnector = async (uri, token, ct) =>
        {
            var ws = _factory.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await ws.ConnectAsync(uri, ct);
        }
    };

    private async Task<RemoteMeshClient> StartClientAsync(string name, HostServices? host = null, IApprovalHandler? approval = null, IJpegCodec? jpeg = null)
    {
        var client = new RemoteMeshClient(Options(name, jpeg), host, approval);
        _disposables.Add(client);
        await client.StartAsync(_cts.Token);
        return client;
    }

    private HostServices Host(Recorder input, InMemoryClipboardManager clipboard, string root) => new()
    {
        CaptureFactory = () => new SyntheticScreenCapture(160, 96, fps: 30),
        Input = input,
        Clipboard = clipboard,
        FileRoot = root
    };

    private static async Task Eventually(Func<bool> cond, string what, int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond(), $"not reached in time: {what}");
    }

    private static InputEvent Mouse() => new() { EventType = InputEventType.MouseMove, X = 1000, Y = 2000 };
    private static InputEvent Key() => new() { EventType = InputEventType.KeyDown, KeyCode = 65 };

    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApprovedConnection_ServesScreen_Input_Clipboard_AndFiles_WithinTheGrantedRights()
    {
        var input = new Recorder();
        var clipboard = new InMemoryClipboardManager { Policy = ClipboardPolicy.ReadOnly };
        var root = Path.Combine(_dir, "host-files");
        Directory.CreateDirectory(root);
        var granted = Permission.ViewScreen | Permission.ControlMouse | Permission.ClipboardRead | Permission.FileUpload;
        var approver = new Approver(_ => new ApprovalDecision(true, granted));

        var host = await StartClientAsync("host", Host(input, clipboard, root), approver);
        var hostSessions = new ConcurrentQueue<HostSession>();
        host.HostSessionStarted += hostSessions.Enqueue;
        var viewerClient = await StartClientAsync("viewer");

        var renderer = new Collector();
        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, renderer, cancellationToken: _cts.Token);

        // The host user was asked, and the prompt named who is asking.
        Assert.Equal(viewerClient.DeviceId, Assert.Single(approver.Asked).RequesterDeviceId);
        Assert.Equal(granted, viewer.Granted);
        await Eventually(() => !hostSessions.IsEmpty, "host session started");

        // Screen: frames arrive at the host's resolution.
        await Eventually(() => renderer.Frames.Count >= 5, "5 frames rendered");
        Assert.All(renderer.Frames, f => Assert.Equal((160, 96), f));

        // Input: the mouse is allowed, the keyboard is not (never granted).
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await viewer.SendInputAsync(Key(), _cts.Token);
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Eventually(() => input.Injected.Count >= 2, "mouse events injected");
        await Task.Delay(200);
        Assert.Equal(2, input.Injected.Count);
        Assert.DoesNotContain(input.Injected, e => e.EventType == InputEventType.KeyDown);

        // Clipboard: readable (granted + host opted in), not writable (not granted).
        await clipboard.SetTextAsync("host text");
        var read = await viewer.Clipboard.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5), _cts.Token);
        Assert.True(read.Ok);
        Assert.Equal("host text", System.Text.Encoding.UTF8.GetString(read.Data!));
        Assert.NotEqual(ClipboardResult.Ok, await viewer.Clipboard.WriteTextAsync("x", TimeSpan.FromSeconds(5), _cts.Token));

        // Files: upload allowed, download not granted.
        var local = Path.Combine(_dir, "up.bin");
        var bytes = new byte[300_000];
        new Random(5).NextBytes(bytes);
        await File.WriteAllBytesAsync(local, bytes);
        Assert.True((await viewer.Files.UploadAsync(local, "up.bin", ct: _cts.Token)).Ok);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "up.bin")));
        Assert.False((await viewer.Files.DownloadAsync("up.bin", Path.Combine(_dir, "down.bin"), ct: _cts.Token)).Ok);

        // Liveness is measured through the relay on both sides.
        await Eventually(() => viewer.Rtt is not null && hostSessions.Single().Rtt is not null, "round trip measured");
    }

    [Fact]
    public async Task ClosingTheViewer_EndsTheHostSession_AndViceVersa()
    {
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.ViewOnly));
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        var hostSessions = new ConcurrentQueue<HostSession>();
        host.HostSessionStarted += hostSessions.Enqueue;
        var viewerClient = await StartClientAsync("viewer");

        var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Collector(), cancellationToken: _cts.Token);
        await Eventually(() => !hostSessions.IsEmpty, "host session started");
        var hostSession = hostSessions.Single();

        await viewer.DisposeAsync();
        var reason = await hostSession.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(string.IsNullOrEmpty(reason));

        // The other direction: the host ends a fresh session and the viewer finds out.
        var second = await viewerClient.ConnectAsync(host.DeviceId, new Collector(), cancellationToken: _cts.Token);
        await Eventually(() => hostSessions.Count == 2, "second host session");
        await hostSessions.Last().DisposeAsync();
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task DeclinedRequest_IsRefused()
    {
        var approver = new Approver(_ => ApprovalDecision.Deny);
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        var viewerClient = await StartClientAsync("viewer");

        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Collector(), approvalTimeout: TimeSpan.FromSeconds(10), cancellationToken: _cts.Token));
        Assert.Single(approver.Asked);
    }

    [Fact]
    public async Task NoApprovalHandler_MeansNobodyGetsIn()
    {
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approval: null);
        var viewerClient = await StartClientAsync("viewer");

        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Collector(), approvalTimeout: TimeSpan.FromSeconds(10), cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task AcceptingWithNoRights_IsTreatedAsADecline()
    {
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.None));
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        var viewerClient = await StartClientAsync("viewer");

        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Collector(), approvalTimeout: TimeSpan.FromSeconds(10), cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task OneTimeCode_ConnectsWithoutAPrompt_ButOnlyToViewTheScreen()
    {
        var input = new Recorder();
        var approver = new Approver(_ => ApprovalDecision.Deny); // would refuse — the code path must not ask
        var host = await StartClientAsync("host", Host(input, new InMemoryClipboardManager(), _dir), approver);
        var viewerClient = await StartClientAsync("viewer");

        var code = await host.GenerateOtpAsync(_cts.Token);
        var renderer = new Collector();
        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, renderer, otp: code, cancellationToken: _cts.Token);

        Assert.Empty(approver.Asked);
        Assert.Equal(Permission.ViewOnly, viewer.Granted);
        await Eventually(() => renderer.Frames.Count >= 3, "frames rendered");

        // View-only: input is refused by the host.
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Task.Delay(300);
        Assert.Empty(input.Injected);

        // A code is single use.
        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Collector(), otp: code, cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task WrongOneTimeCode_IsRefused()
    {
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir));
        var viewerClient = await StartClientAsync("viewer");
        await host.GenerateOtpAsync(_cts.Token);

        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Collector(), otp: "000000", cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task HostCanTakeRightsBackWhileConnected()
    {
        var input = new Recorder();
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.ViewScreen | Permission.ControlMouse));
        var host = await StartClientAsync("host", Host(input, new InMemoryClipboardManager(), _dir), approver);
        var hostSessions = new ConcurrentQueue<HostSession>();
        host.HostSessionStarted += hostSessions.Enqueue;
        var viewerClient = await StartClientAsync("viewer");
        await using var viewer = await viewerClient.ConnectAsync(host.DeviceId, new Collector(), cancellationToken: _cts.Token);
        await Eventually(() => !hostSessions.IsEmpty, "host session started");

        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Eventually(() => input.Injected.Count == 1, "first move injected");

        hostSessions.Single().Revoke(Permission.ControlMouse);
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Task.Delay(300);
        Assert.Single(input.Injected); // the second move was refused: revocation is immediate
    }

    [Fact]
    public async Task ConnectingToAnUnknownDevice_IsRefused()
    {
        var viewerClient = await StartClientAsync("viewer");
        await Assert.ThrowsAsync<ConnectionRefusedException>(() =>
            viewerClient.ConnectAsync(DeviceId.New(), new Collector(), cancellationToken: _cts.Token));
    }


    private sealed class WaitingApprover : IApprovalHandler
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Withdrawn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
        {
            Asked.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Withdrawn.TrySetResult(); throw; }
            return ApprovalDecision.Deny;
        }
    }

    [Fact]
    public async Task ApprovalPrompt_IsWithdrawn_WhenTheRequesterDisappears()
    {
        var approver = new WaitingApprover();
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        var viewerClient = await StartClientAsync("viewer");

        // The viewer asks, and then goes away (closes the app) without waiting for the answer.
        var attempt = viewerClient.ConnectAsync(host.DeviceId, new Collector(), approvalTimeout: TimeSpan.FromSeconds(30), cancellationToken: _cts.Token);
        await approver.Asked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await viewerClient.DisposeAsync();

        // The person at the host must not be left answering a question about somebody who left.
        await approver.Withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await attempt; } catch (Exception) { } // the abandoned attempt fails; that is expected
    }


    /// <summary>The first request is allowed at once (with the mouse); later ones wait until the test releases them.</summary>
    private sealed class GatedApprover : IApprovalHandler
    {
        private int _count;
        public TaskCompletionSource SecondAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) == 1)
                return new ApprovalDecision(true, Permission.ViewScreen | Permission.ControlMouse);
            SecondAsked.TrySetResult();
            await ReleaseSecond.Task.WaitAsync(ct);
            return ApprovalDecision.Deny;
        }
    }

    [Fact]
    public async Task WhileAnApprovalPromptIsOpen_ConnectedViewersCannotInject_SoTheyCannotClickAllowThemselves()
    {
        var input = new Recorder();
        var approver = new GatedApprover();
        var host = await StartClientAsync("host", Host(input, new InMemoryClipboardManager(), _dir), approver);
        var first = await StartClientAsync("first");
        var second = await StartClientAsync("second");

        await using var viewer = await first.ConnectAsync(host.DeviceId, new Collector(), cancellationToken: _cts.Token);
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Eventually(() => input.Injected.Count == 1, "input works normally");

        // Somebody else asks to connect: the user is now looking at a prompt.
        var pending = second.ConnectAsync(host.DeviceId, new Collector(), approvalTimeout: TimeSpan.FromSeconds(20), cancellationToken: _cts.Token);
        await approver.SecondAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Task.Delay(400);
        Assert.Single(input.Injected); // held: the first viewer cannot reach the prompt

        // The prompt is answered (declined): input flows again.
        approver.ReleaseSecond.SetResult();
        await Assert.ThrowsAsync<ConnectionRefusedException>(() => pending);
        await Task.Delay(200);
        await viewer.SendInputAsync(Mouse(), _cts.Token);
        await Eventually(() => input.Injected.Count == 2, "input resumes after the prompt is closed");
    }


    /// <summary>Keeps pixels exactly (a real JPEG header followed by raw pixels): isolates the negotiation from image quality.</summary>
    private sealed class PassThroughJpeg : IJpegCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, int quality)
        {
            var d = new byte[15 + width * height * 4];
            d[0] = 0xFF; d[1] = 0xD8; d[2] = 0xFF; d[3] = 0xC0; d[5] = 11; d[6] = 8;
            d[7] = (byte)(height >> 8); d[8] = (byte)height; d[9] = (byte)(width >> 8); d[10] = (byte)width;
            for (var y = 0; y < height; y++) pixels.Slice(y * stride, width * 4).CopyTo(d.AsSpan(15 + y * width * 4));
            return d;
        }

        public void Decode(ReadOnlySpan<byte> jpeg, int width, int height, Span<byte> destination, int destinationStride)
        {
            for (var y = 0; y < height; y++)
                jpeg.Slice(15 + y * width * 4, width * 4).CopyTo(destination.Slice(y * destinationStride, width * 4));
        }
    }

    private async Task<(CodecType? Codec, Collector Frames)> StreamWithAsync(IJpegCodec? hostCodec, IJpegCodec? viewerCodec)
    {
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.ViewOnly));
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver, hostCodec);
        var sessions = new ConcurrentQueue<HostSession>();
        host.HostSessionStarted += sessions.Enqueue;
        var viewerClient = await StartClientAsync("viewer", jpeg: viewerCodec);

        var renderer = new Collector();
        var viewer = await viewerClient.ConnectAsync(host.DeviceId, renderer, cancellationToken: _cts.Token);
        _disposables.Add(viewer);
        await Eventually(() => renderer.Frames.Count >= 5, "frames rendered");
        Assert.All(renderer.Frames, f => Assert.Equal((160, 96), f));
        await Eventually(() => sessions.Any(), "host session started");
        return (sessions.Single().VideoCodec, renderer);
    }

    [Fact]
    public async Task WhenBothSidesCanDoJpeg_TheLossyCodecIsUsed()
    {
        var (codec, _) = await StreamWithAsync(new PassThroughJpeg(), new PassThroughJpeg());
        Assert.Equal(CodecType.TileDeltaJpeg, codec);
    }

    [Fact]
    public async Task WhenTheViewerCannotDecodeJpeg_TheHostStaysLossless()
    {
        var (codec, _) = await StreamWithAsync(new PassThroughJpeg(), viewerCodec: null);
        Assert.Equal(CodecType.TileDeltaReference, codec);
    }

    [Fact]
    public async Task WhenTheHostCannotEncodeJpeg_TheViewerStillGetsAPicture()
    {
        var (codec, _) = await StreamWithAsync(hostCodec: null, new PassThroughJpeg());
        Assert.Equal(CodecType.TileDeltaReference, codec);
    }

    [Fact]
    public async Task WhenNeitherHasJpeg_TheLosslessCodecWorksAsBefore()
    {
        var (codec, _) = await StreamWithAsync(null, null);
        Assert.Equal(CodecType.TileDeltaReference, codec);
    }

    [Fact]
    public async Task TheRealWindowsJpegCodec_CarriesAPictureEndToEnd()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (codec, _) = await StreamWithAsync(new GdiJpegCodec(), new GdiJpegCodec());
        Assert.Equal(CodecType.TileDeltaJpeg, codec);
    }


    // ── Short IDs ────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryClient_GetsANineDigitShortId()
    {
        var client = await StartClientAsync("pc");
        Assert.Matches("^[1-9][0-9]{8}$", client.ShortId);
    }

    [Fact]
    public async Task AViewer_CanConnectWithTheShortId_TypedWithSpaces()
    {
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.ViewOnly));
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        var viewerClient = await StartClientAsync("viewer");

        var renderer = new Collector();
        await using var viewer = await viewerClient.ConnectAsync(RemoteDesktop.Protocol.ShortId.Format(host.ShortId), renderer, cancellationToken: _cts.Token);

        Assert.Equal(viewerClient.DeviceId, Assert.Single(approver.Asked).RequesterDeviceId);
        await Eventually(() => renderer.Frames.Count >= 3, "frames rendered");
    }

    [Fact]
    public async Task TheOneTimeCode_WorksWithTheShortId_Too()
    {
        var host = await StartClientAsync("host", Host(new Recorder(), new InMemoryClipboardManager(), _dir));
        var viewerClient = await StartClientAsync("viewer");
        var code = await host.GenerateOtpAsync(_cts.Token);

        await using var viewer = await viewerClient.ConnectAsync(host.ShortId, new Collector(), otp: code, cancellationToken: _cts.Token);

        Assert.Equal(Permission.ViewOnly, viewer.Granted);
    }

    [Fact]
    public async Task ADevice_KeepsItsShortId_AcrossRestarts()
    {
        var first = await StartClientAsync("same");
        var number = first.ShortId;
        await first.DisposeAsync();

        var again = new RemoteMeshClient(Options("same"));
        _disposables.Add(again);
        await again.StartAsync(_cts.Token);

        Assert.Equal(number, again.ShortId); // it asked for its old number and nobody else had taken it
    }

    [Fact]
    public async Task IfTheRememberedNumberBelongsToSomebodyElse_TheDeviceGetsANewOne_AndStillWorks()
    {
        var owner = await StartClientAsync("owner");

        // A device whose data folder remembers a number that is, meanwhile, held by another device.
        var dir = Path.Combine(_dir, "latecomer");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "short.id"), owner.ShortId);

        var latecomer = await StartClientAsync("latecomer");

        Assert.NotEqual(owner.ShortId, latecomer.ShortId);        // the number was not taken from its owner
        Assert.Matches("^[1-9][0-9]{8}$", latecomer.ShortId);
        // and the number it was given is now what it remembers
        Assert.Equal(latecomer.ShortId, (await File.ReadAllTextAsync(Path.Combine(dir, "short.id"))).Trim());

        // The owner is still reachable by its number.
        var approver = new Approver(_ => new ApprovalDecision(true, Permission.ViewOnly));
        var host = await StartClientAsync("host2", Host(new Recorder(), new InMemoryClipboardManager(), _dir), approver);
        await using var viewer = await latecomer.ConnectAsync(host.ShortId, new Collector(), cancellationToken: _cts.Token);
        Assert.Equal(Permission.ViewOnly, viewer.Granted);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not an id")]
    [InlineData("")]
    public async Task ConnectingToSomethingThatIsNotAnAddress_IsACallerMistake(string address)
    {
        var client = await StartClientAsync("pc");
        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(address, new Collector(), cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task ANumberNobodyHas_IsRefused_LikeAnyUnknownDevice()
    {
        var client = await StartClientAsync("pc");
        await Assert.ThrowsAsync<ConnectionRefusedException>(() => client.ConnectAsync("123456789", new Collector(), cancellationToken: _cts.Token));
    }

    [Fact]
    public void ConnectionAddress_AcceptsBothForms_AndNothingElse()
    {
        Assert.True(ConnectionAddress.TryNormalize("482 913 057", out var a));
        Assert.Equal("482913057", a);

        var id = DeviceId.New();
        Assert.True(ConnectionAddress.TryNormalize("  " + id + "  ", out var b));
        Assert.Equal(id.ToString(), b);

        Assert.False(ConnectionAddress.TryNormalize("hello", out _));
        Assert.False(ConnectionAddress.TryNormalize(null, out _));
    }

    [Fact]
    public async Task DeviceIdentity_IsStableAcrossRestarts()
    {
        var first = await StartClientAsync("same");
        var id = first.DeviceId;
        await first.DisposeAsync();

        var again = new RemoteMeshClient(Options("same"));
        _disposables.Add(again);
        await again.StartAsync(_cts.Token);
        Assert.Equal(id, again.DeviceId); // same key, same ID: the server's pinned key still matches
    }
}
