using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Input;
using RemoteDesktop.UI.Avalonia.Services;
using RemoteDesktop.UI.Services;
using Permission = RemoteDesktop.Core.Permission;

namespace RemoteDesktop.UI.Avalonia.Views;

/// <summary>
/// Shows the other computer's screen and forwards the mouse and keyboard to it, within what the other person allowed.
/// Same behaviour as the Windows window: the pointer is mapped onto the letterboxed picture, keys go as Windows
/// virtual-key codes, a dropped connection keeps the last picture and says so.
/// </summary>
public partial class ViewerWindow : Window
{
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(16); // about 60 moves per second is plenty

    private readonly ViewerSession? _session;
    private readonly AvaloniaFrameRenderer? _renderer;
    private readonly bool _canMouse, _canKeyboard;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _sinceMove = Stopwatch.StartNew();
    private (int X, int Y) _lastPosition;
    private long _lastFrames, _lastBytes;
    private bool _ended;

    public ViewerWindow() => InitializeComponent();

    public ViewerWindow(ViewerSession session, AvaloniaFrameRenderer renderer) : this()
    {
        _session = session;
        _renderer = renderer;

        var granted = session.Granted;
        _canMouse = (granted & Permission.ControlMouse) != 0;
        _canKeyboard = (granted & Permission.ControlKeyboard) != 0;
        GetClipboardButton.IsEnabled = (granted & Permission.ClipboardRead) != 0;
        SendClipboardButton.IsEnabled = (granted & Permission.ClipboardWrite) != 0;
        UploadButton.IsEnabled = (granted & Permission.FileUpload) != 0;
        DownloadButton.IsEnabled = (granted & Permission.FileDownload) != 0;
        Surface.Cursor = new Cursor(_canMouse ? StandardCursorType.Cross : StandardCursorType.Arrow);

        renderer.BitmapChanged += SetBitmap;
        renderer.FrameDrawn += OnFrameDrawn;
        if (renderer.Bitmap is not null) SetBitmap(renderer.Bitmap);

        session.Ended += reason => Dispatcher.UIThread.Post(() => OnEnded(reason));
        session.Reconnecting += () => Dispatcher.UIThread.Post(ShowReconnecting);
        session.Reconnected += () => Dispatcher.UIThread.Post(ShowReconnecting);
        ShowReconnecting();
        ShowSecurity();
        if (session.Completion.IsCompleted) OnEnded(session.Completion.Result);

        Surface.PointerMoved += OnPointerMoved;
        Surface.PointerPressed += OnPointerPressed;
        Surface.PointerReleased += OnPointerReleased;
        Surface.PointerWheelChanged += OnPointerWheel;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();

        Opened += (_, _) => Surface.Focus();
        Closed += async (_, _) =>
        {
            _statusTimer.Stop();
            renderer.BitmapChanged -= SetBitmap;
            renderer.FrameDrawn -= OnFrameDrawn;
            await session.DisposeAsync();
        };
    }

    private void SetBitmap(WriteableBitmap bitmap)
    {
        Screen.Source = bitmap;
        WaitingText.IsVisible = false;
    }

    private void OnFrameDrawn() => Screen.InvalidateVisual();

    // The connection dropped and is being rebuilt (the session itself is not over). The last picture stays on screen.
    private void ShowReconnecting()
    {
        if (_ended || _session is null) return;
        ReconnectingBanner.IsVisible = _session.IsReconnecting;
        Title = _session.IsReconnecting ? "遠端桌面(重新連線中…)" : "遠端桌面";
    }

    private void OnEnded(string reason)
    {
        if (_ended) return;
        _ended = true;
        ReconnectingBanner.IsVisible = false;
        _statusTimer.Stop();
        EndedReason.Text = reason;
        EndedOverlay.IsVisible = true;
        Title = "遠端桌面(已結束)";
    }

    private void UpdateStatus()
    {
        if (_session is null || _renderer is null) return;
        var frames = _renderer.FramesDrawn;
        var fps = frames - _lastFrames;
        _lastFrames = frames;
        var bytes = _session.VideoBytesReceived;
        var mbit = (bytes - _lastBytes) * 8 / 1_000_000.0;
        _lastBytes = bytes;
        var rtt = _session.Rtt is { } r ? $"{r.TotalMilliseconds:0} ms" : "—";
        var mode = _canMouse || _canKeyboard ? "可控制" : "僅檢視";
        var quality = _session.VideoCodec == RemoteDesktop.Encoding.CodecType.TileDeltaJpeg ? "壓縮" : "無損";
        // Nothing is sent while the remote screen is unchanged, so "0 fps" means "nothing is moving", not "frozen".
        var motion = fps == 0 ? "畫面靜止" : $"{fps} fps";
        StatusText.Text = $"{mode}  ·  {motion}  ·  {mbit:0.0} Mbit/s({quality})  ·  延遲 {rtt}";
    }

    private void Say(string message) => Notice.Text = message;

    /// <summary>The code both people should see the same; checked once, it is remembered for this device's key.</summary>
    private void ShowSecurity()
    {
        if (_session is null) return;
        var code = _session.SecurityCode;
        if (_session.PeerVerified)
        {
            SecurityBar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
            SecurityText.Text = $"✓ 已核對的裝置  ·  安全碼 {code}";
            VerifyButton.IsVisible = false;
            return;
        }
        SecurityText.Text = _session.PeerIsNew
            ? $"第一次連到這台裝置。請與對方核對安全碼:{code}(兩邊必須完全相同,不同代表連線被竄改)"
            : _session.PeerKeyRotated
                ? $"這台裝置換了新的安全金鑰(它自己簽署的,已自動跟上)。新的安全碼 {code}(需要重新核對)"
                : $"安全碼 {code}(尚未核對)";
        VerifyButton.IsVisible = true;
    }

    private void OnVerifyCode(object? sender, RoutedEventArgs e)
    {
        if (_session?.MarkPeerVerified() == true) ShowSecurity();
    }

    // ── Mouse / keyboard → host ──────────────────────────────────────────

    private (int X, int Y)? Locate(PointerEventArgs e)
    {
        if (_renderer is null) return null;
        var (w, h) = _renderer.FrameSize;
        var p = e.GetPosition(Surface);
        return CoordinateMapper.Map(p.X, p.Y, Surface.Bounds.Width, Surface.Bounds.Height, w, h);
    }

    private void Send(InputEvent e) => _ = SendSafeAsync(e);

    private async Task SendSafeAsync(InputEvent e)
    {
        if (_ended || _session is null) return;
        try { await _session.SendInputAsync(e); }
        catch (Exception) { /* the session is ending; the Ended handler tells the user */ }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_canMouse || _ended || _sinceMove.Elapsed < MoveInterval) return;
        if (Locate(e) is not { } pos) return;
        _sinceMove.Restart();
        _lastPosition = pos;
        Send(new InputEvent { EventType = InputEventType.MouseMove, X = pos.X, Y = pos.Y });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Surface.Focus();
        var button = AvaloniaKeyMap.ToButton(e.GetCurrentPoint(Surface).Properties.PointerUpdateKind);
        if (!_canMouse || _ended || button == 0 || Locate(e) is not { } pos) return;
        e.Pointer.Capture(Surface); // keep receiving the release even if the pointer leaves the picture
        _lastPosition = pos;
        Send(new InputEvent { EventType = InputEventType.MouseButtonDown, X = pos.X, Y = pos.Y, Button = button });
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        var button = AvaloniaKeyMap.ToButton(e.InitialPressMouseButton switch
        {
            MouseButton.Left => PointerUpdateKind.LeftButtonReleased,
            MouseButton.Right => PointerUpdateKind.RightButtonReleased,
            MouseButton.Middle => PointerUpdateKind.MiddleButtonReleased,
            _ => PointerUpdateKind.Other
        });
        if (!_canMouse || _ended || button == 0) return;
        var pos = Locate(e) ?? _lastPosition;
        Send(new InputEvent { EventType = InputEventType.MouseButtonUp, X = pos.X, Y = pos.Y, Button = button });
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_canMouse || _ended || Locate(e) is null) return;
        var units = AvaloniaKeyMap.ToWheelUnits(e.Delta.Y);
        if (units != 0) Send(new InputEvent { EventType = InputEventType.MouseWheel, Delta = units });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => ForwardKey(e, InputEventType.KeyDown);
    private void OnKeyUp(object? sender, KeyEventArgs e) => ForwardKey(e, InputEventType.KeyUp);

    private void ForwardKey(KeyEventArgs e, InputEventType type)
    {
        // Only while the picture itself has focus, so typing in a dialog or pressing Enter on a button stays local.
        if (!_canKeyboard || _ended || !Surface.IsFocused) return;
        var vk = AvaloniaKeyMap.ToVirtualKey(e.Key);
        if (vk == 0) return;
        Send(new InputEvent { EventType = type, KeyCode = vk });
        e.Handled = true; // Alt+F4, Tab, ... go to the remote computer, not this window
    }

    // ── Clipboard / files ────────────────────────────────────────────────

    private async void OnGetClipboard(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        Say("取得中…");
        var read = await _session.Clipboard.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5));
        if (!read.Ok || read.Data is null) { Say(ResultText.Of(read.Result)); return; }
        try
        {
            if (Clipboard is { } board) await board.SetTextAsync(System.Text.Encoding.UTF8.GetString(read.Data));
            Say("已複製到你的剪貼簿");
        }
        catch (Exception) { Say("無法寫入你的剪貼簿"); }
    }

    private async void OnSendClipboard(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        string? text = null;
        try { if (Clipboard is { } board) text = await board.TryGetTextAsync(); }
        catch (Exception) { }
        if (string.IsNullOrEmpty(text)) { Say("你的剪貼簿沒有文字"); return; }
        Say("傳送中…");
        Say(ResultText.Of(await _session.Clipboard.WriteTextAsync(text, TimeSpan.FromSeconds(5))));
    }

    private async void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "選擇要傳送到對方電腦的檔案", AllowMultiple = false });
        var local = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (local is null) return;
        var name = System.IO.Path.GetFileName(local);
        var progress = new Progress<TransferProgress>(p => Say($"上傳 {name}:{p.ProgressPercent:0}%"));
        var outcome = await _session.Files.UploadAsync(local, name, progress);
        Say(outcome.Ok ? $"已上傳 {name}" : ResultText.Of(outcome.Result));
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var remoteName = await TextPromptDialog.AskAsync(this, "下載檔案", "輸入對方「檔案傳輸資料夾」中的檔名:");
        if (remoteName is null) return;

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "儲存到",
            SuggestedFileName = System.IO.Path.GetFileName(remoteName)
        });
        var local = target?.TryGetLocalPath();
        if (local is null) return;
        var progress = new Progress<TransferProgress>(p => Say($"下載 {remoteName}:{p.ProgressPercent:0}%"));
        var outcome = await _session.Files.DownloadAsync(remoteName, local, progress);
        Say(outcome.Ok ? $"已下載 {remoteName}" : ResultText.Of(outcome.Result));
    }

    private void OnDisconnect(object? sender, RoutedEventArgs e) => Close();
}
