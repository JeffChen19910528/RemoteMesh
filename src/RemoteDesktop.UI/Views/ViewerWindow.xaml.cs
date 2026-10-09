using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Input;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Views;

/// <summary>
/// The remote screen. Mouse and keyboard are forwarded only for the rights the host granted — there is no point
/// sending what the host will refuse — and only while the picture has the keyboard focus.
/// </summary>
public partial class ViewerWindow : Window
{
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(8);

    private readonly ViewerSession _session;
    private readonly WpfFrameRenderer _renderer;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _sinceMove = Stopwatch.StartNew();
    private readonly bool _canMouse, _canKeyboard;
    private (int X, int Y) _lastPosition;
    private long _lastFrames;
    private long _lastBytes;
    private bool _ended;

    public ViewerWindow(ViewerSession session, WpfFrameRenderer renderer)
    {
        InitializeComponent();
        _session = session;
        _renderer = renderer;

        var granted = session.Granted;
        _canMouse = (granted & Permission.ControlMouse) != 0;
        _canKeyboard = (granted & Permission.ControlKeyboard) != 0;
        GetClipboardButton.IsEnabled = (granted & Permission.ClipboardRead) != 0;
        SendClipboardButton.IsEnabled = (granted & Permission.ClipboardWrite) != 0;
        UploadButton.IsEnabled = (granted & Permission.FileUpload) != 0;
        DownloadButton.IsEnabled = (granted & Permission.FileDownload) != 0;
        Surface.Cursor = _canMouse ? Cursors.Cross : Cursors.Arrow;

        renderer.BitmapChanged += SetBitmap;
        if (renderer.Bitmap is not null) SetBitmap(renderer.Bitmap);

        session.Ended += reason => Dispatcher.BeginInvoke(() => OnEnded(reason));
        session.Reconnecting += () => Dispatcher.BeginInvoke(ShowReconnecting);
        session.Reconnected += () => Dispatcher.BeginInvoke(ShowReconnecting);
        ShowReconnecting();
        ShowSecurity();
        if (session.Completion.IsCompleted) OnEnded(session.Completion.Result);

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();

        Loaded += (_, _) => Surface.Focus();
        Closed += async (_, _) =>
        {
            _statusTimer.Stop();
            renderer.BitmapChanged -= SetBitmap;
            await session.DisposeAsync();
        };
    }

    private void SetBitmap(System.Windows.Media.Imaging.WriteableBitmap bitmap)
    {
        Screen.Source = bitmap;
        WaitingText.Visibility = Visibility.Collapsed;
    }

    // The connection dropped and is being rebuilt (the session itself is not over). The last picture stays on screen.
    private void ShowReconnecting()
    {
        if (_ended) return;
        ReconnectingBanner.Visibility = _session.IsReconnecting ? Visibility.Visible : Visibility.Collapsed;
        Title = _session.IsReconnecting ? "遠端桌面(重新連線中…)" : "遠端桌面";
    }

    private void OnEnded(string reason)
    {
        if (_ended) return;
        _ended = true;
        ReconnectingBanner.Visibility = Visibility.Collapsed;
        _statusTimer.Stop();
        EndedReason.Text = reason;
        EndedOverlay.Visibility = Visibility.Visible;
        Title = "遠端桌面(已結束)";
    }

    private void UpdateStatus()
    {
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
        var code = _session.SecurityCode;
        if (_session.PeerVerified)
        {
            SecurityBar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
            SecurityText.Text = $"✓ 已核對的裝置  ·  安全碼 {code}";
            VerifyButton.Visibility = Visibility.Collapsed;
            return;
        }
        SecurityText.Text = _session.PeerIsNew
            ? $"第一次連到這台裝置。請與對方核對安全碼:{code}(兩邊必須完全相同,不同代表連線被竄改)"
            : $"安全碼 {code}(尚未核對)";
        VerifyButton.Visibility = Visibility.Visible;
    }

    private void OnVerifyCode(object sender, RoutedEventArgs e)
    {
        if (_session.MarkPeerVerified()) ShowSecurity();
    }

    // ── Mouse / keyboard → host ──────────────────────────────────────────

    private (int X, int Y)? Locate(MouseEventArgs e)
    {
        var (w, h) = _renderer.FrameSize;
        var p = e.GetPosition(Surface);
        return CoordinateMapper.Map(p.X, p.Y, Surface.ActualWidth, Surface.ActualHeight, w, h);
    }

    private void Send(InputEvent e) => _ = SendSafeAsync(e);

    private async Task SendSafeAsync(InputEvent e)
    {
        if (_ended) return;
        try { await _session.SendInputAsync(e); }
        catch (Exception) { /* the session is ending; the Ended handler tells the user */ }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_canMouse || _ended || _sinceMove.Elapsed < MoveInterval) return;
        if (Locate(e) is not { } pos) return;
        _sinceMove.Restart();
        _lastPosition = pos;
        Send(new InputEvent { EventType = InputEventType.MouseMove, X = pos.X, Y = pos.Y });
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Surface.Focus();
        var button = KeyMap.ToButton(e.ChangedButton);
        if (!_canMouse || _ended || button == 0 || Locate(e) is not { } pos) return;
        Surface.CaptureMouse(); // keep receiving the release even if the pointer leaves the picture
        _lastPosition = pos;
        Send(new InputEvent { EventType = InputEventType.MouseButtonDown, X = pos.X, Y = pos.Y, Button = button });
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Surface.ReleaseMouseCapture();
        var button = KeyMap.ToButton(e.ChangedButton);
        if (!_canMouse || _ended || button == 0) return;
        var pos = Locate(e) ?? _lastPosition;
        Send(new InputEvent { EventType = InputEventType.MouseButtonUp, X = pos.X, Y = pos.Y, Button = button });
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_canMouse || _ended || Locate(e) is null) return;
        Send(new InputEvent { EventType = InputEventType.MouseWheel, Delta = e.Delta });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) => ForwardKey(e, InputEventType.KeyDown);
    protected override void OnPreviewKeyUp(KeyEventArgs e) => ForwardKey(e, InputEventType.KeyUp);

    private void ForwardKey(KeyEventArgs e, InputEventType type)
    {
        // Only while the picture itself has focus, so typing in a dialog or pressing Enter on a button stays local.
        if (!_canKeyboard || _ended || !Surface.IsKeyboardFocused) return;
        var vk = KeyMap.ToVirtualKey(KeyEventArgsLike.From(e));
        if (vk == 0) return;
        Send(new InputEvent { EventType = type, KeyCode = vk });
        e.Handled = true; // Alt+F4, Tab, ... go to the remote computer, not this window
    }

    // ── Clipboard / files ────────────────────────────────────────────────

    private async void OnGetClipboard(object sender, RoutedEventArgs e)
    {
        Say("取得中…");
        var read = await _session.Clipboard.ReadAsync(ClipboardFormat.Text, TimeSpan.FromSeconds(5));
        if (!read.Ok || read.Data is null) { Say(ResultText.Of(read.Result)); return; }
        try
        {
            System.Windows.Clipboard.SetText(System.Text.Encoding.UTF8.GetString(read.Data));
            Say("已複製到你的剪貼簿");
        }
        catch (System.Runtime.InteropServices.ExternalException) { Say("無法寫入你的剪貼簿(被其他程式佔用)"); }
    }

    private async void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        string? text = null;
        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { }
        if (string.IsNullOrEmpty(text)) { Say("你的剪貼簿沒有文字"); return; }
        Say("傳送中…");
        Say(ResultText.Of(await _session.Clipboard.WriteTextAsync(text, TimeSpan.FromSeconds(5))));
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇要傳送到對方電腦的檔案" };
        if (dialog.ShowDialog(this) != true) return;
        var name = System.IO.Path.GetFileName(dialog.FileName);
        var progress = new Progress<TransferProgress>(p => Say($"上傳 {name}:{p.ProgressPercent:0}%"));
        var outcome = await _session.Files.UploadAsync(dialog.FileName, name, progress);
        Say(outcome.Ok ? $"已上傳 {name}" : ResultText.Of(outcome.Result));
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var ask = new PromptDialog("下載檔案", "輸入對方「檔案傳輸資料夾」中的檔名:") { Owner = this };
        ask.ShowDialog();
        if (ask.Value is not { } remoteName) return;

        var save = new SaveFileDialog { Title = "儲存到", FileName = System.IO.Path.GetFileName(remoteName) };
        if (save.ShowDialog(this) != true) return;
        var progress = new Progress<TransferProgress>(p => Say($"下載 {remoteName}:{p.ProgressPercent:0}%"));
        var outcome = await _session.Files.DownloadAsync(remoteName, save.FileName, progress);
        Say(outcome.Ok ? $"已下載 {remoteName}" : ResultText.Of(outcome.Result));
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();
}
