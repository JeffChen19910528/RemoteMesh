namespace RemoteDesktop.Clipboard;

/// <summary>In-process clipboard (tests, headless agents). The OS-backed manager implements the same interface.</summary>
public sealed class InMemoryClipboardManager : IClipboardManager
{
    private string? _text;
    private byte[]? _image;

    /// <summary>Secure default: clipboard sharing is off until the host opts in.</summary>
    public ClipboardPolicy Policy { get; set; } = ClipboardPolicy.Disabled;

    public Task<string?> GetTextAsync(CancellationToken ct = default) => Task.FromResult(_text);
    public Task SetTextAsync(string text, CancellationToken ct = default) { _text = text; return Task.CompletedTask; }
    public Task<byte[]?> GetImageAsync(CancellationToken ct = default) => Task.FromResult(_image);
    public Task SetImageAsync(byte[] imageData, CancellationToken ct = default) { _image = imageData; return Task.CompletedTask; }
}
