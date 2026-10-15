using Avalonia.Input;

namespace RemoteDesktop.UI.Avalonia.Services;

/// <summary>Avalonia key → Windows virtual-key code (the wire format hosts on every system understand).</summary>
public static class AvaloniaKeyMap
{
    /// <summary>The virtual-key code for the key, or 0 when it has no usable code.</summary>
    public static int ToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 0x41 + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return 0x30 + (key - Key.D0);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return 0x60 + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24) return 0x70 + (key - Key.F1);

        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,
            Key.Apps => 0x5D,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => 0
        };
    }

    /// <summary>Avalonia's pointer button to the wire button (1 left, 2 right, 3 middle); 0 for extra buttons.</summary>
    public static int ToButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
        _ => 0
    };

    /// <summary>Avalonia wheel delta (about 1.0 per notch) to wheel units (120 per notch).</summary>
    public static int ToWheelUnits(double deltaY) => (int)Math.Round(Math.Clamp(deltaY, -50, 50) * 120);
}
