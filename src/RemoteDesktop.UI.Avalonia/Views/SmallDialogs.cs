using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace RemoteDesktop.UI.Avalonia.Views;

/// <summary>Asks for one line of text. Returns null when cancelled or left empty.</summary>
public sealed class TextPromptDialog : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(0, 8, 0, 16) };
    private string? _value;

    private TextPromptDialog(string title, string label)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "確定", IsDefault = true, Margin = new Thickness(0), Padding = new Thickness(20, 6) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 6) };
        ok.Click += (_, _) =>
        {
            var text = (_input.Text ?? "").Trim();
            if (text.Length == 0) return;
            _value = text;
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = label, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                _input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } }
            }
        };
        Opened += (_, _) => _input.Focus();
    }

    public static async Task<string?> AskAsync(Window owner, string title, string label)
    {
        var dialog = new TextPromptDialog(title, label);
        await dialog.ShowDialog(owner);
        return dialog._value;
    }
}

/// <summary>A yes/no question whose default answer is "no", so a stray Enter never confirms it.</summary>
public sealed class ConfirmDialog : Window
{
    private bool _yes;

    private ConfirmDialog(string message)
    {
        Title = "RemoteMesh";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yes = new Button { Content = "是", Padding = new Thickness(22, 6) };
        var no = new Button { Content = "否", IsDefault = true, IsCancel = true, Margin = new Thickness(0), Padding = new Thickness(22, 6) };
        yes.Click += (_, _) => { _yes = true; Close(); };
        no.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } }
            }
        };
        Opened += (_, _) => no.Focus();
    }

    public static async Task<bool> AskAsync(Window? owner, string message)
    {
        var dialog = new ConfirmDialog(message);
        if (owner is not null) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return dialog._yes;
    }
}

/// <summary>A one-button notice.</summary>
public sealed class NoticeDialog : Window
{
    public NoticeDialog(string message)
    {
        Title = "RemoteMesh";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var ok = new Button { Content = "確定", IsDefault = true, Margin = new Thickness(0), Padding = new Thickness(22, 6), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children = { new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) }, ok }
        };
    }
}
