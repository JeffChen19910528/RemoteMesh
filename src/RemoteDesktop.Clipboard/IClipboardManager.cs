namespace RemoteDesktop.Clipboard;

public interface IClipboardManager
{
    ClipboardPolicy Policy { get; set; }
    Task<string?> GetTextAsync(CancellationToken ct = default);
    Task SetTextAsync(string text, CancellationToken ct = default);
    Task<byte[]?> GetImageAsync(CancellationToken ct = default);
    Task SetImageAsync(byte[] imageData, CancellationToken ct = default);
}
