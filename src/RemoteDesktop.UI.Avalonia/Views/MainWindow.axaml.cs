using Avalonia.Controls;
using RemoteDesktop.UI.ViewModels;

namespace RemoteDesktop.UI.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;

    public MainWindow() => InitializeComponent(); // the designer / XAML loader

    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.StartAsync();
        Closed += async (_, _) => await viewModel.DisposeAsync();
    }
}
