using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Platform;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// The macOS clipboard through <c>pbcopy</c>/<c>pbpaste</c> (text) and NSPasteboard P/Invoke (images).
/// Text goes in/out via stdin/stdout as UTF-8 — never as an argument. Image data is PNG bytes stored
/// under the <c>public.png</c> UTI, which every modern macOS app understands.
/// </summary>
public sealed class MacClipboardManager : CommandClipboardManager
{
    public MacClipboardManager(ICommandRunner? runner = null) : this(runner ?? new ProcessCommandRunner(), 0) { }

    private MacClipboardManager(ICommandRunner runner, int unused)
        : base(runner,
            runner.Exists("pbpaste") && runner.Exists("pbcopy") ? new Tool("pbpaste", [], "pbcopy", []) : null,
            "pbcopy / pbpaste were not found; the clipboard cannot be shared.") { }

    public override Task<byte[]?> GetImageAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult<byte[]?>(null);
        return Task.Run(MacPasteboard.GetImageBytes, ct);
    }

    public override Task SetImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (imageData.Length > ClipboardLimits.MaxImageBytes) throw new IOException("The image is too large for the clipboard.");
        return Task.Run(() => MacPasteboard.SetImageBytes(imageData), ct);
    }
}

/// <summary>Reads and writes PNG image data via NSPasteboard using the Objective-C runtime.</summary>
internal static class MacPasteboard
{
    private const string PngUti = "public.png";

    // Selectors are obtained once at class-init time; this type is never touched on non-macOS because
    // MacClipboardManager.GetImageAsync / SetImageAsync guard with OperatingSystem.IsMacOS() first.
    private static readonly IntPtr _sPasteboard = MacNative.ObjcGetClass("NSPasteboard");
    private static readonly IntPtr _sNsData = MacNative.ObjcGetClass("NSData");
    private static readonly IntPtr _sNsString = MacNative.ObjcGetClass("NSString");
    private static readonly IntPtr _selGeneralPasteboard = MacNative.ObjcSel("generalPasteboard");
    private static readonly IntPtr _selDataForType = MacNative.ObjcSel("dataForType:");
    private static readonly IntPtr _selClearContents = MacNative.ObjcSel("clearContents");
    private static readonly IntPtr _selSetDataForType = MacNative.ObjcSel("setData:forType:");
    private static readonly IntPtr _selDataWithBytesLength = MacNative.ObjcSel("dataWithBytes:length:");
    private static readonly IntPtr _selBytes = MacNative.ObjcSel("bytes");
    private static readonly IntPtr _selLength = MacNative.ObjcSel("length");
    private static readonly IntPtr _selStringWithUtf8 = MacNative.ObjcSel("stringWithUTF8String:");

    private static IntPtr MakeNSString(string s)
    {
        var raw = Marshal.StringToHGlobalAnsi(s);
        try { return MacNative.ObjcMsg1(_sNsString, _selStringWithUtf8, raw); }
        finally { Marshal.FreeHGlobal(raw); }
    }

    private static IntPtr GeneralPasteboard() => MacNative.ObjcMsg0(_sPasteboard, _selGeneralPasteboard);

    public static byte[]? GetImageBytes()
    {
        var pb = GeneralPasteboard();
        if (pb == IntPtr.Zero) return null;

        var typeStr = MakeNSString(PngUti);
        var data = MacNative.ObjcMsg1(pb, _selDataForType, typeStr);
        if (data == IntPtr.Zero) return null;

        var length = MacNative.ObjcMsgNint(data, _selLength);
        if (length <= 0 || length > ClipboardLimits.MaxImageBytes) return null;

        var bytesPtr = MacNative.ObjcMsg0(data, _selBytes);
        if (bytesPtr == IntPtr.Zero) return null;

        var result = new byte[(int)length];
        Marshal.Copy(bytesPtr, result, 0, (int)length);
        return result;
    }

    public static void SetImageBytes(byte[] imageData)
    {
        var pb = GeneralPasteboard();
        if (pb == IntPtr.Zero) throw new IOException("Could not access the system clipboard.");

        MacNative.ObjcMsgNint(pb, _selClearContents);

        var handle = GCHandle.Alloc(imageData, GCHandleType.Pinned);
        try
        {
            var nsData = MacNative.ObjcMsgBytesLen(_sNsData, _selDataWithBytesLength,
                handle.AddrOfPinnedObject(), imageData.Length);
            if (nsData == IntPtr.Zero) throw new IOException("Could not create clipboard data object.");

            var typeStr = MakeNSString(PngUti);
            MacNative.ObjcMsg2(pb, _selSetDataForType, nsData, typeStr);
        }
        finally { handle.Free(); }
    }
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

    /// <summary>
    /// Opens System Settings directly to the Screen Recording or Accessibility privacy pane so the person can grant
    /// the missing permission without hunting through menus. Returns true when the URL was dispatched (the person may
    /// still dismiss the dialog). Has no effect and returns false when not on macOS.
    /// </summary>
    public static bool OpenScreenRecordingSettings() => OpenPrivacyUrl("Privacy_ScreenCapture");

    /// <summary>Opens System Settings directly to the Accessibility privacy pane.</summary>
    public static bool OpenAccessibilitySettings() => OpenPrivacyUrl("Privacy_Accessibility");

    private static bool OpenPrivacyUrl(string pane)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            Process.Start(new ProcessStartInfo("open",
                $"x-apple.systempreferences:com.apple.preference.security?{pane}")
            { UseShellExecute = false });
            return true;
        }
        catch (Exception) { return false; }
    }
}
