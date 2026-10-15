using System.Windows;
using RemoteDesktop.Client;
using RemoteDesktop.UI.Services;
using RemoteDesktop.UI.ViewModels;
using RemoteDesktop.UI.Views;

namespace RemoteDesktop.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // REMOTEMESH_DATA keeps settings and the device identity somewhere else (portable use, testing).
        var dataDirectory = Environment.GetEnvironmentVariable("REMOTEMESH_DATA");
        var store = new SettingsStore(string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory);

        // One copy per data folder: a second copy would register the same device twice and fight over the connection.
        var lockName = @"Local\RemoteMesh.SingleInstance." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(store.DataDirectory.ToLowerInvariant())))[..16];
        _single = new Mutex(initiallyOwned: true, lockName, out var first);
        if (!first)
        {
            MessageBox.Show("RemoteMesh 已經在執行了。", "RemoteMesh", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var shell = new WpfShell(Dispatcher);
        var viewModel = new MainViewModel(shell, store, () => new WpfFrameRenderer(Dispatcher));
        var window = new MainWindow(viewModel);
        shell.MainWindow = window;
        MainWindow = window;
        window.Show();
    }

    private Mutex? _single;

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Opens the real windows for the view-model.</summary>
internal sealed class WpfShell(System.Windows.Threading.Dispatcher dispatcher) : IShell
{
    public Window? MainWindow { get; set; }

    public async Task<ApprovalDecision> AskApprovalAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await dispatcher.InvokeAsync(() =>
        {
            var dialog = new ApprovalDialog(request, timeout);
            if (MainWindow is { IsVisible: true }) dialog.Owner = MainWindow;
            // The requester left: take the prompt away so nobody answers a question that no longer exists.
            using var registration = ct.Register(() => dispatcher.BeginInvoke(dialog.Close));
            dialog.ShowDialog();
            ct.ThrowIfCancellationRequested();
            return dialog.Decision;
        });
    }

    public void ShowViewer(ViewerSession session, IViewerRenderer renderer) =>
        dispatcher.Invoke(() => new ViewerWindow(session, (WpfFrameRenderer)renderer).Show());

    public Task<AppSettings?> EditSettingsAsync(AppSettings current, string? problem) =>
        Task.FromResult(dispatcher.Invoke(() =>
        {
            var dialog = new SettingsDialog(current, problem);
            if (MainWindow is { IsVisible: true }) dialog.Owner = MainWindow;
            dialog.ShowDialog();
            return dialog.Result;
        }));

    public Task<bool> ConfirmAsync(string message) =>
        Task.FromResult(dispatcher.Invoke(() => System.Windows.MessageBox.Show(message, "RemoteMesh",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes));

    public void CopyToClipboard(string text) =>
        dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: the user can select the text instead */ }
        });
}
