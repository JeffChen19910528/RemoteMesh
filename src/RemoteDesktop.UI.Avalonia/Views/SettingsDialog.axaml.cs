using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Avalonia.Views;

public partial class SettingsDialog : Window
{
    public AppSettings? Result { get; private set; }

    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(AppSettings current, string? problem) : this()
    {
        ServerUrl.Text = current.ServerUrl;
        DeviceName.Text = current.DeviceName;
        AllowClipboard.IsChecked = current.AllowClipboard;
        FileRoot.Text = current.FileRoot;
        ShowProblem(problem);
    }

    private void ShowProblem(string? problem)
    {
        Problem.Text = problem ?? string.Empty;
        Problem.IsVisible = problem is not null;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "選擇檔案傳輸資料夾", AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) FileRoot.Text = path;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            ServerUrl = (ServerUrl.Text ?? "").Trim(),
            DeviceName = (DeviceName.Text ?? "").Trim(),
            AllowClipboard = AllowClipboard.IsChecked == true,
            FileRoot = (FileRoot.Text ?? "").Trim()
        };
        var problem = settings.Validate();
        if (problem is not null) { ShowProblem(problem); return; }
        Result = settings;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
