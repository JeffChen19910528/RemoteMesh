using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RemoteDesktop.Client;
using RemoteDesktop.UI.Avalonia.Services;
using RemoteDesktop.UI.Avalonia.Views;
using RemoteDesktop.UI.Services;
using RemoteDesktop.UI.ViewModels;

namespace RemoteDesktop.UI.Avalonia;

public partial class App : Application
{
    private FileStream? _single;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // REMOTEMESH_DATA keeps settings and the device identity somewhere else (portable use, testing).
            var dataDirectory = Environment.GetEnvironmentVariable("REMOTEMESH_DATA");
            var store = new SettingsStore(string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory);

            // One copy per data folder: a second copy would register the same device twice and fight over the connection.
            if (!SingleInstance.TryAcquire(store.DataDirectory, out _single))
            {
                var notice = new NoticeDialog("RemoteMesh 已經在執行了。");
                notice.Closed += (_, _) => desktop.Shutdown();
                desktop.MainWindow = notice;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var shell = new AvaloniaShell();
            var viewModel = new MainViewModel(shell, store, () => new AvaloniaFrameRenderer());
            var window = new MainWindow(viewModel);
            shell.MainWindow = window;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _single?.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Holds a lock file for as long as the application runs; a second copy cannot take it.</summary>
internal static class SingleInstance
{
    public static bool TryAcquire(string dataDirectory, out FileStream? held)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            held = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException)
        {
            held = null;
            return false;
        }
    }
}

/// <summary>Opens the real windows for the view-model, always on the UI thread.</summary>
internal sealed class AvaloniaShell : IShell
{
    public Window? MainWindow { get; set; }

    public async Task<ApprovalDecision> AskApprovalAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var closed = new TaskCompletionSource();
        var dialog = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var d = new ApprovalDialog(request, timeout);
            d.Closed += (_, _) => closed.TrySetResult();
            d.Show();
            return d;
        });
        // The requester gave up while the question was open: close it, and the answer stays "declined".
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await closed.Task;
        ct.ThrowIfCancellationRequested();
        return dialog.Decision;
    }

    public void ShowViewer(ViewerSession session, IViewerRenderer renderer) =>
        Dispatcher.UIThread.Post(() => new ViewerWindow(session, (AvaloniaFrameRenderer)renderer).Show());

    public Task<AppSettings?> EditSettingsAsync(AppSettings current, string? problem) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new SettingsDialog(current, problem);
            if (MainWindow is { IsVisible: true } owner) await dialog.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                await closed.Task;
            }
            return dialog.Result;
        });

    public Task<bool> ConfirmAsync(string message) =>
        Dispatcher.UIThread.InvokeAsync(() => ConfirmDialog.AskAsync(MainWindow is { IsVisible: true } ? MainWindow : null, message));

    public void CopyToClipboard(string text) =>
        Dispatcher.UIThread.Post(async () =>
        {
            try { if (MainWindow?.Clipboard is { } board) await board.SetTextAsync(text); }
            catch (Exception) { /* clipboard busy: the user can select the text instead */ }
        });
}
