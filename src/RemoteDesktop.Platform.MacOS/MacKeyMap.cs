namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// The wire contract carries Windows virtual-key codes; macOS speaks hardware key codes (the kVK_ constants, ANSI
/// layout: a key is named by what it prints on a US keyboard, which is what the viewer sends). Pure data, tested on
/// every operating system.
/// </summary>
public static class MacKeyMap
{
    // CGEventFlags masks
    public const ulong ShiftMask = 0x00020000, ControlMask = 0x00040000, OptionMask = 0x00080000, CommandMask = 0x00100000;

    /// <summary>The macOS key code for a Windows virtual-key code, or -1 if there is none.</summary>
    public static int ToKeyCode(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return Letters[vk - 0x41];
        if (vk is >= 0x30 and <= 0x39) return Digits[vk - 0x30];
        if (vk is >= 0x60 and <= 0x69) return Keypad[vk - 0x60];
        if (vk is >= 0x70 and <= 0x7B) return Function[vk - 0x70];
        if (vk is >= 0x7C and <= 0x7F) return new[] { 0x69, 0x6B, 0x71, 0x6A }[vk - 0x7C]; // F13-F16

        return vk switch
        {
            0x08 => 0x33, // Backspace -> Delete
            0x09 => 0x30, // Tab
            0x0D => 0x24, // Return
            0x10 or 0xA0 => 0x38, // Shift
            0xA1 => 0x3C,         // Right Shift
            0x11 or 0xA2 => 0x3B, // Control
            0xA3 => 0x3E,         // Right Control
            0x12 or 0xA4 => 0x3A, // Alt -> Option
            0xA5 => 0x3D,         // Right Alt -> Right Option
            0x14 => 0x39, // Caps Lock
            0x1B => 0x35, // Escape
            0x20 => 0x31, // Space
            0x21 => 0x74, // Page Up
            0x22 => 0x79, // Page Down
            0x23 => 0x77, // End
            0x24 => 0x73, // Home
            0x25 => 0x7B, // Left
            0x26 => 0x7E, // Up
            0x27 => 0x7C, // Right
            0x28 => 0x7D, // Down
            0x2E => 0x75, // Forward Delete
            0x2D => 0x72, // Insert -> Help (the Mac has no Insert)
            0x5B => 0x37, // Left Windows -> Command
            0x5C => 0x36, // Right Windows -> Right Command
            0x6A => 0x43, // Numpad *
            0x6B => 0x45, // Numpad +
            0x6D => 0x4E, // Numpad -
            0x6E => 0x41, // Numpad .
            0x6F => 0x4B, // Numpad /
            0xBA => 0x29, // ;
            0xBB => 0x18, // =
            0xBC => 0x2B, // ,
            0xBD => 0x1B, // -
            0xBE => 0x2F, // .
            0xBF => 0x2C, // /
            0xC0 => 0x32, // `
            0xDB => 0x21, // [
            0xDC => 0x2A, // \
            0xDD => 0x1E, // ]
            0xDE => 0x27, // '
            _ => -1
        };
    }

    // kVK_ANSI_A..Z
    private static readonly int[] Letters =
    [
        0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06
    ];

    // kVK_ANSI_0..9
    private static readonly int[] Digits = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];

    // kVK_ANSI_Keypad0..9
    private static readonly int[] Keypad = [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C];

    // kVK_F1..F12
    private static readonly int[] Function = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F];

    /// <summary>The modifier flag a modifier key sets while it is held, or 0 for any other key.</summary>
    public static ulong ModifierFlag(int keyCode) => keyCode switch
    {
        0x38 or 0x3C => ShiftMask,
        0x3B or 0x3E => ControlMask,
        0x3A or 0x3D => OptionMask,
        0x37 or 0x36 => CommandMask,
        _ => 0
    };

    /// <summary>Wire mouse buttons (1 left, 2 right, 3 middle) to CoreGraphics buttons (0 left, 1 right, 2 other); -1 for others.</summary>
    public static int ToButton(int wireButton) => wireButton switch { 1 => 0, 2 => 1, 3 => 2, _ => -1 };

    /// <summary>Wheel units (120 = one notch, positive = away from the user) to scroll "lines" (positive = up).</summary>
    public static int ToScrollLines(int delta)
    {
        if (delta == 0) return 0;
        var lines = Math.Clamp((Math.Abs(delta) + 20) / 40, 1, 60); // a notch (120) is three lines; small nudges still move one
        return delta > 0 ? lines : -lines;
    }

    /// <summary>Normalised 0..32767 coordinate to a position in points on a display that is <paramref name="size"/> points wide (or high).</summary>
    public static double ToPoint(int normalized, double size)
    {
        if (size <= 1) return 0;
        var v = Math.Clamp(normalized, 0, 32767);
        return Math.Min(size - 1, v * size / 32768.0);
    }
}
