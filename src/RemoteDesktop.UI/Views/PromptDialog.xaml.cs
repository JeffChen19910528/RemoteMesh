using System.Windows;

namespace RemoteDesktop.UI.Views;

public partial class PromptDialog : Window
{
    public string? Value { get; private set; }

    public PromptDialog(string title, string label)
    {
        InitializeComponent();
        Title = title;
        Label.Text = label;
        Loaded += (_, _) => Input.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Value = text;
        Close();
    }
}
