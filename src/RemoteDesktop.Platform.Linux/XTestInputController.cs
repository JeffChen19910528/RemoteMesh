using RemoteDesktop.Input;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// Injects input through the XTEST extension: the X server treats it like a real keyboard and mouse. Best effort by
/// design (an exception would end the whole input channel): a key with no mapping, or a refused call, is counted in
/// <see cref="FailedInjections"/> instead of thrown.
/// </summary>
public sealed class XTestInputController : IInputController, IDisposable
{
    private readonly object _lock = new(); // one connection, used one call at a time
    private readonly IntPtr _display;
    private readonly int _screen, _width, _height;
    private readonly Dictionary<uint, uint> _keycodes = new();
    private long _failed;
    private bool _disposed;

    public XTestInputController()
    {
        _display = X11Session.Open();
        _screen = X11Native.XDefaultScreen(_display);
        _width = X11Native.XDisplayWidth(_display, _screen);
        _height = X11Native.XDisplayHeight(_display, _screen);
    }

    public long FailedInjections => Interlocked.Read(ref _failed);

    public Task InjectAsync(InputEvent e, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;
            switch (e.EventType)
            {
                case InputEventType.MouseMove:
                    Fail(X11Native.XTestFakeMotionEvent(_display, _screen, X11KeyMap.ToPixel(e.X, _width), X11KeyMap.ToPixel(e.Y, _height), UIntPtr.Zero));
                    break;

                case InputEventType.MouseButtonDown:
                case InputEventType.MouseButtonUp:
                {
                    var button = X11KeyMap.ToButton(e.Button);
                    if (button == 0) break;
                    // Position and button together, so the click lands where the viewer pointed.
                    Fail(X11Native.XTestFakeMotionEvent(_display, _screen, X11KeyMap.ToPixel(e.X, _width), X11KeyMap.ToPixel(e.Y, _height), UIntPtr.Zero));
                    Fail(X11Native.XTestFakeButtonEvent(_display, button, e.EventType == InputEventType.MouseButtonDown ? 1 : 0, UIntPtr.Zero));
                    break;
                }

                case InputEventType.MouseWheel:
                {
                    var (button, clicks) = X11KeyMap.ToWheel(e.Delta);
                    for (var i = 0; i < clicks && button != 0; i++)
                    {
                        Fail(X11Native.XTestFakeButtonEvent(_display, button, 1, UIntPtr.Zero));
                        Fail(X11Native.XTestFakeButtonEvent(_display, button, 0, UIntPtr.Zero));
                    }
                    break;
                }

                case InputEventType.KeyDown:
                case InputEventType.KeyUp:
                {
                    var keysym = X11KeyMap.ToKeysym(e.KeyCode);
                    if (keysym == 0) break;
                    if (!_keycodes.TryGetValue(keysym, out var keycode))
                        _keycodes[keysym] = keycode = X11Native.XKeysymToKeycode(_display, (UIntPtr)keysym);
                    if (keycode == 0) { Interlocked.Increment(ref _failed); break; } // this keyboard layout has no such key
                    Fail(X11Native.XTestFakeKeyEvent(_display, keycode, e.EventType == InputEventType.KeyDown ? 1 : 0, UIntPtr.Zero));
                    break;
                }
            }
            X11Native.XFlush(_display);
        }
        return Task.CompletedTask;
    }

    private void Fail(int result)
    {
        if (result == 0) Interlocked.Increment(ref _failed); // XTest returns 0 on failure
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            X11Native.XCloseDisplay(_display);
        }
    }
}
