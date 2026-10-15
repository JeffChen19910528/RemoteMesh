using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux;

/// <summary>The few Xlib / XTest calls the Linux backend needs. Nothing here runs unless a Linux backend is created.</summary>
internal static class X11Native
{
    private const string X11 = "libX11.so.6";
    private const string Xtst = "libXtst.so.6";

    public const int AllPlanes = -1;      // ~0 as an unsigned long: every bit plane
    public const int ZPixmap = 2;

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport(X11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(X11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] public static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XFlush(IntPtr display);
    [DllImport(X11)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] public static extern uint XKeysymToKeycode(IntPtr display, UIntPtr keysym);
    [DllImport(X11)] public static extern int XFree(IntPtr data);
    [DllImport(X11)] public static extern int XDestroyImage(IntPtr image);

    [DllImport(X11)]
    public static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, UIntPtr planeMask, int format);

    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(X11)] public static extern IntPtr XSetErrorHandler(XErrorHandler? handler);

    [DllImport(Xtst)] public static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, UIntPtr delay);
    [DllImport(Xtst)] public static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, UIntPtr delay);
    [DllImport(Xtst)] public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, UIntPtr delay);

    /// <summary>The leading fields of Xlib's XImage (the rest is function pointers we never touch).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public IntPtr Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public UIntPtr RedMask;
        public UIntPtr GreenMask;
        public UIntPtr BlueMask;
    }
}

/// <summary>
/// Connection to the X server shared by the Linux backends: one-time setup (threads, an error handler that does not
/// kill the process) and a clear answer to "is there an X server I can use?".
/// </summary>
internal static class X11Session
{
    private static readonly object Gate = new();
    private static bool _initialised;
    private static X11Native.XErrorHandler? _handler; // kept alive for the lifetime of the process (native code holds the pointer)
    private static long _errors;

    /// <summary>How many protocol errors the X server reported (for example capturing a window that just closed).</summary>
    public static long ErrorCount => Interlocked.Read(ref _errors);

    public static bool HasDisplayVariable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Opens a connection to the display named by <c>DISPLAY</c>; throws a readable error when there is none.</summary>
    public static IntPtr Open()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            throw new PlatformNotSupportedException("The X11 backend only runs on Linux.");
        Initialise();
        if (!HasDisplayVariable)
            throw new PlatformNotSupportedException(
                "No X display: DISPLAY is not set. Start the agent inside a graphical session (X11, or XWayland on Wayland).");
        IntPtr display;
        try { display = X11Native.XOpenDisplay(IntPtr.Zero); }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException("libX11 is not installed (package libx11-6).", ex);
        }
        if (display == IntPtr.Zero)
            throw new PlatformNotSupportedException(
                "Could not connect to the X display" + (IsWaylandSession ? " (a Wayland-only session has no X server to capture; log in with an X11 session or enable XWayland)." : "."));
        return display;
    }

    private static void Initialise()
    {
        lock (Gate)
        {
            if (_initialised) return;
            X11Native.XInitThreads(); // must come before any other Xlib call
            _handler = (_, _) => { Interlocked.Increment(ref _errors); return 0; }; // the default handler calls exit()
            X11Native.XSetErrorHandler(_handler);
            _initialised = true;
        }
    }
}
