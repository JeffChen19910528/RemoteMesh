using RemoteDesktop.Clipboard;
using RemoteDesktop.Client;
using RemoteDesktop.Encoding;
using RemoteDesktop.Platform.Linux;
using RemoteDesktop.Platform.MacOS;
using RemoteDesktop.Platform.Skia;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.Platform;

/// <summary>
/// Picks this computer's screen, input, clipboard and JPEG backends. The agent and the desktop application both call
/// this, so every operating system gets the same features from the same code path. What cannot be provided (no X
/// display, no clipboard tool, a permission the person has not granted yet) is left out and explained in
/// <see cref="HostSetup.Notices"/> rather than failing: the machine can still view other computers.
/// </summary>
public static class PlatformHost
{
    /// <summary>The lossy video codec for this system: GDI+ on Windows, Skia elsewhere. Null if none can be loaded.</summary>
    public static IJpegCodec? CreateJpegCodec()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsParts.CreateJpegCodec();
            return SelfTested(new SkiaJpegCodec());
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PlatformNotSupportedException or BadImageFormatException)
        {
            return null; // lossless frames only; everything else still works
        }
    }

    /// <summary>
    /// A codec whose native library is missing fails on first use, in the middle of a session. Proving it with one tiny
    /// picture now turns that into "no lossy video" (lossless works everywhere) instead of a broken session.
    /// </summary>
    internal static IJpegCodec? SelfTested(IJpegCodec codec)
    {
        try
        {
            var pixels = new byte[16 * 16 * 4];
            var jpeg = codec.Encode(pixels, 16, 16, 16 * 4, 80);
            codec.Decode(jpeg, 16, 16, new byte[16 * 16 * 4], 16 * 4);
            return codec;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException
                                       or InvalidOperationException or InvalidDataException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static HostSetup CreateHostServices(bool allowClipboard, string? fileRoot)
    {
        var notices = new List<string>();
        var policy = allowClipboard ? ClipboardPolicy.Bidirectional : ClipboardPolicy.Disabled;

        if (OperatingSystem.IsWindows())
            return new HostSetup(WindowsParts.Create(policy, fileRoot), notices);
        if (OperatingSystem.IsMacOS())
            return new HostSetup(MacParts.Create(policy, fileRoot, notices), notices);
        if (OperatingSystem.IsLinux())
            return new HostSetup(LinuxParts.Create(policy, fileRoot, notices), notices);

        notices.Add("No screen/input backend exists for this operating system: this machine cannot be controlled.");
        return new HostSetup(new HostServices { FileRoot = fileRoot }, notices);
    }

    private static class WindowsParts
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static IJpegCodec CreateJpegCodec() => new GdiJpegCodec();

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static HostServices Create(ClipboardPolicy policy, string? fileRoot) => new()
        {
            CaptureFactory = () => new GdiScreenCapture(),
            Input = new SendInputController(),
            Clipboard = new WindowsClipboardManager { Policy = policy },
            FileRoot = fileRoot
        };
    }

    private static class LinuxParts
    {
        public static HostServices Create(ClipboardPolicy policy, string? fileRoot, List<string> notices)
        {
            XTestInputController? input = null;
            Func<RemoteDesktop.Capture.IScreenCapture>? capture = null;
            try
            {
                input = new XTestInputController(); // opens the X display: the cheapest honest test of "can we use X here?"
                capture = () => new X11ScreenCapture();
                if (IsWaylandOnly())
                    notices.Add("This looks like a Wayland session. The screen can only be shared from an X11 session (choose \"on Xorg\" at the login screen); under Wayland the picture may be empty or black.");
            }
            catch (PlatformNotSupportedException ex)
            {
                notices.Add(ex.Message);
            }

            var clipboard = new LinuxClipboardManager { Policy = policy };
            if (!clipboard.IsAvailable) notices.Add(LinuxClipboardManager.InstallHint);
            return new HostServices { CaptureFactory = capture, Input = input, Clipboard = clipboard, FileRoot = fileRoot };
        }

        // XWayland answers on DISPLAY too, but only shows X11 windows: warn when the session is Wayland.
        private static bool IsWaylandOnly() =>
            string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
    }

    private static class MacParts
    {
        public static HostServices Create(ClipboardPolicy policy, string? fileRoot, List<string> notices)
        {
            foreach (var missing in MacPermissions.MissingPermissions())
                notices.Add($"Allow this application under {missing}, then restart it.");

            return new HostServices
            {
                CaptureFactory = () => new CoreGraphicsScreenCapture(),
                Input = new CGEventInputController(),
                Clipboard = new MacClipboardManager { Policy = policy },
                FileRoot = fileRoot
            };
        }
    }
}

/// <summary>The host backends for this machine, and what the person should know about them.</summary>
public sealed record HostSetup(HostServices Services, IReadOnlyList<string> Notices);
