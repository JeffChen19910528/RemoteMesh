using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// The CoreGraphics / CoreFoundation / ApplicationServices calls the macOS backend needs. Nothing here runs unless a
/// macOS backend is created, so the project builds and its logic is testable on any operating system.
/// </summary>
internal static class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint(double x, double y)
    {
        public double X = x;
        public double Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X, Y, Width, Height;
    }

    // ── Screen ───────────────────────────────────────────────────────────

    [DllImport(CoreGraphics)] public static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] public static extern IntPtr CGDisplayCreateImage(uint display);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetBytesPerRow(IntPtr image);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetBitsPerPixel(IntPtr image);
    [DllImport(CoreGraphics)] public static extern uint CGImageGetBitmapInfo(IntPtr image);
    [DllImport(CoreGraphics)] public static extern IntPtr CGImageGetDataProvider(IntPtr image);
    [DllImport(CoreGraphics)] public static extern IntPtr CGDataProviderCopyData(IntPtr provider);
    [DllImport(CoreGraphics)] public static extern void CGImageRelease(IntPtr image);

    [DllImport(CoreFoundation)] public static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] public static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);

    // ── Permissions ──────────────────────────────────────────────────────

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGRequestScreenCaptureAccess();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool AXIsProcessTrusted();

    // ── Input ────────────────────────────────────────────────────────────

    public const int kCGHIDEventTap = 0;

    [DllImport(CoreGraphics)] public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int mouseType, CGPoint position, int mouseButton);
    [DllImport(CoreGraphics)] public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);
    // The non-variadic form of CGEventCreateScrollWheelEvent: variadic calls do not pass arguments the same way on Apple silicon.
    [DllImport(CoreGraphics)] public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, int units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CoreGraphics)] public static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CoreGraphics)] public static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);
    [DllImport(CoreGraphics)] public static extern void CGEventPost(int tap, IntPtr evt);

    public const int kCGMouseEventClickState = 1;
}
