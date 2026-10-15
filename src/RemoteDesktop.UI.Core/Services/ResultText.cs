using RemoteDesktop.Clipboard;
using RemoteDesktop.FileTransfer;

namespace RemoteDesktop.UI.Services;

/// <summary>Plain-language wording for results, so the UI never shows an enum name to a person.</summary>
public static class ResultText
{
    public static string Of(ClipboardResult r) => r switch
    {
        ClipboardResult.Ok => "完成",
        ClipboardResult.PolicyDenied => "對方的電腦不允許共用剪貼簿",
        ClipboardResult.PermissionDenied => "對方沒有授權你使用剪貼簿",
        ClipboardResult.SessionNotActive => "連線已經不在作用中",
        ClipboardResult.TooLarge => "內容太大",
        ClipboardResult.Empty => "對方的剪貼簿是空的(或內容不是文字)",
        ClipboardResult.RateLimited => "操作太頻繁,請稍後再試",
        _ => "剪貼簿操作失敗"
    };

    public static string Of(FileTransferResult r) => r switch
    {
        FileTransferResult.Ok => "完成",
        FileTransferResult.PermissionDenied => "對方沒有授權你傳輸檔案",
        FileTransferResult.SessionNotActive => "連線已經不在作用中",
        FileTransferResult.InvalidName => "檔名不被接受",
        FileTransferResult.TooLarge => "檔案太大",
        FileTransferResult.Exists => "對方的資料夾已經有同名檔案",
        FileTransferResult.NotFound => "對方的資料夾找不到這個檔案",
        FileTransferResult.ChecksumMismatch => "檔案傳輸後驗證不符,已丟棄",
        FileTransferResult.Busy => "對方正忙於其他傳輸,請稍後再試",
        FileTransferResult.Cancelled => "已取消",
        FileTransferResult.Timeout => "等待對方回應逾時",
        FileTransferResult.ConnectionClosed => "連線中斷",
        _ => "檔案傳輸失敗"
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
    };
}
