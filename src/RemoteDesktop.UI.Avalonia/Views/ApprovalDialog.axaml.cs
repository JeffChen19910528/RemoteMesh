using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Avalonia.Views;

/// <summary>"Somebody wants to connect": shows who, lets the user choose the rights, declines on timeout.</summary>
public partial class ApprovalDialog : Window
{
    private readonly Dictionary<Permission, CheckBox> _boxes = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft;

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Deny;

    public ApprovalDialog() => InitializeComponent();

    public ApprovalDialog(ApprovalRequest request, TimeSpan timeout) : this()
    {
        // The person knows the other side by its nine-digit number; the full ID is there for anyone who needs to check it.
        RequesterBox.Text = request.RequesterShortId is { } shortId
            ? RemoteDesktop.Protocol.ShortId.Format(shortId)
            : request.RequesterDeviceId.ToString();
        FullId.Text = request.RequesterShortId is null ? string.Empty : "完整識別碼:" + request.RequesterDeviceId;

        foreach (var option in PermissionOptions.All)
        {
            var box = new CheckBox
            {
                Content = option.Label,
                IsChecked = option.DefaultOn,
                IsEnabled = !option.Locked,
                Margin = new Thickness(0, 3, 0, 3)
            };
            _boxes[option.Permission] = box;
            Rights.Children.Add(box);
        }

        _secondsLeft = (int)Math.Max(1, timeout.TotalSeconds);
        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            if (--_secondsLeft <= 0) { Close(); return; } // nobody answered: stays declined
            UpdateCountdown();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateCountdown() => Countdown.Text = $"{_secondsLeft} 秒內沒有回應,將自動拒絕";

    private void OnAllow(object? sender, RoutedEventArgs e)
    {
        var granted = _boxes.Where(p => p.Value.IsChecked == true).Aggregate(Permission.None, (acc, p) => acc | p.Key);
        Decision = new ApprovalDecision(true, granted);
        Close();
    }

    private void OnDeny(object? sender, RoutedEventArgs e) => Close();
}
