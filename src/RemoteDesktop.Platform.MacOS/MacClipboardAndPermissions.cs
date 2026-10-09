using RemoteDesktop.Platform;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// The macOS clipboard (text) through <c>pbcopy</c> / <c>pbpaste</c>, which every Mac has. Text goes in and out on
/// stdin / stdout as UTF-8 (the runner sets the locale for them), never as an argument.
/// </summary>
public sealed class MacClipboardManager : CommandClipboardManager
{
    public MacClipboardManager(ICommandRunner? runner = null) : this(runner ?? new ProcessCommandRunner(), 0) { }

    private MacClipboardManager(ICommandRunner runner, int unused)
        : base(runner,
            runner.Exists("pbpaste") && runner.Exists("pbcopy") ? new Tool("pbpaste", [], "pbcopy", []) : null,
            "pbcopy / pbpaste were not found; the clipboard cannot be shared.") { }
}

/// <summary>What macOS lets this application do. Both must be granted by the person at the computer.</summary>
public static class MacPermissions
{
    /// <summary>Screen Recording: without it the screen capture shows only the wallpaper.</summary>
    public static bool CanCaptureScreen() => OperatingSystem.IsMacOS() && MacNative.CGPreflightScreenCaptureAccess();

    /// <summary>Accessibility: without it mouse and keyboard events are dropped.</summary>
    public static bool CanControlInput() => OperatingSystem.IsMacOS() && MacNative.AXIsProcessTrusted();

    /// <summary>Shows the system's permission prompt for Screen Recording (once); returns the current state.</summary>
    public static bool RequestScreenCapture() => OperatingSystem.IsMacOS() && MacNative.CGRequestScreenCaptureAccess();

    /// <summary>What to tell the person when something is missing; empty when everything is granted.</summary>
    public static IReadOnlyList<string> MissingPermissions()
    {
        var missing = new List<string>();
        if (!OperatingSystem.IsMacOS()) return missing;
        if (!CanCaptureScreen()) missing.Add("Screen Recording (System Settings → Privacy & Security → Screen Recording)");
        if (!CanControlInput()) missing.Add("Accessibility (System Settings → Privacy & Security → Accessibility)");
        return missing;
    }
}
