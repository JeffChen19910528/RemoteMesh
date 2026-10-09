using RemoteDesktop.Platform;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// The Linux clipboard (text), through the tools every desktop has: <c>wl-copy</c>/<c>wl-paste</c> on Wayland,
/// <c>xclip</c> or <c>xsel</c> on X11. Linux has no clipboard API that works without a window, and the helpers
/// are the maintained way to do it.
/// </summary>
public sealed class LinuxClipboardManager : CommandClipboardManager
{
    /// <summary>The package to install when <see cref="CommandClipboardManager.IsAvailable"/> is false.</summary>
    public const string InstallHint = "Install xclip (X11) or wl-clipboard (Wayland) to share the clipboard.";

    public LinuxClipboardManager(ICommandRunner? runner = null, bool? wayland = null)
        : this(runner ?? new ProcessCommandRunner(), wayland ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) { }

    private LinuxClipboardManager(ICommandRunner runner, bool wayland) : base(runner, Detect(runner, wayland), InstallHint) { }

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
}
