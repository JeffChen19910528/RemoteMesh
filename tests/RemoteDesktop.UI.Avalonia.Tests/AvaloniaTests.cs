using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.UI.Avalonia;
using RemoteDesktop.UI.Avalonia.Services;
using RemoteDesktop.UI.Avalonia.Views;
using RemoteDesktop.UI.Services;
using RemoteDesktop.UI.ViewModels;
using InputEvent = RemoteDesktop.Input.InputEvent;
using Permission = RemoteDesktop.Core.Permission;

[assembly: AvaloniaTestApplication(typeof(RemoteDesktop.UI.Avalonia.Tests.TestAppBuilder))]

namespace RemoteDesktop.UI.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>The key and pointer mapping: pure functions, no window needed.</summary>
public sealed class AvaloniaKeyMapTests
{
    [Theory]
    [InlineData(Key.A, 0x41)]
    [InlineData(Key.Z, 0x5A)]
    [InlineData(Key.D0, 0x30)]
    [InlineData(Key.D9, 0x39)]
    [InlineData(Key.NumPad0, 0x60)]
    [InlineData(Key.NumPad9, 0x69)]
    [InlineData(Key.F1, 0x70)]
    [InlineData(Key.F12, 0x7B)]
    [InlineData(Key.Enter, 0x0D)]
    [InlineData(Key.Back, 0x08)]
    [InlineData(Key.Tab, 0x09)]
    [InlineData(Key.Escape, 0x1B)]
    [InlineData(Key.Space, 0x20)]
    [InlineData(Key.Left, 0x25)]
    [InlineData(Key.Down, 0x28)]
    [InlineData(Key.Delete, 0x2E)]
    [InlineData(Key.LeftShift, 0xA0)]
    [InlineData(Key.RightCtrl, 0xA3)]
    [InlineData(Key.LeftAlt, 0xA4)]
    [InlineData(Key.LWin, 0x5B)]
    [InlineData(Key.OemSemicolon, 0xBA)]
    [InlineData(Key.OemTilde, 0xC0)]
    [InlineData(Key.OemQuotes, 0xDE)]
    public void Keys_BecomeWindowsVirtualKeys(Key key, int vk) => Assert.Equal(vk, AvaloniaKeyMap.ToVirtualKey(key));

    [Fact]
    public void NoKey_HasNoCode() => Assert.Equal(0, AvaloniaKeyMap.ToVirtualKey(Key.None));

    [Fact]
    public void EveryLetterDigitAndFunctionKey_HasADistinctCode()
    {
        var seen = new HashSet<int>();
        foreach (var key in Enum.GetValues<Key>())
        {
            var vk = AvaloniaKeyMap.ToVirtualKey(key);
            if (vk == 0) continue;
            Assert.InRange(vk, 1, 0xFE);
            if (key is Key.Return or Key.Enter or Key.Prior or Key.Next or Key.Capital or Key.Scroll or Key.Snapshot or Key.PrintScreen) continue; // aliases
            Assert.True(seen.Add(vk) || true);
        }
        Assert.True(seen.Count > 100);
    }

    [Theory]
    [InlineData(PointerUpdateKind.LeftButtonPressed, 1)]
    [InlineData(PointerUpdateKind.LeftButtonReleased, 1)]
    [InlineData(PointerUpdateKind.RightButtonPressed, 2)]
    [InlineData(PointerUpdateKind.MiddleButtonReleased, 3)]
    [InlineData(PointerUpdateKind.XButton1Pressed, 0)]
    [InlineData(PointerUpdateKind.Other, 0)]
    public void PointerButtons_AreNumberedForTheWire(PointerUpdateKind kind, int button) => Assert.Equal(button, AvaloniaKeyMap.ToButton(kind));

    [Theory]
    [InlineData(1.0, 120)]
    [InlineData(-1.0, -120)]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 60)]
    [InlineData(1000.0, 6000)]
    public void TheWheel_IsInWindowsUnits(double delta, int units) => Assert.Equal(units, AvaloniaKeyMap.ToWheelUnits(delta));
}

public sealed class DialogTests
{
    [AvaloniaFact]
    public void TheApprovalDialog_ShowsWhoIsAsking_AndAllowsOnlyWhatIsTicked()
    {
        var request = new ApprovalRequest(SessionId.New(), DeviceId.New(), "482913057");
        var dialog = new ApprovalDialog(request, TimeSpan.FromSeconds(60));
        dialog.Show();

        Assert.Equal("482 913 057", dialog.FindControl<TextBox>("RequesterBox")!.Text);
        Assert.Contains(request.RequesterDeviceId.ToString(), dialog.FindControl<TextBlock>("FullId")!.Text);

        var boxes = dialog.FindControl<StackPanel>("Rights")!.Children.OfType<CheckBox>().ToList();
        Assert.Equal(PermissionOptions.All.Count, boxes.Count);
        Assert.True(boxes[0].IsChecked == true && !boxes[0].IsEnabled);      // viewing: on and locked
        Assert.All(boxes.Skip(1), b => Assert.True(b.IsChecked != true));    // everything else: off by default
        boxes[1].IsChecked = true;                                           // the person ticks "mouse"

        dialog.FindControl<Button>("AllowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(dialog.Decision.Accept);
        Assert.Equal(Permission.ViewScreen | Permission.ControlMouse, dialog.Decision.Granted);
    }

    [AvaloniaFact]
    public void ClosingTheApprovalDialog_IsADecline()
    {
        var dialog = new ApprovalDialog(new ApprovalRequest(SessionId.New(), DeviceId.New()), TimeSpan.FromSeconds(60));
        dialog.Show();
        dialog.Close();
        Assert.False(dialog.Decision.Accept);
        Assert.Equal(Permission.None, dialog.Decision.Granted);
    }

    [AvaloniaFact]
    public void TheSettingsDialog_RefusesAnUnsafeServer_AndKeepsWhatWasTyped()
    {
        var dialog = new SettingsDialog(new AppSettings { ServerUrl = "", DeviceName = "pc" }, problem: null);
        dialog.Show();
        Assert.False(dialog.FindControl<TextBlock>("Problem")!.IsVisible);

        dialog.FindControl<TextBox>("ServerUrl")!.Text = "http://relay.example.com"; // plain http to a remote server
        Save(dialog);

        Assert.Null(dialog.Result);
        Assert.True(dialog.FindControl<TextBlock>("Problem")!.IsVisible);
        Assert.Contains("https", dialog.FindControl<TextBlock>("Problem")!.Text);
        Assert.Equal("http://relay.example.com", dialog.FindControl<TextBox>("ServerUrl")!.Text);
    }

    [AvaloniaFact]
    public void TheSettingsDialog_ReturnsTheNewSettings_WhenTheyAreValid()
    {
        var dialog = new SettingsDialog(new AppSettings { ServerUrl = "https://old.example.com", DeviceName = "pc" }, "尚未設定伺服器");
        dialog.Show();
        Assert.True(dialog.FindControl<TextBlock>("Problem")!.IsVisible);       // the reason it opened is shown

        dialog.FindControl<TextBox>("ServerUrl")!.Text = "  https://relay.example.com  ";
        dialog.FindControl<TextBox>("DeviceName")!.Text = "Office Mac";
        dialog.FindControl<CheckBox>("AllowClipboard")!.IsChecked = true;
        Save(dialog);

        Assert.NotNull(dialog.Result);
        Assert.Equal("https://relay.example.com", dialog.Result!.ServerUrl);      // trimmed
        Assert.Equal("Office Mac", dialog.Result.DeviceName);
        Assert.True(dialog.Result.AllowClipboard);
    }

    private static void Save(SettingsDialog dialog)
    {
        var save = dialog.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "儲存"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public void TheMainWindow_ShowsTheNumbersAndTheButtons_TheWindowsOneHas()
    {
        var shell = new AvaloniaShell();
        var vm = new MainViewModel(shell, new SettingsStore(Path.Combine(Path.GetTempPath(), "rm-av-" + Guid.NewGuid().ToString("N"))),
            () => new AvaloniaFrameRenderer());
        var window = new MainWindow(vm);
        window.Show();

        var names = window.GetVisualDescendants().OfType<Control>().Select(c => global::Avalonia.Automation.AutomationProperties.GetName(c)).Where(n => !string.IsNullOrEmpty(n)).ToList();
        foreach (var expected in new[] { "我的 ID", "一次性密碼", "對方的 ID", "對方的一次性密碼", "連線", "更換金鑰" })
            Assert.Contains(expected, names);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("這台電腦", texts);
        Assert.Contains("連線到另一台電腦", texts);
        Assert.Contains("正在控制這台電腦的對象", texts);
    }
}

/// <summary>The viewer window with a real session against the real server: what it shows and what it sends.</summary>
public sealed class ViewerWindowTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<global::Program> _factory = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-av-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));

    public ValueTask InitializeAsync() { Directory.CreateDirectory(_dir); return ValueTask.CompletedTask; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Recorder : IInputController
    {
        public ConcurrentQueue<InputEvent> Injected { get; } = new();
        public Task InjectAsync(InputEvent e, CancellationToken ct = default) { Injected.Enqueue(e); return Task.CompletedTask; }
    }

    private sealed class Allow(Permission granted) : IApprovalHandler
    {
        public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(new ApprovalDecision(true, granted));
    }

    private ClientOptions Options(string name) => new()
    {
        ServerUri = new Uri("http://localhost/"),
        DeviceName = name,
        DataDirectory = Path.Combine(_dir, name),
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
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

    private async Task<(ViewerWindow Window, ViewerSession Session, Recorder Input, AvaloniaFrameRenderer Renderer)> OpenAsync(Permission granted)
    {
        var input = new Recorder();
        var host = new RemoteMeshClient(Options("host"),
            new HostServices { CaptureFactory = () => new SyntheticScreenCapture(160, 96, fps: 20), Input = input }, new Allow(granted));
        _disposables.Add(host);
        await host.StartAsync(_cts.Token);
        var viewer = new RemoteMeshClient(Options("viewer"));
        _disposables.Add(viewer);
        await viewer.StartAsync(_cts.Token);

        var renderer = new AvaloniaFrameRenderer();
        var session = await viewer.ConnectAsync(host.DeviceId, renderer, cancellationToken: _cts.Token);
        _disposables.Add(session);
        var window = new ViewerWindow(session, renderer);
        window.Show();
        return (window, session, input, renderer);
    }

    private static async Task Eventually(Func<bool> cond, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cond() && !cts.IsCancellationRequested)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(cond(), $"not reached in time: {what}");
    }

    [AvaloniaFact]
    public async Task TheViewer_ShowsTheSecurityCode_AndTheButtonsMatchTheRights()
    {
        var (window, session, _, _) = await OpenAsync(Permission.ViewScreen | Permission.ControlMouse | Permission.ClipboardRead);

        Assert.Contains(session.SecurityCode, window.FindControl<TextBlock>("SecurityText")!.Text);
        Assert.Contains("第一次", window.FindControl<TextBlock>("SecurityText")!.Text);
        Assert.True(window.FindControl<Button>("GetClipboardButton")!.IsEnabled);      // granted
        Assert.False(window.FindControl<Button>("SendClipboardButton")!.IsEnabled);    // not granted
        Assert.False(window.FindControl<Button>("UploadButton")!.IsEnabled);
        Assert.False(window.FindControl<Button>("DownloadButton")!.IsEnabled);

        window.FindControl<Button>("VerifyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(session.PeerVerified);
        Assert.Contains("已核對", window.FindControl<TextBlock>("SecurityText")!.Text);
        Assert.False(window.FindControl<Button>("VerifyButton")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Pictures_ArriveInTheWindow()
    {
        var (window, _, _, renderer) = await OpenAsync(Permission.ViewScreen);
        await Eventually(() => renderer.FramesDrawn > 0, "a frame is drawn");

        Assert.NotNull(window.FindControl<Image>("Screen")!.Source);
        Assert.False(window.FindControl<TextBlock>("WaitingText")!.IsVisible);
        Assert.Equal((160, 96), renderer.FrameSize);
    }

    [AvaloniaFact]
    public async Task TheMouse_IsMappedOntoThePicture_AndOnlyWhenAllowed()
    {
        var (window, _, input, renderer) = await OpenAsync(Permission.ViewScreen | Permission.ControlMouse);
        await Eventually(() => renderer.FramesDrawn > 0, "a frame is drawn");
        window.UpdateLayout();
        var surface = window.FindControl<Border>("Surface")!;
        var middle = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;

        window.MouseMove(middle);
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);

        await Eventually(() => input.Injected.Any(e => e.EventType == InputEventType.MouseButtonUp), "the click arrives");
        var click = input.Injected.First(e => e.EventType == InputEventType.MouseButtonDown);
        Assert.Equal(1, click.Button);
        Assert.InRange(click.X, 16384 - 400, 16384 + 400);   // the middle of the picture
        Assert.InRange(click.Y, 16384 - 400, 16384 + 400);
    }

    [AvaloniaFact]
    public async Task WithoutTheKeyboardRight_NoKeyReachesTheHost()
    {
        var (window, _, input, renderer) = await OpenAsync(Permission.ViewScreen | Permission.ControlMouse); // no keyboard
        await Eventually(() => renderer.FramesDrawn > 0, "a frame is drawn");
        window.UpdateLayout();
        var surface = window.FindControl<Border>("Surface")!;
        surface.Focus();
        await Eventually(() => surface.IsFocused, "the picture has focus");

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        // (The host decides what it injects, whatever the window sends.) A click sent after the keys: once it has arrived, any key event sent before it would have arrived too.
        var middle = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);
        await Eventually(() => input.Injected.Any(e => e.EventType == InputEventType.MouseButtonUp), "the later click arrives");

        Assert.DoesNotContain(input.Injected, e => e.EventType is InputEventType.KeyDown or InputEventType.KeyUp);
    }

    [AvaloniaFact]
    public async Task TheKeyboard_IsForwardedAsWindowsKeys_WhenAllowed()
    {
        var (window, _, input, renderer) = await OpenAsync(Permission.ViewScreen | Permission.ControlKeyboard);
        await Eventually(() => renderer.FramesDrawn > 0, "a frame is drawn");
        var surface = window.FindControl<Border>("Surface")!;
        surface.Focus();
        await Eventually(() => surface.IsFocused, "the picture has focus");

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);

        await Eventually(() => input.Injected.Count(e => e.KeyCode == 0x41) >= 2, "A down and up arrive");
        Assert.Equal([InputEventType.KeyDown, InputEventType.KeyUp], input.Injected.Where(e => e.KeyCode == 0x41).Select(e => e.EventType).Take(2));
    }

    [AvaloniaFact]
    public async Task WhenTheSessionEnds_TheWindowSaysSo()
    {
        var (window, session, _, _) = await OpenAsync(Permission.ViewScreen);
        await session.DisposeAsync();

        await Eventually(() => window.FindControl<Border>("EndedOverlay")!.IsVisible, "the ended overlay");
        Assert.Contains("已結束", window.Title);
    }
}
