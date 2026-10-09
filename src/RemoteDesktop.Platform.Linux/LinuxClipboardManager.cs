using RemoteDesktop.Clipboard;
using RemoteDesktop.Platform;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// The Linux clipboard (text and images), through the tools every desktop ships:
/// <c>wl-copy</c>/<c>wl-paste</c> on Wayland, <c>xclip</c> on X11. Text is read/written as UTF-8.
/// Images are read/written as PNG bytes via the <c>image/png</c> MIME type.
/// </summary>
public sealed class LinuxClipboardManager : CommandClipboardManager
{
    /// <summary>The package to install when <see cref="CommandClipboardManager.IsAvailable"/> is false.</summary>
    public const string InstallHint = "Install xclip (X11) or wl-clipboard (Wayland) to share the clipboard.";

    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(5);

    private readonly bool _wayland;

    public LinuxClipboardManager(ICommandRunner? runner = null, bool? wayland = null)
        : this(runner ?? new ProcessCommandRunner(), wayland ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) { }

    private LinuxClipboardManager(ICommandRunner runner, bool wayland)
        : base(runner, Detect(runner, wayland), InstallHint)
    {
        _wayland = wayland;
    }

    private static Tool? Detect(ICommandRunner runner, bool wayland)
    {
        if (wayland && runner.Exists("wl-paste") && runner.Exists("wl-copy"))
            return new Tool("wl-paste", ["--no-newline"], "wl-copy", []);
        if (runner.Exists("xclip"))
            return new Tool("xclip", ["-selection", "clipboard", "-out"], "xclip", ["-selection", "clipboard", "-in"]);
        if (runner.Exists("xsel"))
            return new Tool("xsel", ["--clipboard", "--output"], "xsel", ["--clipboard", "--input"]);
        return null;
    }

    public override async Task<byte[]?> GetImageAsync(CancellationToken ct = default)
    {
        var (file, args) = DetectImageRead();
        if (file is null) return null;

        var result = await _runner.RunAsync(file, args, null, captureOutput: true, ImageTimeout, ct);
        if (result.TimedOut || result.ExitCode != 0 || result.Output.Length == 0) return null;
        if (result.Output.Length > ClipboardLimits.MaxImageBytes) return null;
        return result.Output;
    }

    public override async Task SetImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (imageData.Length > ClipboardLimits.MaxImageBytes) throw new IOException("The image is too large for the clipboard.");

        var (file, args) = DetectImageWrite();
        if (file is null) throw new NotSupportedException(InstallHint);

        var result = await _runner.RunAsync(file, args, imageData, captureOutput: false, ImageTimeout, ct);
        if (result.TimedOut || result.ExitCode != 0) throw new IOException("The clipboard tool did not accept the image.");
    }

    private (string? file, string[] args) DetectImageRead()
    {
        if (_wayland && _runner.Exists("wl-paste"))
            return ("wl-paste", ["--type", "image/png"]);
        if (_runner.Exists("xclip"))
            return ("xclip", ["-selection", "clipboard", "-t", "image/png", "-out"]);
        return (null, []);
    }

    private (string? file, string[] args) DetectImageWrite()
    {
        if (_wayland && _runner.Exists("wl-copy"))
            return ("wl-copy", ["--type", "image/png"]);
        if (_runner.Exists("xclip"))
            return ("xclip", ["-selection", "clipboard", "-t", "image/png", "-in"]);
        return (null, []);
    }
}
