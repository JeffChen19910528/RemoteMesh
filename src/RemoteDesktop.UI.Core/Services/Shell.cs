using RemoteDesktop.Client;
using RemoteDesktop.Core;

namespace RemoteDesktop.UI.Services;

/// <summary>Everything the view-model needs from the windowing layer, so it can be tested without windows.</summary>
public interface IShell
{
    /// <summary>Asks the person; <paramref name="ct"/> is cancelled if the request is withdrawn, which closes the prompt.</summary>
    Task<ApprovalDecision> AskApprovalAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct);
    void ShowViewer(ViewerSession session, IViewerRenderer renderer);
    /// <summary>Shows the settings form; null when the person cancelled.</summary>
    Task<AppSettings?> EditSettingsAsync(AppSettings current, string? problem);
    void CopyToClipboard(string text);

    /// <summary>Asks a yes/no question that must not be answered by accident.</summary>
    Task<bool> ConfirmAsync(string message);
}

/// <summary>The rights a host can hand out, with the wording shown in the approval dialog.</summary>
public static class PermissionOptions
{
    public sealed record Option(Permission Permission, string Label, bool DefaultOn, bool Locked);

    /// <summary>Least privilege by default: only viewing is pre-selected, and nothing risky is offered at all.</summary>
    public static IReadOnlyList<Option> All { get; } =
    [
        new(Permission.ViewScreen, "查看畫面", true, true),
        new(Permission.ControlMouse, "控制滑鼠", false, false),
        new(Permission.ControlKeyboard, "控制鍵盤", false, false),
        new(Permission.ClipboardRead, "讀取這台電腦的剪貼簿", false, false),
        new(Permission.ClipboardWrite, "寫入這台電腦的剪貼簿", false, false),
        new(Permission.FileUpload, "傳送檔案到這台電腦", false, false),
        new(Permission.FileDownload, "下載這台電腦的檔案", false, false),
    ];

    /// <summary>Human-readable list of rights, for the session list and the viewer's status line.</summary>
    public static string Describe(Permission granted)
    {
        var parts = All.Where(o => (granted & o.Permission) == o.Permission).Select(o => o.Label).ToList();
        return parts.Count == 0 ? "無" : string.Join("、", parts);
    }
}

/// <summary>Shows one approval prompt at a time; a request nobody answers in time is declined.</summary>
public sealed class UiApprovalHandler(IShell shell, TimeSpan? timeout = null) : IApprovalHandler
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    public async Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct); // several people asking at once: they are asked in turn, never merged
        try
        {
            return await shell.AskApprovalAsync(request, _timeout, ct);
        }
        finally { _one.Release(); }
    }
}
