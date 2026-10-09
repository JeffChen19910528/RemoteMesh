namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// The wire contract carries Windows virtual-key codes; X11 speaks keysyms. This maps one to the other (US layout
/// for the punctuation keys: a key is named by what it prints on a US keyboard, which is what the viewer sends).
/// </summary>
public static class X11KeyMap
{
    /// <summary>The X11 keysym for a Windows virtual-key code, or 0 if there is none.</summary>
    public static uint ToKeysym(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return (uint)(vk + 0x20);           // A-Z -> a-z (shift comes as its own key event)
        if (vk is >= 0x30 and <= 0x39) return (uint)vk;                     // 0-9
        if (vk is >= 0x70 and <= 0x87) return (uint)(0xFFBE + (vk - 0x70)); // F1-F24
        if (vk is >= 0x60 and <= 0x69) return (uint)(0xFFB0 + (vk - 0x60)); // numpad 0-9

        return vk switch
        {
            0x08 => 0xFF08, // Backspace
            0x09 => 0xFF09, // Tab
            0x0D => 0xFF0D, // Enter
            0x10 or 0xA0 => 0xFFE1, // Shift
            0xA1 => 0xFFE2,         // Right Shift
            0x11 or 0xA2 => 0xFFE3, // Ctrl
            0xA3 => 0xFFE4,         // Right Ctrl
            0x12 or 0xA4 => 0xFFE9, // Alt
            0xA5 => 0xFFEA,         // Right Alt
            0x13 => 0xFF13, // Pause
            0x14 => 0xFFE5, // Caps Lock
            0x1B => 0xFF1B, // Escape
            0x20 => 0x0020, // Space
            0x21 => 0xFF55, // Page Up
            0x22 => 0xFF56, // Page Down
            0x23 => 0xFF57, // End
            0x24 => 0xFF50, // Home
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x2C => 0xFF61, // Print Screen
            0x2D => 0xFF63, // Insert
            0x2E => 0xFFFF, // Delete
            0x5B => 0xFFEB, // Left Windows -> Super_L
            0x5C => 0xFFEC, // Right Windows -> Super_R
            0x5D => 0xFF67, // Apps -> Menu
            0x6A => 0xFFAA, // Numpad *
            0x6B => 0xFFAB, // Numpad +
            0x6D => 0xFFAD, // Numpad -
            0x6E => 0xFFAE, // Numpad .
            0x6F => 0xFFAF, // Numpad /
            0x90 => 0xFF7F, // Num Lock
            0x91 => 0xFF14, // Scroll Lock
            0xBA => 0x003B, // ;
            0xBB => 0x003D, // =
            0xBC => 0x002C, // ,
            0xBD => 0x002D, // -
            0xBE => 0x002E, // .
            0xBF => 0x002F, // /
            0xC0 => 0x0060, // `
            0xDB => 0x005B, // [
            0xDC => 0x005C, // \
            0xDD => 0x005D, // ]
            0xDE => 0x0027, // '
            _ => 0
        };
    }

    /// <summary>Wire mouse buttons (1 left, 2 right, 3 middle) to X11 buttons (1 left, 2 middle, 3 right); 0 for others.</summary>
    public static uint ToButton(int wireButton) => wireButton switch { 1 => 1, 2 => 3, 3 => 2, _ => 0 };

    /// <summary>Wheel units (120 = one notch, positive = away from the user) to X11 wheel button clicks: (button, count).</summary>
    public static (uint Button, int Clicks) ToWheel(int delta)
    {
        if (delta == 0) return (0, 0);
        var clicks = Math.Clamp((Math.Abs(delta) + 60) / 120, 1, 20); // a notch is 120; small deltas still move one step
        return (delta > 0 ? 4u : 5u, clicks);
    }

    /// <summary>Normalised 0..32767 coordinate to a pixel on a screen <paramref name="size"/> pixels wide (or high).</summary>
    public static int ToPixel(int normalized, int size)
    {
        if (size <= 1) return 0;
        var v = Math.Clamp(normalized, 0, 32767);
        return (int)Math.Min(size - 1, ((long)v * size) / 32768);
    }
}
