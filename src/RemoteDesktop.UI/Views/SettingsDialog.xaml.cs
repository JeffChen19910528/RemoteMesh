using System.Windows;
using Microsoft.Win32;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Views;

public partial class SettingsDialog : Window
{
    public AppSettings? Result { get; private set; }

    public SettingsDialog(AppSettings current, string? problem)
    {
        InitializeComponent();
        ServerUrl.Text = current.ServerUrl;
        DeviceName.Text = current.DeviceName;
        AllowClipboard.IsChecked = current.AllowClipboard;
        FileRoot.Text = current.FileRoot;
        Show(problem);
    }

    private void Show(string? problem)
    {
        Problem.Text = problem ?? string.Empty;
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "選擇檔案傳輸資料夾" };
        if (dialog.ShowDialog(this) == true) FileRoot.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            ServerUrl = ServerUrl.Text.Trim(),
            DeviceName = DeviceName.Text.Trim(),
            AllowClipboard = AllowClipboard.IsChecked == true,
            FileRoot = FileRoot.Text.Trim()
        };
        var problem = settings.Validate();
        if (problem is not null) { Show(problem); return; }
        Result = settings;
        Close();
    }
}
