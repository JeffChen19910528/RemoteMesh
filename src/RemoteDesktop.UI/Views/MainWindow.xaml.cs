using System.Windows;
using RemoteDesktop.UI.ViewModels;

namespace RemoteDesktop.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.StartAsync();
        Closed += async (_, _) => await viewModel.DisposeAsync();
    }
}
