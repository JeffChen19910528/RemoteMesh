using RemoteDesktop.Input;

namespace RemoteDesktop.Platform.Windows;

/// <summary>One low-level input record, independent of the Win32 struct layout so the mapping is testable anywhere.</summary>
public readonly record struct InputSpec(bool IsKey, int X, int Y, int MouseData, uint Flags, ushort VirtualKey, ushort ScanCode);

/// <summary>
/// The wire contract for input, made concrete for Windows:
/// <list type="bullet">
/// <item>X/Y are normalized over the whole primary screen, 0..32767 (so they do not depend on frame scaling).</item>
/// <item>Button: 1 = left, 2 = right, 3 = middle. Other buttons are ignored.</item>
/// <item>KeyCode is a Windows virtual-key code. Modifier keys are sent as their own key events.</item>
/// <item>Delta is in wheel units (120 = one notch).</item>
/// </list>
/// </summary>
public static class InputTranslator
{
    public const int NormalizedMax = 32767;

    // mouse_event flags
    internal const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, ABSOLUTE = 0x8000;

    // keybd_event flags
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;

    private static readonly HashSet<int> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home Left Up Right Down
        0x2D, 0x2E,                                     // Insert Delete
        0x5B, 0x5C, 0x5D,                               // Left Win, Right Win, Apps
        0x6F,                                           // Numpad divide
        0x90,                                           // NumLock
        0xA3, 0xA5                                      // Right Ctrl, Right Alt
    ];

    /// <summary>Maps one event to the records to send; empty if the event has no Windows equivalent.</summary>
    public static IReadOnlyList<InputSpec> Translate(InputEvent e, Func<ushort, ushort> scanCodeOf)
    {
        switch (e.EventType)
        {
            case InputEventType.MouseMove:
                return [Mouse(e, MOVE)];

            case InputEventType.MouseButtonDown:
            case InputEventType.MouseButtonUp:
            {
                var down = e.EventType == InputEventType.MouseButtonDown;
                uint? flag = e.Button switch
                {
                    1 => down ? LEFTDOWN : LEFTUP,
                    2 => down ? RIGHTDOWN : RIGHTUP,
                    3 => down ? MIDDLEDOWN : MIDDLEUP,
                    _ => null
                };
                // Position and button in one record so the click lands where the viewer pointed.
                return flag is null ? [] : [Mouse(e, MOVE | flag.Value)];
            }

            case InputEventType.MouseWheel:
                return [new InputSpec(false, 0, 0, e.Delta, WHEEL, 0, 0)];

            case InputEventType.KeyDown:
            case InputEventType.KeyUp:
            {
                if (e.KeyCode is <= 0 or > 0xFF) return []; // virtual keys are 1..254
                var vk = (ushort)e.KeyCode;
                var flags = (ExtendedKeys.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0)
                            | (e.EventType == InputEventType.KeyUp ? KEYEVENTF_KEYUP : 0);
                return [new InputSpec(true, 0, 0, 0, flags, vk, scanCodeOf(vk))];
            }

            default:
                return [];
        }
    }

    private static int Scale(int v) => (int)(((long)Math.Clamp(v, 0, NormalizedMax) * 65535 + NormalizedMax / 2) / NormalizedMax);

    private static InputSpec Mouse(InputEvent e, uint flags)
    {
        // Windows absolute coordinates span 0..65535 across the primary monitor.
        var x = Scale(e.X);
        var y = Scale(e.Y);
        return new InputSpec(false, x, y, 0, flags | ABSOLUTE, 0, 0);
    }
}
