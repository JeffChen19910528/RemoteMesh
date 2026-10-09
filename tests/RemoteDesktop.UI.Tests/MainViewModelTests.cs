using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.UI.Services;
using RemoteDesktop.UI.ViewModels;

namespace RemoteDesktop.UI.Tests;

/// <summary>
/// The window logic with the real client stack and the real server, only the windows themselves faked:
/// what the user would see and click is driven through <see cref="MainViewModel"/>.
/// </summary>
public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<MainViewModel> _models = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-ui-vm-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var m in _models) { try { await m.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeShell : IShell
    {
        public Func<ApprovalRequest, ApprovalDecision> Decide { get; set; } = _ => ApprovalDecision.Deny;
        public Func<AppSettings, string?, AppSettings?> Edit { get; set; } = (_, _) => null;
        public ConcurrentQueue<ApprovalRequest> Asked { get; } = new();
        public ConcurrentQueue<ViewerSession> Viewers { get; } = new();
        public ConcurrentQueue<string> Copied { get; } = new();
        public ConcurrentQueue<string?> SettingsShownWith { get; } = new();

        public Task<ApprovalDecision> AskApprovalAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(Decide(request));
        }

        public void ShowViewer(ViewerSession session, IViewerRenderer renderer) => Viewers.Enqueue(session);

        public Task<AppSettings?> EditSettingsAsync(AppSettings current, string? problem)
        {
            SettingsShownWith.Enqueue(problem);
            return Task.FromResult(Edit(current, problem));
        }

        public bool ConfirmAnswer { get; set; } = true;
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(ConfirmAnswer);

        public void CopyToClipboard(string text) => Copied.Enqueue(text);
    }

    private (MainViewModel Model, FakeShell Shell) NewModel(string name, bool configured = true)
    {
        var dir = Path.Combine(_dir, name);
        var store = new SettingsStore(dir);
        if (configured) store.Save(new AppSettings { ServerUrl = "http://localhost", DeviceName = name });

        var shell = new FakeShell();
        var model = new MainViewModel(
            shell, store, () => new WpfFrameRenderer(Dispatcher.CurrentDispatcher),
            clientFactory: (o, host, approval) => new RemoteMeshClient(new ClientOptions
            {
                ServerUri = o.ServerUri,
                DeviceName = o.DeviceName,
                DataDirectory = o.DataDirectory,
                KeepAliveInterval = TimeSpan.FromMilliseconds(100),
                ConfigureHub = h =>
                {
                    h.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    h.Transports = HttpTransportType.LongPolling;
                },
                RelayConnector = async (uri, token, ct) =>
                {
                    var ws = _factory.Server.CreateWebSocketClient();
                    ws.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
                    return await ws.ConnectAsync(uri, ct);
                }
            }, host, approval),
            hostServices: _ => new HostServices
            {
                CaptureFactory = () => new SyntheticScreenCapture(160, 96, fps: 20), // never the real screen in tests
                Input = new Recorder()
            });
        _models.Add(model);
        return (model, shell);
    }

    private sealed class Recorder : IInputController
    {
        public Task InjectAsync(InputEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task Eventually(Func<bool> cond, string what, int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond(), $"not reached in time: {what}");
    }

    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_WithGoodSettings_GoesOnline_ShowsTheIdAndAFreshCode()
    {
        var (vm, _) = NewModel("pc");
        await vm.StartAsync();

        Assert.True(vm.IsOnline);
        Assert.Matches("^[1-9][0-9]{2} [0-9]{3} [0-9]{3}$", vm.MyId); // the number people are told: 482 913 057
        Assert.True(DeviceId.TryParse(vm.MyDeviceId, out _));          // the full ID is still there, for support
        Assert.Matches("^[0-9]{6}$", vm.Otp);
        Assert.Contains("已連線", vm.Status);
    }

    [Fact]
    public async Task Start_WithoutSettings_AsksForThem_AndStaysIdleIfDeclined()
    {
        var (vm, shell) = NewModel("pc", configured: false);
        await vm.StartAsync();

        Assert.Single(shell.SettingsShownWith);
        Assert.Null(shell.SettingsShownWith.Single()); // first run: no scolding, just the form
        Assert.False(vm.IsOnline);
        Assert.Contains("尚未設定", vm.Status);
    }

    [Fact]
    public async Task Start_WithBrokenSettings_ShowsWhatIsWrong()
    {
        var dir = Path.Combine(_dir, "broken");
        new SettingsStore(dir).Save(new AppSettings { ServerUrl = "ftp://nope", DeviceName = "x" });
        var shell = new FakeShell();
        var vm = new MainViewModel(shell, new SettingsStore(dir), () => new WpfFrameRenderer(Dispatcher.CurrentDispatcher));
        _models.Add(vm);

        await vm.StartAsync();

        Assert.NotNull(shell.SettingsShownWith.Single()); // the user is told why it will not start
        Assert.False(vm.IsOnline);
    }

    [Fact]
    public async Task Connect_WithAMalformedId_SaysSo_AndDoesNotTryToConnect()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();

        vm.RemoteId = "12345";
        await vm.ConnectAsync();

        Assert.False(string.IsNullOrEmpty(vm.Error));
        Assert.Empty(shell.Viewers);
    }

    [Fact]
    public async Task Connect_ToYourself_IsRefusedWithAnExplanation()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();

        vm.RemoteId = vm.MyId;
        await vm.ConnectAsync();

        Assert.Contains("自己", vm.Error);

        vm.RemoteId = vm.MyDeviceId; // the full ID of oneself is refused just the same
        await vm.ConnectAsync();
        Assert.Contains("自己", vm.Error);
        Assert.Empty(shell.Viewers);
    }

    [Fact]
    public async Task Connect_ToAnUnknownDevice_ShowsAFriendlyError_AndStaysUsable()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();

        vm.RemoteId = DeviceId.New().ToString();
        await vm.ConnectAsync();

        Assert.False(string.IsNullOrEmpty(vm.Error));
        Assert.DoesNotContain("Exception", vm.Error);
        Assert.Empty(shell.Viewers);
        Assert.True(vm.ConnectCommand.CanExecute(null)); // not stuck "connecting"
    }

    [Fact]
    public async Task BothScreensShowTheSameSecurityCode_AndVerifyingItIsRemembered()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => new ApprovalDecision(true, Permission.ViewScreen);
        await host.StartAsync();
        await viewer.StartAsync();

        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();
        var session = Assert.Single(viewerShell.Viewers);
        await Eventually(() => host.BeingControlled.Count == 1, "host lists the viewer");
        var item = host.BeingControlled.Single();

        Assert.Equal(session.SecurityCode, item.SecurityCode);           // what the two people read to each other
        Assert.Contains("第一次", item.Trust);
        Assert.True(item.CanVerify);
        Assert.True(host.VerifyCommand.CanExecute(item));

        host.VerifyCommand.Execute(item);                                  // the host user compared and clicked
        Assert.Contains("已核對", item.Trust);
        Assert.False(item.CanVerify);
        Assert.False(host.VerifyCommand.CanExecute(item));
        Assert.False(session.PeerVerified);                                // the viewer side decides for itself
    }

    [Fact]
    public async Task BlockingADevice_AsksFirst_EndsItsSession_ListsItAndCanBeUndone()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => new ApprovalDecision(true, Permission.ViewScreen);
        await host.StartAsync();
        await viewer.StartAsync();
        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();
        var session = Assert.Single(viewerShell.Viewers);
        await Eventually(() => host.BeingControlled.Count == 1, "host lists the viewer");
        var item = host.BeingControlled.Single();
        Assert.False(host.HasBlockedPeers);

        hostShell.ConfirmAnswer = false;
        await host.BlockAsync(item);                                    // "no": nothing happens
        Assert.False(host.HasBlockedPeers);
        Assert.False(session.Completion.IsCompleted);

        hostShell.ConfirmAnswer = true;
        await host.BlockAsync(item);
        await Eventually(() => session.Completion.IsCompleted, "the blocked device's session ends");
        await Eventually(() => host.BeingControlled.Count == 0, "and leaves the list");
        Assert.True(host.HasBlockedPeers);
        var blocked = Assert.Single(host.BlockedPeers);
        Assert.Contains(viewer.MyId.Replace(" ", ""), blocked.Who.Replace(" ", ""));   // known by the number people know it by

        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();
        Assert.Single(hostShell.Asked);                                  // the second try never reached a person
        Assert.Single(viewerShell.Viewers);                              // and no new viewer opened

        await host.UnblockAsync(blocked);
        Assert.False(host.HasBlockedPeers);
        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();
        Assert.Equal(2, viewerShell.Viewers.Count);                      // asked about again, like anyone else
    }

    [Fact]
    public async Task ChangingTheKey_AsksFirst_ThenDoesItAndKeepsWorking()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();
        var registry = _factory.Services.GetRequiredService<RemoteDesktop.Server.Core.IDeviceRegistry>();
        var id = DeviceId.Parse(vm.MyDeviceId);
        var before = await registry.GetPublicKeyAsync(id);

        shell.ConfirmAnswer = false;
        await vm.RotateKeyAsync();                                  // "no": nothing happens
        Assert.Equal(before, await registry.GetPublicKeyAsync(id));

        shell.ConfirmAnswer = true;
        await vm.RotateKeyAsync();
        Assert.NotEqual(before, await registry.GetPublicKeyAsync(id));
        Assert.Contains("已更換", vm.Status);
        Assert.True(string.IsNullOrEmpty(vm.Error));
        Assert.True(vm.IsOnline);
    }

    [Fact]
    public async Task AKeyThatChangedSinceTheLastTime_IsReportedPlainly_AndNothingOpens()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => new ApprovalDecision(true, Permission.ViewScreen);
        await host.StartAsync();
        await viewer.StartAsync();
        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();
        await viewerShell.Viewers.Single().DisposeAsync();

        var registry = _factory.Services.GetRequiredService<RemoteDesktop.Server.Core.IDeviceRegistry>();
        var hostId = DeviceId.Parse(host.MyDeviceId);
        await registry.RegisterAsync((await registry.FindAsync(hostId))!, RemoteDesktop.Security.DeviceKeyGenerator.Generate().PublicKey);

        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();

        Assert.Contains("安全金鑰", viewer.Error);
        Assert.Contains("中止", viewer.Error);
        Assert.Single(viewerShell.Viewers);                        // only the first, legitimate one
        Assert.True(viewer.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectingWithAnApproval_ShowsTheViewer_AndTheHostSeesWhoIsInAndWhatTheyMayDo()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => new ApprovalDecision(true, Permission.ViewScreen | Permission.ControlMouse);
        await host.StartAsync();
        await viewer.StartAsync();

        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();

        // The host user was asked, and the question named the viewer's device.
        var asked = Assert.Single(hostShell.Asked);
        Assert.Equal(DeviceId.Parse(viewer.MyDeviceId), asked.RequesterDeviceId);
        // The host sees the very number the viewer sees for itself, so it can tell who is asking.
        Assert.Equal(viewer.MyId.Replace(" ", ""), asked.RequesterShortId);
        var session = Assert.Single(viewerShell.Viewers);
        Assert.Equal(Permission.ViewScreen | Permission.ControlMouse, session.Granted);

        await Eventually(() => host.BeingControlled.Count == 1, "host lists the viewer");
        var item = host.BeingControlled.Single();
        Assert.Contains(viewer.MyId, item.Who); // the list shows the connected person's number, not a GUID
        Assert.Contains("控制滑鼠", item.Rights);
        Assert.DoesNotContain("控制鍵盤", item.Rights);

        // The host can cut the connection from the list.
        host.DisconnectCommand.Execute(item);
        await Eventually(() => host.BeingControlled.Count == 0, "viewer removed from the list");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ConnectingWithTheOneTimeCode_NeedsNoApproval_AndOnlyViews()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        await host.StartAsync();
        await viewer.StartAsync();

        viewer.RemoteId = host.MyId;
        viewer.RemoteCode = host.Otp;
        await viewer.ConnectAsync();

        Assert.Empty(hostShell.Asked);
        Assert.Equal(Permission.ViewOnly, Assert.Single(viewerShell.Viewers).Granted);
        Assert.Equal(string.Empty, viewer.RemoteCode); // a used code is not left lying in the box
        await Eventually(() => host.BeingControlled.Count == 1, "host lists the viewer");
        Assert.Contains("一次性密碼", host.BeingControlled.Single().Who); // the host cannot tell who it is, and says so
    }


    [Theory]
    [InlineData("482913057")]
    [InlineData("482 913 057")]
    [InlineData("482-913-057")]
    public async Task TheShortId_CanBeTypedInAnyCommonWay(string typed)
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => new ApprovalDecision(true, Permission.ViewOnly);
        await host.StartAsync();
        await viewer.StartAsync();

        // Re-type the host's real number in the style under test.
        var digits = host.MyId.Replace(" ", "");
        viewer.RemoteId = typed.Length == 9 ? digits
            : typed.Contains('-') ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}" : host.MyId;
        await viewer.ConnectAsync();

        Assert.Single(viewerShell.Viewers);
        Assert.True(string.IsNullOrEmpty(viewer.Error));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("12345678a")]
    [InlineData("hello")]
    public async Task ANumberThatIsNotAnId_SaysWhatAnIdLooksLike(string typed)
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();

        vm.RemoteId = typed;
        await vm.ConnectAsync();

        Assert.Contains("9 位數字", vm.Error);
        Assert.Empty(shell.Viewers);
    }

    [Fact]
    public async Task CopyId_CopiesTheNumberPeopleWillRead()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();

        vm.CopyIdCommand.Execute(null);

        Assert.Equal(vm.MyId, Assert.Single(shell.Copied)); // "482 913 057", not the long ID
    }

    [Fact]
    public async Task ADeclinedRequest_ShowsAnError_AndNoViewer()
    {
        var (host, hostShell) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        hostShell.Decide = _ => ApprovalDecision.Deny;
        await host.StartAsync();
        await viewer.StartAsync();

        viewer.RemoteId = host.MyId;
        await viewer.ConnectAsync();

        Assert.False(string.IsNullOrEmpty(viewer.Error));
        Assert.Empty(viewerShell.Viewers);
        Assert.Empty(host.BeingControlled);
    }

    [Fact]
    public async Task NewCode_ReplacesTheOldOne_AndTheOldOneStopsWorking()
    {
        var (host, _) = NewModel("host");
        var (viewer, viewerShell) = NewModel("viewer");
        await host.StartAsync();
        await viewer.StartAsync();
        var old = host.Otp;

        await host.NewCodeAsync();
        Assert.NotEqual(old, host.Otp);

        viewer.RemoteId = host.MyId;
        viewer.RemoteCode = old;
        await viewer.ConnectAsync();
        Assert.Empty(viewerShell.Viewers);
        Assert.False(string.IsNullOrEmpty(viewer.Error));
    }

    [Fact]
    public async Task SavingNewSettings_RestartsTheClient_AndKeepsTheSameIdentity()
    {
        var (vm, shell) = NewModel("pc");
        await vm.StartAsync();
        var id = vm.MyDeviceId;
        var shortNumber = vm.MyId;

        shell.Edit = (cur, _) => new AppSettings { ServerUrl = cur.ServerUrl, DeviceName = "renamed", AllowClipboard = true };
        Assert.True(await vm.OpenSettingsAsync(null));

        await Eventually(() => vm.IsOnline && vm.Status.Contains("已連線"), "back online");
        Assert.Equal(id, vm.MyDeviceId); // changing a setting must not make this computer a different device
        Assert.Equal(shortNumber, vm.MyId);   // ...nor change the number people know it by
    }
}
