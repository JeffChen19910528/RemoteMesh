using RemoteDesktop.Input;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Injects input with CGEvent, which the system treats like a real keyboard and mouse. macOS only allows this for
/// applications the user ticked under "Accessibility" (System Settings → Privacy &amp; Security); without that the
/// events are silently dropped, so <see cref="MacPermissions"/> lets the application tell the user. Best effort by
/// design (an exception would end the whole input channel): a key with no mapping is counted in
/// <see cref="FailedInjections"/> instead of thrown.
/// </summary>
public sealed class CGEventInputController : IInputController
{
    private const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, LeftDragged = 6, RightDragged = 7,
        OtherDown = 25, OtherUp = 26, OtherDragged = 27;

    private readonly object _lock = new();
    private readonly double _width, _height;
    private ulong _flags;          // modifier keys currently held
    private int _buttonsHeld;      // bit 0 left, 1 right, 2 other: moves while a button is held are drags
    private long _failed;

    /// <summary>
    /// When true a Windows Ctrl key acts as the Mac Command key (so Ctrl+C copies), the way people expect when they
    /// control a Mac from Windows. Off by default: keys are passed through as they are named.
    /// </summary>
    public bool CtrlActsAsCommand { get; set; }

    public CGEventInputController()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The CGEvent backend only runs on macOS.");
        var bounds = MacNative.CGDisplayBounds(MacNative.CGMainDisplayID());
        _width = bounds.Width;
        _height = bounds.Height;
    }

    public long FailedInjections => Interlocked.Read(ref _failed);

    public Task InjectAsync(InputEvent e, CancellationToken ct = default)
    {
        lock (_lock)
        {
            switch (e.EventType)
            {
                case InputEventType.MouseMove:
                    PostMouse(MoveType(), e, button: ButtonForDrag());
                    break;

                case InputEventType.MouseButtonDown:
                case InputEventType.MouseButtonUp:
                {
                    var button = MacKeyMap.ToButton(e.Button);
                    if (button < 0) break;
                    var down = e.EventType == InputEventType.MouseButtonDown;
                    var type = button switch { 0 => down ? LeftDown : LeftUp, 1 => down ? RightDown : RightUp, _ => down ? OtherDown : OtherUp };
                    _buttonsHeld = down ? _buttonsHeld | (1 << button) : _buttonsHeld & ~(1 << button);
                    PostMouse(type, e, button);
                    break;
                }

                case InputEventType.MouseWheel:
                {
                    var lines = MacKeyMap.ToScrollLines(e.Delta);
                    if (lines == 0) break;
                    Post(MacNative.CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1 /* lines */, 1, lines, 0, 0));
                    break;
                }

                case InputEventType.KeyDown:
                case InputEventType.KeyUp:
                {
                    var code = KeyCodeFor(e.KeyCode);
                    if (code < 0) { Interlocked.Increment(ref _failed); break; } // this Mac keyboard has no such key
                    var down = e.EventType == InputEventType.KeyDown;
                    var flag = MacKeyMap.ModifierFlag(code);
                    if (flag != 0) _flags = down ? _flags | flag : _flags & ~flag;

                    var evt = MacNative.CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)code, down);
                    if (evt != IntPtr.Zero) MacNative.CGEventSetFlags(evt, _flags); // keep Shift/Command/... on for the keys that follow
                    Post(evt);
                    break;
                }
            }
        }
        return Task.CompletedTask;
    }

    private int KeyCodeFor(int vk)
    {
        var code = MacKeyMap.ToKeyCode(vk);
        if (CtrlActsAsCommand && code is 0x3B) return 0x37;  // Control -> Command
        if (CtrlActsAsCommand && code is 0x3E) return 0x36;  // Right Control -> Right Command
        return code;
    }

    private int MoveType() => (_buttonsHeld & 1) != 0 ? LeftDragged : (_buttonsHeld & 2) != 0 ? RightDragged : (_buttonsHeld & 4) != 0 ? OtherDragged : Moved;

    private int ButtonForDrag() => (_buttonsHeld & 1) != 0 ? 0 : (_buttonsHeld & 2) != 0 ? 1 : (_buttonsHeld & 4) != 0 ? 2 : 0;

    private void PostMouse(int type, InputEvent e, int button)
    {
        var point = new MacNative.CGPoint(MacKeyMap.ToPoint(e.X, _width), MacKeyMap.ToPoint(e.Y, _height));
        var evt = MacNative.CGEventCreateMouseEvent(IntPtr.Zero, type, point, button);
        if (evt != IntPtr.Zero)
        {
            MacNative.CGEventSetFlags(evt, _flags);
            if (type is LeftDown or RightDown or OtherDown) MacNative.CGEventSetIntegerValueField(evt, MacNative.kCGMouseEventClickState, 1);
        }
        Post(evt);
    }

    private void Post(IntPtr evt)
    {
        if (evt == IntPtr.Zero) { Interlocked.Increment(ref _failed); return; }
        try { MacNative.CGEventPost(MacNative.kCGHIDEventTap, evt); }
        finally { MacNative.CFRelease(evt); }
    }
}
